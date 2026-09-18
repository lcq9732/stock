using Microsoft.Data.Sqlite;
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
/// 【指数成分】【指数权重】【股票名册与流通市值】三个任务（2026-09-18 从编排器迁过来，
/// 见 doc/index-roster-task-design.md）。
///
/// 盯的是搬家时最容易丢的三条：
/// ① 指数成分：**空结果不算失败**（新浪对某些老指数本来就没有成分）；
/// ② 指数权重：**两道筛子**（本地这一期还新鲜 / 确认没有权重文件），丢了就是每轮
///    拿四五百个注定 404 的请求去撞中证的反爬；
/// ③ 市值：**失败粒度是一轮**，而且 as_of_date 记的是"值属于哪个交易日"。
/// </summary>
public class IndexRosterTaskTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteIndexRepository _indexRepo;
    private readonly SqliteFundamentalMetricRepository _fundamentals;
    private readonly SqliteTradingDayRepository _cal;
    private readonly JsonManifestStore _manifest;

    public IndexRosterTaskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"idxTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _indexRepo = new SqliteIndexRepository(_paths.CurrentDb);
        _indexRepo.EnsureSchema();
        _fundamentals = new SqliteFundamentalMetricRepository(_paths.CurrentDb);
        _fundamentals.EnsureSchema();
        _cal = new SqliteTradingDayRepository(_paths.CurrentDb);
        _cal.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    private List<string> FailedTodo(string taskId) =>
        (_manifest.Load().Todo(taskId, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();

    // ── 指数成分 ──────────────────────────────────────────────────

    /// <summary>
    /// **空结果不算失败**。新浪对某些老指数本来就没有成分——记成失败的话，
    /// 那些指数会永远躺在名单里、每轮都白抓一次。
    /// </summary>
    [Fact]
    public async Task 指数成分_空结果不进失败名单()
    {
        var all = IndexCatalog.All.Select(i => i.Code).ToList();
        var p = new MockIndexConsProvider { EmptyFor = [.. all.Take(3)] };
        var r = await new IndexConsTask(p, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental, MaxItems: 5), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Empty(FailedTodo(RetryTaskIds.IndexCons));
    }

    [Fact]
    public async Task 指数成分_失败的进名单_下轮只补它()
    {
        var first = IndexCatalog.All[0].Code;
        var p = new MockIndexConsProvider { Throws = [first] };
        await new IndexConsTask(p, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental, MaxItems: 3), CancellationToken.None);
        Assert.Contains(first, FailedTodo(RetryTaskIds.IndexCons));

        // 只补待办：只问名单里那个
        var p2 = new MockIndexConsProvider();
        await new IndexConsTask(p2, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);
        Assert.Empty(FailedTodo(RetryTaskIds.IndexCons));
    }

    /// <summary>732 个的轮次要能分批跑——这正是迁移带来的。</summary>
    [Fact]
    public async Task 指数成分_MaxItems只跑那么多个()
    {
        var p = new CountingConsProvider();
        await new IndexConsTask(p, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental, MaxItems: 4), CancellationToken.None);

        Assert.Equal(4, p.Count);
    }

    private sealed class CountingConsProvider : IIndexConsProvider
    {
        public event Action<string>? OnStatus;
        public int Count;
        public Task<List<(string Code, DateTime? InDate)>> GetConsAsync(string indexCode, CancellationToken ct = default)
        {
            OnStatus?.Invoke("");
            Count++;
            return Task.FromResult(new List<(string, DateTime?)> { ("600000", null) });
        }
    }

    // ── 指数权重 ──────────────────────────────────────────────────

    /// <summary>
    /// **筛子①**：本地这一期还新鲜（25 天内）就不再问。
    /// 先跑一轮把权重落库，第二轮应该一个都不问。
    /// </summary>
    [Fact]
    public async Task 指数权重_本地这一期还新鲜就不再问()
    {
        // 基准日取今天，保证落在 FreshDays 窗口内
        var p = new MockIndexWeightProvider { NoFileFor = _ => false };
        await new IndexWeightTask(p, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental, MaxItems: 3), CancellationToken.None);

        var p2 = new CountingWeightProvider();
        var r = await new IndexWeightTask(p2, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        // 抓过的那几个被筛掉了（其余的没抓过，仍会问——所以只断言"抓过的没再问"）
        Assert.True(p2.Asked.Count < IndexCatalog.All.Count);
    }

    /// <summary>
    /// **筛子②**：确认过"没有权重文件"的，30 天内不再问；而且那份名单**不在 Todos 里**，
    /// 是 `Manifest.IndexWeightMissing`。
    /// </summary>
    [Fact]
    public async Task 指数权重_没有文件的记进独立名单_不算失败()
    {
        var p = new MockIndexWeightProvider { NoFileFor = _ => true };   // 全都 404
        var r = await new IndexWeightTask(p, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental, MaxItems: 3), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Empty(FailedTodo(RetryTaskIds.IndexWeight));            // 404 不是失败
        Assert.NotEmpty(_manifest.Load().IndexWeightMissing);          // 记在独立名单里

        // ⚠ 名单要在第二轮**之前**取：第二轮问过的（也返回空）跑完也会进这份名单，
        //   跑完再读就分不清"被筛掉的"和"这轮刚记进去的"了。
        var missed = _manifest.Load().IndexWeightMissing.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(missed);

        // 第二轮：第一轮记下的那些应该被筛子②挡掉
        var p2 = new CountingWeightProvider();
        await new IndexWeightTask(p2, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        Assert.DoesNotContain(p2.Asked, a => missed.Contains(a));
    }

    /// <summary>抓到权重了就把"没有文件"的记录撤掉（中证补上了文件的情况）。</summary>
    [Fact]
    public async Task 指数权重_后来有文件了就撤销记录()
    {
        var code = IndexCatalog.All[0].Code;
        var m = _manifest.Load();
        m.IndexWeightMissing[code] = DateTime.Today.AddDays(-40);   // 已过 30 天，会再问一次
        _manifest.Save(m);

        var p = new MockIndexWeightProvider { NoFileFor = c => c != code };
        await new IndexWeightTask(p, _indexRepo, _manifest)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental, MaxItems: 2), CancellationToken.None);

        Assert.DoesNotContain(code, _manifest.Load().IndexWeightMissing.Keys);
    }

    private sealed class CountingWeightProvider : IIndexWeightProvider
    {
        public event Action<string>? OnStatus;
        public readonly List<string> Asked = [];
        public Task<List<IndexWeightRow>> GetWeightsAsync(string indexCode, CancellationToken ct = default)
        {
            OnStatus?.Invoke("");
            Asked.Add(indexCode);
            return Task.FromResult(new List<IndexWeightRow>());
        }
    }

    // ── 名册与流通市值 ────────────────────────────────────────────

    /// <summary>一轮扫回全市场：市值落库、名册刷新、新股入库。</summary>
    [Fact]
    public async Task 市值_一轮扫回全市场并发现新股()
    {
        var r = await NewRosterTask(new MockMarketCapFetcher()).RunAsync(
            new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        var codes = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Select(s => s.Code).ToList();
        Assert.Contains("600999", codes);        // 模拟源带回来的"新股"
    }

    /// <summary>
    /// **as_of_date 记的是"值属于哪个交易日"**：盘后跑（QuotesAreLive=false）时，
    /// 快照的基准价是上一个交易日的收盘——2026-09-18 起先问本地交易日历。
    /// </summary>
    [Fact]
    public async Task 市值_盘后跑归到日历里的最近交易日()
    {
        var lastTradingDay = DateTime.Today.AddDays(-3);
        _cal.Upsert([(DateOnly.FromDateTime(lastTradingDay), ITradingDayRepository.SzseSource)]);

        await NewRosterTask(new MockMarketCapFetcher { QuotesAreLive = false }).RunAsync(
            new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        var asOf = AsOfDates();
        Assert.Equal([lastTradingDay.Date], asOf);
    }

    /// <summary>行情是实时的（今天已开盘）＝值就是当日的，记今天。</summary>
    [Fact]
    public async Task 市值_盘中跑记今天()
    {
        await NewRosterTask(new MockMarketCapFetcher { QuotesAreLive = true }).RunAsync(
            new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal([DateTime.Today], AsOfDates());
    }

    /// <summary>
    /// **失败粒度是一轮**：整轮扫描失败 → 整项判失败，并把这批代码整体记进 `round` 待办。
    /// </summary>
    [Fact]
    public async Task 市值_整轮失败_记成一轮待办()
    {
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [("600000", "票A"), ("000001", "票B")]);

        var r = await NewRosterTask(new MockMarketCapFetcher { Throws = true }).RunAsync(
            new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
        var round = (_manifest.Load().Todo(RetryTaskIds.Roster, RetryTodoKind.Round)?.Targets ?? [])
            .Select(t => t.Code).ToList();
        Assert.Equal(2, round.Count);
    }

    /// <summary>
    /// 补待办＝重来一轮；成功之后名单整体清空。
    ///
    /// ⚠ 待办里的代码**故意不在本地名册里**：这一轮"碰过的"必须取待办里那批，
    /// 取当前名册的话它们永远移不出去、待办再也不会清空（2026-09-18 实机验证抓出来的）。
    /// </summary>
    [Fact]
    public async Task 市值_补待办就是重来一轮()
    {
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [("600000", "票A")]);
        var m = _manifest.Load();
        m.SetTodo(RetryTaskIds.Roster, RetryTodoKind.Round,
                  [new RetryTarget { Code = "600000" }, new RetryTarget { Code = "900001" }]);
        _manifest.Save(m);

        await NewRosterTask(new MockMarketCapFetcher()).RunAsync(
            new TaskRunArgs(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(_manifest.Load().Todo(RetryTaskIds.Roster, RetryTodoKind.Round)?.Targets ?? []);
    }

    private RosterMarketCapTask NewRosterTask(IMarketCapFetcher cap) =>
        new(cap, _fundamentals, _cal, new MockStockListProvider(() => []),
            new MockBarFetcher(), _manifest, _paths);

    private List<DateTime> AsOfDates()
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT as_of_date FROM FundamentalMetric;";
        var list = new List<DateTime>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(DateTime.Parse(rd.GetString(0)));
        return list;
    }
}
