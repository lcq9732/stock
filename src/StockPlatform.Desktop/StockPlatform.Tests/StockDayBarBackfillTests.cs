using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
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
/// 【个股日K·前复权】的「首次整段回补」+ 年份区间（2026-09-22，随【拉取区间数据】改成分派器加，
/// 见 doc/fetch-year-migration-design.md）。
///
/// 盯三件事：
/// ① <b>整段回补不看水位线</b>。缺的往往是**开头**而不是尾巴——日更那根按回看年数只填了最近
///    3 年，按水位线往后续**永远补不到前面那几年**。所以"本地已经是最新"的票也必须进计划。
/// ② <b>年份区间只收窄、不放宽</b>，而且落在窗口外时给出空计划、**一个请求都不发**
///    （判据本身在 <see cref="BackfillWindowRuleTests"/>，这里验它真的接上了）。
/// ③ <b>覆盖重抓</b>只在带 <see cref="TaskRunArgs.OverwriteQfq"/> 时生效。不带的话库里已有的行
///    要原样留着（<see cref="BarWritePlanner"/> 判它"值也对得上"就跳过）——这正是
///    "补历史不该动已有数据"和"抹接缝要整段重写"的分界。
///
/// 真 SQLite + 离线模拟源（<see cref="MockBarFetcher"/>）：一个请求都不发，走的是产品代码的落库路径。
/// </summary>
public class StockDayBarBackfillTests : IDisposable
{
    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly IManifestStore _manifest;

    /// <summary>
    /// 库里已有行的成交量标记。**用量不用价**来判"这一行有没有被重写"：
    /// 价格一旦跟模拟源不同就会被 <see cref="BarWritePlanner.IsDrifted"/> 判成复权基准漂移、
    /// 无论带不带覆盖开关都会被重写——那样就分不出"覆盖重抓"和"漂移修复"两条路了。
    /// 所以收盘价照抄模拟源（不漂移），只让成交量不同。
    /// </summary>
    private const double MarkerVolume = 100;

