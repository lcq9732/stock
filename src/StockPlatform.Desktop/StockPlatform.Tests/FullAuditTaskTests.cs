using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【全库数据体检】迁到新任务框架之后的落账语义（2026-09-09，见
/// doc/full-audit-task-migration-design.md §3②、§5）。
///
/// 盯的是迁移里**最容易写错、而且写错很安静**的两件事：
/// ① **落账的单位是"面"（标的类型 × 口径），不是整张名单**。同一个口径有三个面（个股/ETF/指数
///    都用 day），整体替换等于扫完个股就把 ETF 和指数的旧记录删了——名单越跑越少，没人会发现。
/// ② **Tries 必须从旧记录继承**。把补过两轮的计数清零，就永远收敛不到"数据源确实没有"，
///    于是每次体检都把全市场的停牌重报一遍。
/// </summary>
public class FullAuditTaskTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _manifestPath;
    private readonly SqliteBarRepository _bars;
    private readonly JsonManifestStore _manifest;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    private static readonly DateTime[] Days =
    [
        new(2026, 9, 1), new(2026, 9, 2), new(2026, 9, 3),
    ];

    public FullAuditTaskTests()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"auditTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        _dbPath = Path.Combine(tmp, "current.sqlite");
        _manifestPath = Path.Combine(tmp, "manifest.json");

        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _manifest = new JsonManifestStore(_manifestPath);

        // 交易日历锚：上证指数的前复权日线（体检写死用它）
        Insert(Anchor, Granularity.Day, Days);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true); } catch { /* 临时目录 */ }
    }

    private void Insert(string code, string gran, params DateTime[] days) =>
        _bars.InsertOrRefreshUnconfirmed(days.Select(d => new Bar
        {
            Code = code, Granularity = gran, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
            // 体检的 cutoff 是"今天减 2 天"，所以造的数据要落在过去；抓取时刻给个盘后时间，
            // 免得将来加 V1（盘中固化）判据时这些行被顺带报出来
            FetchedAt = d.AddHours(20),
        }));

    private void Meta(string code, string type) =>
        SqliteStockMetaUpsert.Upsert(_dbPath, [(code, code)], type);

    private static TaskRunArgs Args(FetchMode mode = FetchMode.Incremental, int? maxItems = null) =>
        new(Mode: mode, MaxItems: maxItems);

    private FullAuditTask NewTask() => new(_dbPath, _manifest);

    // ─────────────────── ① 面级落账 ───────────────────

    [Fact]
    public async Task 扫个股那个面_不会动掉ETF那个面的旧记录()
    {
        // 个股 600000：前复权缺 9-2（真空洞，本轮该报出来）
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);

        // ETF 的旧记录：这一轮 ETF 一只都没有（名册里没有），所以体检不会碰这个面
        var m = _manifest.Load();
        m.MissingBars =
        [
            new MissingBarRange
            {
                Code = "sh510300", Granularity = Granularity.Day,
                From = Days[0], To = Days[0], Days = 1, Tries = 1,
            },
        ];
        _manifest.Save(m);

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var after = _manifest.Load().MissingBars;
        // ETF 那条必须还在（同一个口径 day，但不同的面）
        Assert.Contains(after, r => r.Code == "sh510300" && r.Tries == 1);
        // 个股那条本轮报了出来
        Assert.Contains(after, r => r.Code == "600000" && r.Granularity == Granularity.Day);
    }

    [Fact]
    public async Task 某个面这轮没有空洞_它的旧记录要被清掉()
    {
        // 600000 这轮是齐的
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days);

        var m = _manifest.Load();
        m.MissingBars =
        [
            new MissingBarRange
            {
                Code = "600000", Granularity = Granularity.Day,
                From = Days[1], To = Days[1], Days = 1, Tries = 1,
            },
        ];
        _manifest.Save(m);

        await NewTask().RunAsync(Args(), CancellationToken.None);

        // 空批也要落账——不然"补上了"这个事实永远写不回名单（框架对空批不调 SaveBatchAsync，
        // 所以 ScanScope 必须始终带一条汇总行，见它的注释）
        Assert.DoesNotContain(_manifest.Load().MissingBars,
            r => r.Code == "600000" && r.Granularity == Granularity.Day);
    }

    // ─────────────────── ② Tries 继承 ───────────────────

    [Fact]
    public async Task 同一段再次被扫出来_Tries不能清零()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);   // 缺 9-2

        var m = _manifest.Load();
        m.MissingBars =
        [
            new MissingBarRange
            {
                Code = "600000", Granularity = Granularity.Day,
                From = Days[1], To = Days[1], Days = 1, Tries = 1,   // 已经补过一轮
            },
        ];
        _manifest.Save(m);

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var again = _manifest.Load().MissingBars
            .Single(r => r.Code == "600000" && r.Granularity == Granularity.Day);
        Assert.Equal(1, again.Tries);
    }

    [Fact]
    public async Task 不同口径的Tries互不干扰()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);      // 前复权缺 9-2
        Insert("600000", Granularity.DayRaw, Days[0], Days[2]);   // 不复权也缺 9-2

        var m = _manifest.Load();
        m.MissingBars =
        [
            new MissingBarRange
            {
                Code = "600000", Granularity = Granularity.Day,
                From = Days[1], To = Days[1], Days = 1, Tries = 2,
            },
        ];
        _manifest.Save(m);

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var after = _manifest.Load().MissingBars.Where(r => r.Code == "600000").ToList();
        Assert.Equal(2, after.Single(r => r.Granularity == Granularity.Day).Tries);
        Assert.Equal(0, after.Single(r => r.Granularity == Granularity.DayRaw).Tries);
    }

    // ─────────────────── ③ 分批 / 取消 ───────────────────

    [Fact]
    public async Task MaxItems限一批_只扫一个面就收尾()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);      // 前复权缺
        Insert("600000", Granularity.DayRaw, Days[0], Days[2]);   // 不复权也缺

        await NewTask().RunAsync(Args(maxItems: 1), CancellationToken.None);

        // 第一个面是"个股·前复权"，它落了账；"个股·不复权"这一轮根本没扫到
        var after = _manifest.Load().MissingBars;
        Assert.Contains(after, r => r.Granularity == Granularity.Day);
        Assert.DoesNotContain(after, r => r.Granularity == Granularity.DayRaw);
    }

    [Fact]
    public async Task 名册为空_算作没什么可做()
    {
        var result = await NewTask().RunAsync(Args(), CancellationToken.None);
        Assert.True(result.NothingToDo);
    }

    // ─────────────────── ④ Thorough 模式 ───────────────────

    [Fact]
    public async Task 彻底重查模式_清空确认没有白名单()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);

        var audit = new SqliteMissingBarRepository(_dbPath);
        audit.Confirm([("600000", Days[1])], Granularity.Day, tries: 2);
        Assert.True(audit.ConfirmedCount() > 0);

        await NewTask().RunAsync(Args(FetchMode.Thorough), CancellationToken.None);

        Assert.Equal(0, audit.ConfirmedCount());
    }

    [Fact]
    public async Task 常规模式_不清白名单()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);

        var audit = new SqliteMissingBarRepository(_dbPath);
        audit.Confirm([("600000", Days[1])], Granularity.Day, tries: 2);
        int had = audit.ConfirmedCount();

        await NewTask().RunAsync(Args(), CancellationToken.None);

        Assert.Equal(had, audit.ConfirmedCount());
        // 白名单挡住了那一天，所以这一轮不该再报它
        Assert.DoesNotContain(_manifest.Load().MissingBars, r => r.Code == "600000");
    }
}
