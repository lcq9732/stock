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

    /// <summary>
    /// 二期（2026-09-13）把 manifest 上那份 MissingBars 换成了按任务分域的 Todos。
    /// 这个 helper 把它摊平回"每段一条"的老形状，好让下面这些判据继续照原样断言——
    /// 它们守的是**体检的行为**（哪些段该报出来、Tries 怎么继承、别的面会不会被误删），
    /// 跟存储换成什么形状无关。
    /// </summary>
    private List<MissingBarRange> Bars() => _manifest.Load().Todos
        .Where(t => t.Kind is RetryTodoKind.Gap or RetryTodoKind.ValueIssue)
        .SelectMany(t => t.Targets.Select(x => new MissingBarRange
        {
            Code = x.Code,
            Granularity = x.Gran ?? Granularity.Day,
            From = x.From ?? default,
            To = x.To ?? default,
            Days = x.Days,
            Tries = x.Tries,
            Reason = x.Reason ?? AuditFindingKind.Gap,
        }))
        .ToList();

    /// <summary>写入前置状态：把几段缺口挂到某个任务名下（体检落账时就是这么存的）。</summary>
    private void SeedGaps(string taskId, params MissingBarRange[] ranges)
    {
        var m = _manifest.Load();
        m.SetTodo(taskId, RetryTodoKind.Gap, ranges.Select(r => new RetryTarget
        {
            Code = r.Code, Gran = r.Granularity,
            From = r.From, To = r.To, Days = r.Days, Tries = r.Tries,
            Reason = r.Reason == AuditFindingKind.Gap ? null : r.Reason,
        }).ToList());
        _manifest.Save(m);
    }

    /// <summary>同上，但挂的是值类记录（行在但值错）。</summary>
    private void SeedValueIssues(string taskId, params MissingBarRange[] ranges)
    {
        var m = _manifest.Load();
        m.SetTodo(taskId, RetryTodoKind.ValueIssue, ranges.Select(r => new RetryTarget
        {
            Code = r.Code, Gran = r.Granularity,
            From = r.From, To = r.To, Days = r.Days, Tries = r.Tries, Reason = r.Reason,
        }).ToList());
        _manifest.Save(m);
    }

    [Fact]
    public async Task 扫个股那个面_不会动掉ETF那个面的旧记录()
    {
        // 个股 600000：前复权缺 9-2（真空洞，本轮该报出来）
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);

        // ETF 的旧记录：这一轮 ETF 一只都没有（名册里没有），所以体检不会碰这个面
        SeedGaps(RetryTaskIds.EtfBars, new MissingBarRange
        {
            Code = "sh510300", Granularity = Granularity.Day,
            From = Days[0], To = Days[0], Days = 1, Tries = 1,
        });

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var after = Bars();
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

        SeedGaps(RetryTaskIds.StockDayBars, new MissingBarRange
        {
            Code = "600000", Granularity = Granularity.Day,
            From = Days[1], To = Days[1], Days = 1, Tries = 1,
        });

        await NewTask().RunAsync(Args(), CancellationToken.None);

        // 空批也要落账——不然"补上了"这个事实永远写不回名单（框架对空批不调 SaveBatchAsync，
        // 所以 ScanScope 必须始终带一条汇总行，见它的注释）
        Assert.DoesNotContain(Bars(),
            r => r.Code == "600000" && r.Granularity == Granularity.Day);
    }

    // ─────────────────── ② Tries 继承 ───────────────────

    [Fact]
    public async Task 同一段再次被扫出来_Tries不能清零()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);   // 缺 9-2

        SeedGaps(RetryTaskIds.StockDayBars, new MissingBarRange
        {
            Code = "600000", Granularity = Granularity.Day,
            From = Days[1], To = Days[1], Days = 1, Tries = 1,   // 已经补过一轮
        });

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var again = Bars()
            .Single(r => r.Code == "600000" && r.Granularity == Granularity.Day);
        Assert.Equal(1, again.Tries);
    }

    [Fact]
    public async Task 不同口径的Tries互不干扰()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);      // 前复权缺 9-2
        Insert("600000", Granularity.DayRaw, Days[0], Days[2]);   // 不复权也缺 9-2

        SeedGaps(RetryTaskIds.StockDayBars, new MissingBarRange
        {
            Code = "600000", Granularity = Granularity.Day,
            From = Days[1], To = Days[1], Days = 1, Tries = 2,
        });

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var after = Bars().Where(r => r.Code == "600000").ToList();
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
        var after = Bars();
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
        Assert.DoesNotContain(Bars(), r => r.Code == "600000");
    }

    // ─────────────────── ⑤ 值体检接入（2026-09-09）───────────────────

    [Fact]
    public async Task 盘中固化的行_进待补名单且带上Reason()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        // 三天都齐（没有缺行），但 9-2 那根是盘中 09:33 抓的
        Insert("600000", Granularity.Day, Days[0], Days[2]);
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = "600000", Granularity = Granularity.Day, PeriodStart = Days[1],
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 100, Turnover = 1,
            FetchedAt = Days[1].AddHours(9).AddMinutes(33),
        }]);

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var hit = Bars()
            .Where(r => r.EffectiveReason == AuditFindingKind.Intraday).ToList();
        Assert.Single(hit);
        Assert.Equal("600000", hit[0].Code);
        Assert.Equal(Days[1], hit[0].From);
        Assert.True(hit[0].IsValueIssue);
    }

    [Fact]
    public async Task 值问题修好后_旧的值类记录被清掉_缺行记录不受影响()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days);          // 这轮完全干净

        // 上一轮报的盘中固化（现在已经修好了，本轮该消失）
        SeedValueIssues(RetryTaskIds.StockDayBars, new MissingBarRange
        {
            Code = "600000", Granularity = Granularity.Day, Reason = AuditFindingKind.Intraday,
            From = Days[1], To = Days[1], Days = 1, Tries = 1,
        });
        // 别的面的缺行记录（值体检一行都不该碰）
        SeedGaps(RetryTaskIds.EtfBars, new MissingBarRange
        {
            Code = "sh510300", Granularity = Granularity.Day, Reason = AuditFindingKind.Gap,
            From = Days[0], To = Days[0], Days = 1, Tries = 2,
        });

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var after = Bars();
        // 值类记录清掉了——这一条靠"汇总行也带 ValueScope"才成立（否则零发现时压根不落账）
        Assert.DoesNotContain(after, r => r.IsValueIssue);
        // 缺行记录原样保留，Tries 也没被动
        var gap = after.Single(r => r.Code == "sh510300");
        Assert.Equal(2, gap.Tries);
        Assert.Equal(AuditFindingKind.Gap, gap.EffectiveReason);
    }

    [Fact]
    public async Task 值类记录的Tries按Reason分别继承()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600000", Granularity.Day, Days[0], Days[2]);      // 缺 9-2 → gap
        _bars.InsertOrRefreshUnconfirmed([new Bar                  // 9-1 是盘中抓的 → intraday
        {
            Code = "600000", Granularity = Granularity.DayRaw, PeriodStart = Days[0],
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 100, Turnover = 1,
            FetchedAt = Days[0].AddHours(10),
        }]);

        SeedValueIssues(RetryTaskIds.StockRawBars, new MissingBarRange
        {
            Code = "600000", Granularity = Granularity.DayRaw, Reason = AuditFindingKind.Intraday,
            From = Days[0], To = Days[0], Days = 1, Tries = 1,
        });

        await NewTask().RunAsync(Args(), CancellationToken.None);

        var intraday = Bars()
            .Single(r => r.EffectiveReason == AuditFindingKind.Intraday);
        Assert.Equal(1, intraday.Tries);      // 继承，没清零
    }

    // ─────────────────── ③ 值问题按"谁能修"定归属（2026-09-29）───────────────────
    // 【重新拉取失败】自己不干活，按待办上记的任务去派。以前值问题只按口径归到个股日K，
    // ETF 只差换手率的也被派去重抓覆盖——把【ETF换手率校正】修好的值冲回去，26 段挂了好几轮。

    private void Put(string code, string gran, DateTime day, double volume = 1000, double turnover = 5,
                     DateTime? fetchedAt = null) =>
        _bars.InsertOrRefreshUnconfirmed([new Bar
        {
            Code = code, Granularity = gran, PeriodStart = day,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = volume, Amount = volume * 100, Turnover = turnover,
            FetchedAt = fetchedAt ?? day.AddHours(20),
        }]);

    /// <summary>一只 ETF：前复权、不复权三天齐全、值一致（不会被报空洞，也没有值问题）。</summary>
    private void CleanEtf(string code)
    {
        Meta(code, SqliteStockMetaUpsert.TypeEtf);
        foreach (var d in Days)
        {
            Put(code, Granularity.Day, d);
            Put(code, Granularity.DayRaw, d);
        }
    }

    /// <summary>带份额仓储和交易日历的体检（能判"ETF 换手率有没有任务能修"）。</summary>
    private FullAuditTask NewTaskWithShares(params (string Market, string Code, DateTime Day, double Wan)[] shares)
    {
        var repo = new SqliteEtfShareRepository(_dbPath);
        repo.EnsureSchema();
        repo.Upsert(shares.Select(s => new EtfShareRow(s.Market, s.Code, DateOnly.FromDateTime(s.Day), s.Wan)).ToList());
        var days = new SqliteTradingDayRepository(_dbPath);
        days.EnsureSchema();
        days.Upsert(Days.Select(d => (DateOnly.FromDateTime(d), "szse")));
        return new FullAuditTask(_dbPath, _manifest, etfShares: repo, tradingDays: days);
    }

    private List<RetryTarget> ValueTodos(string taskId) =>
        _manifest.Load().Todo(taskId, RetryTodoKind.ValueIssue)?.Targets ?? [];

    private static async Task<List<string>> RunLogged(FullAuditTask task)
    {
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        await task.RunAsync(Args(), CancellationToken.None);
        return log;
    }

    [Fact]
    public async Task ETF只差换手率_记在ETF换手率校正名下()
    {
        // ⚠ 特殊那行要先写：已确认的行后写覆盖不了（InsertOrRefreshUnconfirmed）
        Put("sh510150", Granularity.DayRaw, Days[1], turnover: 2);    // 量额一致、只有换手率不同
        CleanEtf("sh510150");

        await RunLogged(NewTaskWithShares(("sh", "510150", Days[0], 100_000)));

        var t = Assert.Single(ValueTodos(RetryTaskIds.EtfTurnoverFix));
        Assert.Equal("sh510150", t.Code);
        Assert.Equal(Granularity.DayRaw, t.Gran);
        Assert.Equal(Days[1], t.From);
        Assert.Equal(AuditFindingKind.EtfTurnover, t.Reason);
        // K线任务名下一条都没有——它们重抓修不好，还会冲掉校正好的值
        Assert.Empty(ValueTodos(RetryTaskIds.StockRawBars));
        Assert.Empty(ValueTodos(RetryTaskIds.EtfBars));
    }

    /// <summary>前一交易日没有官方份额：没有任务能修，只报数、不进名单（否则永远挂着）。</summary>
    [Fact]
    public async Task ETF换手率前一交易日没有份额_只报数不进名单()
    {
        Put("sh510150", Granularity.DayRaw, Days[1], turnover: 2);
        CleanEtf("sh510150");

        var log = await RunLogged(NewTaskWithShares());   // 一条份额都没有

        Assert.DoesNotContain(_manifest.Load().Todos, x => x.Kind == RetryTodoKind.ValueIssue);
        Assert.Contains(log, l => l.Contains("前一交易日没有官方份额") && l.Contains("只报数"));
    }

    [Fact]
    public async Task ETF量额不对_归ETF日K()
    {
        Put("sh510150", Granularity.DayRaw, Days[1], volume: 30);    // 量本身不对，重抓能修
        CleanEtf("sh510150");

        await RunLogged(NewTaskWithShares(("sh", "510150", Days[0], 100_000)));

        var t = Assert.Single(ValueTodos(RetryTaskIds.EtfBars));
        Assert.Equal(AuditFindingKind.Inconsistent, t.Reason);
        Assert.Equal(Granularity.DayRaw, t.Gran);                    // 按每段自己的口径去抓
        Assert.Empty(ValueTodos(RetryTaskIds.StockRawBars));
        Assert.Empty(ValueTodos(RetryTaskIds.EtfTurnoverFix));
    }

    [Fact]
    public async Task 指数的值问题_归指数日K()
    {
        Meta("sz399001", SqliteStockMetaUpsert.TypeIndex);
        Put("sz399001", Granularity.Day, Days[0]);
        Put("sz399001", Granularity.Day, Days[1], fetchedAt: Days[1].AddHours(10));   // 盘中固化
        Put("sz399001", Granularity.Day, Days[2]);

        await RunLogged(NewTask());

        var t = Assert.Single(ValueTodos(RetryTaskIds.IndexBars));
        Assert.Equal("sz399001", t.Code);
        Assert.Equal(AuditFindingKind.Intraday, t.Reason);
        Assert.Empty(ValueTodos(RetryTaskIds.StockDayBars));     // 以前按口径落在这里
    }

    [Fact]
    public async Task 个股只差换手率_仍归个股日K()
    {
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Put("600000", Granularity.DayRaw, Days[1], turnover: 2);     // 先写，理由同上
        foreach (var d in Days)
        {
            Put("600000", Granularity.Day, d);
            Put("600000", Granularity.DayRaw, d);
        }

        await RunLogged(NewTask());

        var t = Assert.Single(ValueTodos(RetryTaskIds.StockRawBars));
        Assert.Equal(AuditFindingKind.Inconsistent, t.Reason);   // 个股换手率差是股本变动，重抓能修
        Assert.Empty(ValueTodos(RetryTaskIds.EtfTurnoverFix));
    }

    /// <summary>
    /// 历史空洞正好是官网记载的全天停牌（2026-09-30）：重抓也拿不到，不进名单——
    /// 以前要白补两轮才被判"数据源确实没有"。不是停牌的那天照样报。
    /// </summary>
    [Fact]
    public async Task 空洞是全天停牌就不进名单_不是停牌的照报()
    {
        Meta("600363", SqliteStockMetaUpsert.TypeStock);
        Meta("600000", SqliteStockMetaUpsert.TypeStock);
        Insert("600363", Granularity.Day, Days[0], Days[2]);    // 9-2 停牌
        Insert("600000", Granularity.Day, Days[0], Days[2]);    // 9-2 真漏了
        new SqliteTradingDayRepository(_dbPath).Upsert(Days.Select(d => (DateOnly.FromDateTime(d), "szse")));
        var d1 = DateOnly.FromDateTime(Days[1]);
        new SqliteSuspensionRepository(_dbPath).Upsert([
            new SuspensionRow(SuspensionSource.SseStock, "sh", "600363", "联创光电", d1, "", d1, "", "LSTP", "WH", "重要公告"),
        ]);

        var log = await RunLogged(NewTask());

        var gaps = Bars().Where(r => r.EffectiveReason == AuditFindingKind.Gap && r.Granularity == Granularity.Day).ToList();
        Assert.Equal("600000", Assert.Single(gaps).Code);
        Assert.Contains(log, l => l.Contains("1 只共 1 天是官网记载的全天停牌"));
    }

    [Fact]
    public async Task 板块指数的值问题_只报数不进名单()
    {
        Meta("BK0001", SqliteStockMetaUpsert.TypeBoard);
        Put("BK0001", Granularity.Day, Days[1], fetchedAt: Days[1].AddHours(10));   // 盘中固化

        var log = await RunLogged(NewTask());

        Assert.DoesNotContain(_manifest.Load().Todos.SelectMany(x => x.Targets), t => t.Code == "BK0001");
        Assert.Contains(log, l => l.Contains("板块指数的 1 段**不进名单**"));
    }
}