    public StockDayBarBackfillTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"dayBackfill_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>把这只票放进本地名册（默认是在市个股）。</summary>
    private void Roster(params string[] codes)
        => SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, codes.Select(c => (c, "测试" + c)));

    /// <summary>把这只票标成**退市股**。</summary>
    private void Delisted(params string[] codes)
        => SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, codes.Select(c => (c, "退市" + c)),
                                        SqliteStockMetaUpsert.TypeDelisted);

    /// <summary>
    /// 给这只票铺一段**已经追到今天**的日线：最后一根是今天、且"收盘后确认"过
    /// （<see cref="IncrementalWindowCalculator.IsConfirmedFinal"/>），所以增量那一路会判它已是最新。
    /// 价格照抄模拟源（不制造漂移），成交量用 <see cref="MarkerVolume"/> 标记。
    /// </summary>
    private void AlreadyUpToDate(string code, int days = 5)
    {
        var rows = new List<Bar>();
        for (int i = days; i >= 0; i--)
        {
            var d = DateTime.Today.AddDays(-i);
            rows.Add(new Bar
            {
                Code = code, Granularity = Granularity.Day, PeriodStart = d,
                Open = MockBarFetcher.Price, Close = MockBarFetcher.Price,
                High = MockBarFetcher.Price, Low = MockBarFetcher.Price,
                Volume = MarkerVolume, Amount = MarkerVolume * MockBarFetcher.Price, Turnover = 1.5,
                // 今天 20:00 ≥ 今天 16:00 ⇒ 已确认，增量会从"明天"起算、于是整只跳过
                FetchedAt = d.AddHours(20),
            });
        }
        _bars.InsertOrRefreshUnconfirmed(rows);
    }

    /// <summary>这只票在这一天的成交量；<see cref="MarkerVolume"/> 就是"这一行没被重写过"。</summary>
    private double? VolumeOn(string code, DateTime day)
        => Stored(code).FirstOrDefault(b => b.PeriodStart.Date == day.Date)?.Volume;

    private async Task<(TaskRunResult Result, List<string> Log)> RunAsync(TaskRunArgs args)
    {
        var source = new NamedBarSource(
            "Mock", new MockBarFetcher(), new MockStockListProvider(() => []));
        var task = new StockDayBarTask(_paths, new BarSourceHolder(source), _manifest, batchSize: 30);
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(args, CancellationToken.None);
        return (result, log);
    }

    private List<Bar> Stored(string code) => _bars.Query(code, Granularity.Day);

    // ── ① 整段回补不看水位线 ──

    /// <summary>⭐ 本地已经是最新的票，整段回补**照样要抓**——缺的是开头不是尾巴。</summary>
    [Fact]
    public async Task 整段回补_本地已是最新的票也照抓()
    {
        Roster("600000");
        AlreadyUpToDate("600000");
        int before = Stored("600000").Count;

        var (result, log) = await RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill,
                                                          YearStart: DateTime.Today.Year - 1));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.False(result.NothingToDo, "整段回补不该因为'水位线已追上'就说没事干");
        Assert.True(Stored("600000").Count > before, "该把前面缺的那段补进来");
        Assert.Contains(log, l => l.Contains("整段回补"));
    }

    /// <summary>对照：同样的库，**增量**模式下这只票会被判成已是最新、一个请求都不发。</summary>
    [Fact]
    public async Task 增量_本地已是最新就跳过()
    {
        Roster("600000");
        AlreadyUpToDate("600000");

        var (result, _) = await RunAsync(new TaskRunArgs(Mode: FetchMode.Incremental));

        Assert.True(result.NothingToDo);
    }

    /// <summary>
    /// ⭐ 整段回补**必须带上退市股**，否则就是静默的回测幸存者偏差。
    ///
    /// 日更不轮询退市股是对的（数据源早就不给新数据，那几百个请求必然落空），
    /// 但往回补历史时它们必须在——老【拉取区间数据】为此专门有一段
    /// （<c>FetchDelistedForRangeAsync</c>，注释写明"消除回测幸存者偏差"）。
    /// ⚠ 坑在 <c>SqliteStockMetaUpsert.GetAll</c> 只返回 type='stock'，
    /// 照它取名册就会整批漏掉退市股，而且一声不吭。
    /// </summary>
    [Fact]
    public async Task 整段回补_退市股也要补()
    {
        Roster("600000");
        Delisted("600001");

        await RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill,
                                       YearStart: DateTime.Today.Year - 1));

        Assert.NotEmpty(Stored("600001"));
    }

    /// <summary>对照：**增量**那一路不该去轮询退市股（它们不会再有新数据）。</summary>
    [Fact]
    public async Task 增量_不轮询退市股()
    {
        Roster("600000");
        Delisted("600001");

        await RunAsync(new TaskRunArgs(Mode: FetchMode.Incremental));

        Assert.Empty(Stored("600001"));
    }

    // ── ② 年份区间 ──

    /// <summary>
    /// 年份区间真的收窄了抓取窗口。
    ///
    /// ⚠ 断言不能写成"所有行都在那一年里"：真要发请求时，窗口会**向前**放宽
    /// <see cref="BarWritePlanner.DriftCheckLookbackDays"/> 天去取漂移比对样本
    /// （数据源一页固定返回 640 根，放宽不多花请求）。所以前边界是"不早于 起始年−400 天"，
    /// 后边界才是硬的——**绝不能晚于结束年**，往未来要数据只会拿回空。
    /// </summary>
    [Fact]
    public async Task 年份区间_收窄抓取窗口()
    {
        Roster("600000");
        int year = DateTime.Today.Year - 1;

        await RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill, YearStart: year, YearEnd: year));

        var got = Stored("600000");
        Assert.NotEmpty(got);
        var lastDay = new DateTime(year, 12, 31);
        var firstAllowed = new DateTime(year, 1, 1).AddDays(-BarWritePlanner.DriftCheckLookbackDays);
        Assert.All(got, b => Assert.True(b.PeriodStart <= lastDay,
            $"不该抓到 {year} 年之后的 {b.PeriodStart:yyyy-MM-dd}"));
        Assert.All(got, b => Assert.True(b.PeriodStart >= firstAllowed,
            $"比漂移比对窗口还早：{b.PeriodStart:yyyy-MM-dd}"));
    }

    /// <summary>⭐ 区间整段落在未来 → 空计划、一个请求都不发，而且**不算失败**。</summary>
    [Fact]
    public async Task 年份区间落在窗口外_不发请求也不算失败()
    {
        Roster("600000");

        var (result, _) = await RunAsync(new TaskRunArgs(
            Mode: FetchMode.FirstBackfill,
            YearStart: DateTime.Today.Year + 5, YearEnd: DateTime.Today.Year + 6));

        Assert.Equal(TaskState.Completed, result.State);
        Assert.True(result.NothingToDo);
        Assert.Empty(Stored("600000"));
    }

    // ── ②′ 已探明的水位 ──

    /// <summary>
    /// ⭐ 数据源已探明"这天之前没有更早数据"的票，整段回补**整只跳过、不发请求**。
    ///
    /// 这张表（<c>BarProbeFloor</c>）就是为这件事建的：没有它，"那些年还没上市"的票每轮
    /// 都要重新试一遍——2026-09-07 实测一万六千个请求、四个半小时、写入为零。
    /// 迁到新框架时这张表一度没人读了（新任务都不碰它），这条测试钉住它确实接回来了。
    /// </summary>
    [Fact]
    public async Task 水位已探明没有更早数据的票_整只跳过()
    {
        Roster("600000");
        var floor = new SqliteBarProbeFloorRepository(_paths.CurrentDb);
        floor.EnsureSchema();
        // 探明"今天之前都没有" ⇒ 无论要补哪一年，都没什么可抓的
        floor.Record([("600000", DateTime.Today.AddDays(1))], Granularity.Day);

        var (result, _) = await RunAsync(new TaskRunArgs(
            Mode: FetchMode.FirstBackfill, YearStart: DateTime.Today.Year - 1));

        Assert.Empty(Stored("600000"));
        Assert.True(result.NothingToDo);
    }

    /// <summary>
    /// 「覆盖重抓」那一路**连水位也不看**：它的语义是"不看本地已有什么、整段按当前基准重写"，
    /// 被水位跳过的话抹接缝的活就白做了。
    /// </summary>
    [Fact]
    public async Task 覆盖重抓不看水位()
    {
        Roster("600000");
        var floor = new SqliteBarProbeFloorRepository(_paths.CurrentDb);
        floor.EnsureSchema();
        floor.Record([("600000", DateTime.Today.AddDays(1))], Granularity.Day);

        await RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill,
                                       YearStart: DateTime.Today.Year - 1, OverwriteQfq: true));

        Assert.NotEmpty(Stored("600000"));
    }

    // ── ③ 覆盖重抓 ──

    /// <summary>
    /// 不带覆盖开关：库里已有、**且值对得上**的行原样留着——补历史不该动已有数据。
    /// （值对不上那种是复权基准漂移，归 <see cref="BarWritePlanner"/> 判，不在这条的范围里。）
    /// </summary>
    [Fact]
    public async Task 不带覆盖开关_已有的行不动()
    {
        Roster("600000");
        AlreadyUpToDate("600000");
        var day = DateTime.Today.AddDays(-1);

        await RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill,
                                       YearStart: DateTime.Today.Year - 1));

        Assert.Equal(MarkerVolume, VolumeOn("600000", day));
    }

    /// <summary>⭐ 带覆盖开关：整段按数据源当前基准重写——这正是"抹平复权基准接缝"要的效果。</summary>
    [Fact]
    public async Task 带覆盖开关_已有的行被整段重写()
    {
        Roster("600000");
        AlreadyUpToDate("600000");
        var day = DateTime.Today.AddDays(-1);

        await RunAsync(new TaskRunArgs(Mode: FetchMode.FirstBackfill,
                                       YearStart: DateTime.Today.Year - 1, OverwriteQfq: true));

        Assert.NotEqual(MarkerVolume, VolumeOn("600000", day));
    }

    /// <summary>覆盖开关**只在整段回补那一路**有意义：增量模式下不该把历史重写掉。</summary>
    [Fact]
    public async Task 增量模式下覆盖开关不生效()
    {
        Roster("600000");
        AlreadyUpToDate("600000");
        var day = DateTime.Today.AddDays(-1);

        await RunAsync(new TaskRunArgs(Mode: FetchMode.Incremental, OverwriteQfq: true));

        Assert.Equal(MarkerVolume, VolumeOn("600000", day));
    }
}
