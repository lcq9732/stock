using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【分档资金流·补历史】的行为（2026-09-11 迁成新式任务；2026-09-12 拆掉快照那一半，
/// 快照的测试在 MoneyFlowSnapshotTaskTests）。
///
/// ════ 为什么有这个文件 ════
/// 迁移的直接动因是一次**误判卡死**：老实现每 100 只才报一句进度，那轮待办只剩 72 只——
/// 一句都报不出来，而 push2his 慢（5 秒间隔、每 15 个请求歇 2 分钟、单只失败还要静默重试），
/// 于是必然哑过 5 分钟，被静默看门狗掐断。掐断不丢数据，但这一项每天记一次失败。
/// 所以第一条测试钉的就是：**待办不足 100 只时，每一只都要有进度出来**。
/// 排队判据本身的测试在下面那个 <see cref="MoneyFlowBackfillPlanTests"/>。
/// </summary>
public class MoneyFlowBackfillTaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mfTask_{Guid.NewGuid():N}");
    private readonly FetchPaths _paths;
    private readonly SqliteNetInflowDetailRepository _repo;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteTradingDayRepository _calendar;

    /// <summary>窗口里的那几天（都在今天之前）。最新那一个交易日不算进期望，所以额外造一天。</summary>
    private static readonly DateTime[] Days =
        [.. Enumerable.Range(1, 6).Select(i => DateTime.Today.AddDays(-i)).OrderBy(d => d)];

    public MoneyFlowBackfillTaskTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteNetInflowDetailRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _calendar = new SqliteTradingDayRepository(_paths.CurrentDb);
        _calendar.EnsureSchema();
        _calendar.Upsert(Days.Select(d => (DateOnly.FromDateTime(d), "szse")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ───────────────────────── 假服务端 ─────────────────────────

    /// <summary>按股票代码给固定报文的假 push2his；记下被问过哪些代码。</summary>
    private sealed class PerStockHandler(Func<string, string> body) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var secid = System.Text.RegularExpressions.Regex
                .Match(request.RequestUri!.Query, @"secid=\d\.(\d+)").Groups[1].Value;
            Asked.Add(secid);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body(secid)),
            });
        }
    }

    /// <summary>一只票的一天：日期 + 主力/小/中/大/超大净额 + 五个占比 + 收盘价 + 涨跌幅。</summary>
    private static string Kline(DateTime day) =>
        $"\"{day:yyyy-MM-dd},100,-70,-30,40,60,1.0,-0.7,-0.3,0.4,0.6,10.0,1.0\"";

    private static string Body(params DateTime[] days) =>
        "{\"rc\":0,\"data\":{\"code\":\"x\",\"klines\":["
        + string.Join(",", days.Select(Kline)) + "]}}";

    private static string Empty() => "{\"rc\":100,\"data\":null}";

    private static EastMoneyMoneyFlowProvider NewPerStock(PerStockHandler handler) =>
        new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new HttpClient(handler));

    // ───────────────────────── 造数据 ─────────────────────────

    private void Roster(params string[] codes) =>
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, codes.Select(c => (c, c)));

    /// <summary>给这只票造日K——它就是资金流的**期望行数**。</summary>
    private void Bars(string code, params DateTime[] days) =>
        _bars.InsertOrRefreshUnconfirmed(days.Select(d => new Bar
        {
            Code = code, Granularity = MoneyFlowBackfillPlan.ExpectGranularity, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
            FetchedAt = d.AddHours(20),
        }));

    private void Flow(string code, params DateTime[] days) =>
        _repo.Upsert(days.Select(d => new NetInflowDetail
        {
            Code = code, TradeDate = d, FetchedAt = d.AddHours(15),
        }));

    /// <summary>跑一轮，把广播出来的进度接住。</summary>
    private async Task<(TaskRunResult Result, List<TaskProgress> Progress)> RunAsync(
        EastMoneyMoneyFlowProvider perStock,
        TaskRunArgs? args = null)
    {
        var task = new MoneyFlowBackfillTask(_paths, _repo, perStock,
                                             progressInterval: TimeSpan.Zero);
        var seen = new List<TaskProgress>();
        task.OnProgress += p => seen.Add(p);
        var result = await task.RunAsync(args ?? new TaskRunArgs(), CancellationToken.None);
        return (result, seen);
    }

    /// <summary>三只票，各自窗口内 5 根日K、资金流一行都没有＝各缺 5 行，都得排队。</summary>
    private void ThreeStocksAllMissing()
    {
        Roster("000001", "000002", "600519");
        foreach (var code in new[] { "000001", "000002", "600519" }) Bars(code, Days);
    }

    // ───────────────────────── 测试 ─────────────────────────

    [Fact]
    public async Task 待办不足一百只也要每只都报进度()
    {
        // 这正是被误判卡死的那个场景：待办 3 只，老实现（每 100 只一句）全程哑火。
        ThreeStocksAllMissing();
        var handler = new PerStockHandler(_ => Body(Days));

        var (result, progress) = await RunAsync(NewPerStock(handler));

        var steps = progress.Where(p => p.Done is > 0 && p.Total == 3).Select(p => p.Done).ToList();
        Assert.Equal([1, 2, 3], steps);
        Assert.Empty(result.Errors);
        Assert.False(result.NothingToDo);
    }

    [Fact]
    public async Task 抓一只存一只_中途停下也留得住已抓的()
    {
        // 骨架是"流式落库"：MaxItems 到点时前面几只已经在库里了，不是全轮白跑。
        ThreeStocksAllMissing();
        var handler = new PerStockHandler(_ => Body(Days[0], Days[1]));

        var (result, _) = await RunAsync(NewPerStock(handler), args: new TaskRunArgs(MaxItems: 2));

        Assert.Equal(2, handler.Asked.Count);          // 第三只根本没去问
        Assert.Equal(2, _repo.CountCodes());           // 前两只整只落库了
        Assert.Equal(4, _repo.Count());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task 接口返回空的票不算成功也不落库()
    {
        // 空返回既不是成功也不是失败。它要是混进"成功"里，920 开头那 342 只
        // secid 拼错的票就又会静默消失（2026-09-06 埋了两天的那个坑）。
        ThreeStocksAllMissing();
        var handler = new PerStockHandler(code => code == "600519" ? Empty() : Body(Days));

        var (result, progress) = await RunAsync(NewPerStock(handler));

        Assert.Equal(2, _repo.CountCodes());
        Assert.Contains(progress, p => p.Text.Contains("接口没数据 1"));
        // 零星几只空返回是常态（停牌/次新），不该报错；只有**大面积**空返回才是请求拼错了
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task 全市场都返回空要喊一声()
    {
        // 大面积空返回不是"这些票没数据"，是我们请求拼错了。不抛异常，所以不主动报就没人知道。
        Roster("000001", "000002");
        Bars("000001", Days); Bars("000002", Days);
        var handler = new PerStockHandler(_ => Empty());

        var (result, _) = await RunAsync(NewPerStock(handler));

        Assert.Contains(result.Errors, e => e.Contains("只接口返回空"));
    }

    [Fact]
    public async Task 次新股不再永远排队()
    {
        // 2026-09-11 查实的误报：老判据"不足 100 行"对上市不足 100 个交易日的票永远不可达，
        // 那 72 只次新股每轮都被重抓、待办数永不归零。现在期望按本地日K算，它们齐了就出队。
        Roster("301999");
        Bars("301999", Days[3], Days[4]);      // 刚上市，窗口内只有 2 根日K
        Flow("301999", Days[3], Days[4]);      // 资金流也是这 2 天——它其实齐了
        var handler = new PerStockHandler(_ => Body(Days));

        var (result, _) = await RunAsync(NewPerStock(handler));

        Assert.Empty(handler.Asked);
        Assert.True(result.NothingToDo);
    }

    [Fact]
    public async Task 缺一两行不补但要报出来()
    {
        // 「增量」门槛是 3 行：09-09 那种全市场缺一天的情形，补一遍是 5500 个请求、约 20 小时，
        // 所以只报不补。⚠ 报这一句是硬要求——"待办 0"很容易被读成"一行不缺"。
        Roster("000001");
        Bars("000001", Days);
        Flow("000001", Days[0], Days[1], Days[2], Days[3]);   // 窗口 5 天里缺 1 天
        var handler = new PerStockHandler(_ => Body(Days));

        var (result, progress) = await RunAsync(NewPerStock(handler));

        Assert.Empty(handler.Asked);
        Assert.Contains(progress, p => p.Text.Contains("另有 1 只各缺") && p.Text.Contains("首次整段回补"));
        Assert.True(result.NothingToDo);
    }

    [Fact]
    public async Task 每轮只问上限那么多只()
    {
        // push2his 累计 16~35 个请求就被切，所以一轮只问 MaxPerRun 只、慢慢补（2026-09-12 用户定）。
        // 截的是「问几只」不是「成功几只」：限流限的是请求数，失败的那几只照样花掉了配额。
        Roster("000001", "000002", "600519");
        foreach (var code in new[] { "000001", "000002", "600519" }) Bars(code, Days);
        var handler = new PerStockHandler(_ => Body(Days));

        var (result, _) = await RunAsync(NewPerStock(handler),
                                         new TaskRunArgs(MaxItems: 2));

        Assert.Equal(2, handler.Asked.Count);                 // 只问了 2 只，第 3 只留给下轮
        Assert.Empty(result.Errors);                          // 没抓完不是错误，是设计如此
        Assert.False(result.NothingToDo);
    }

    [Fact]
    public void 每轮上限要跟估时对得上()
    {
        // 估时是空闲调度判断"这段空档塞不塞得下"的依据。写成全量耗时（原来是 3 小时）
        // 等于让这一项永远排不上队——而它现在一轮只做 30 只、几分钟就完。
        var est = FetchTaskCatalog.Info(FetchActionId.FetchMoneyFlowDetail).Estimate;
        Assert.True(est <= TimeSpan.FromMinutes(15),
            $"每轮上限 {MoneyFlowBackfillPlan.MaxPerRun} 只，估时却写了 {est}——空闲调度会塞不进去");
    }

    [Fact]
    public async Task 首次整段回补模式缺一行也补()
    {
        Roster("000001");
        Bars("000001", Days);
        Flow("000001", Days[0], Days[1], Days[2], Days[3]);   // 缺 1 天
        var handler = new PerStockHandler(_ => Body(Days));

        var (_, progress) = await RunAsync(NewPerStock(handler),
                                          args: new TaskRunArgs(Mode: FetchMode.FirstBackfill));

        Assert.Equal(["000001"], handler.Asked);
        // 门槛已经是 1 行，就不该再说"另有 N 只没到门槛"
        Assert.DoesNotContain(progress, p => p.Text.Contains("另有"));
    }

    [Fact]
    public async Task 最新那个交易日不算进期望()
    {
        // 收盘后K线先抓到、资金流快照还没跑，中间有几个小时。把那天算进期望的话，
        // 那段时间里全市场 5900 只会一起进队——一轮 5900 个请求、二十小时。
        Roster("000001");
        Bars("000001", Days);                      // 含最新那个交易日 Days[^1]
        Flow("000001", Days[..^1]);                // 资金流只到上一个交易日
        var handler = new PerStockHandler(_ => Body(Days));

        var (result, _) = await RunAsync(NewPerStock(handler));

        Assert.Empty(handler.Asked);
        Assert.True(result.NothingToDo);
    }

    [Fact]
    public async Task 没有交易日历就报错不瞎抓()
    {
        // 窗口定不出来时不能退化成"全都抓一遍"——那是 5900 个请求。
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM TradingDay";
        cmd.ExecuteNonQuery();

        Roster("000001");
        Bars("000001", Days);
        var handler = new PerStockHandler(_ => Body(Days));

        var (result, _) = await RunAsync(NewPerStock(handler));

        Assert.Empty(handler.Asked);
        Assert.Contains(result.Errors, e => e.Contains("交易日历"));
    }
}

/// <summary>
/// 逐股补历史的排队判据（2026-09-11 抽成 <see cref="MoneyFlowBackfillPlan"/>）。
/// 抽出来是因为任务和界面那个"还差 N 只"的计数必须用同一份判据，两处各写一份必然漂移。
/// </summary>
public class MoneyFlowBackfillPlanTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"mfPlan_{Guid.NewGuid():N}.sqlite");
    private readonly SqliteNetInflowDetailRepository _repo;
    private readonly SqliteBarRepository _bars;

    private static readonly DateTime[] Days =
        [.. Enumerable.Range(1, 6).Select(i => DateTime.Today.AddDays(-i)).OrderBy(d => d)];

    public MoneyFlowBackfillPlanTests()
    {
        _repo = new SqliteNetInflowDetailRepository(_db);
        _repo.EnsureSchema();
        _bars = new SqliteBarRepository(_db);
        _bars.EnsureSchema();
        var cal = new SqliteTradingDayRepository(_db);
        cal.EnsureSchema();
        cal.Upsert(Days.Select(d => (DateOnly.FromDateTime(d), "szse")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    private void Bars_(string code, params DateTime[] days) =>
        _bars.InsertOrRefreshUnconfirmed(days.Select(d => new Bar
        {
            Code = code, Granularity = MoneyFlowBackfillPlan.ExpectGranularity, PeriodStart = d,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1, FetchedAt = d.AddHours(20),
        }));

    private void Flow(string code, DateTime fetchedAt, params DateTime[] days) =>
        _repo.Upsert(days.Select(d => new NetInflowDetail
        {
            Code = code, TradeDate = d, FetchedAt = fetchedAt,
        }));

    private MoneyFlowBackfillQueue Build(int threshold, params string[] codes) =>
        MoneyFlowBackfillPlan.Build(_db, codes, _repo, threshold);

    [Fact]
    public void 期望按本地日K根数算_不是固定门槛()
    {
        Bars_("000001", Days);                 // 窗口内 5 根（最新那天不算）
        Flow("000001", Days[^1], Days);        // 都有了
        Bars_("000002", Days[3], Days[4]);     // 次新股：只有 2 根
        Flow("000002", Days[^1], Days[3], Days[4]);

        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001", "000002");

        Assert.Empty(q.Todo);
        Assert.Equal(0, q.MinorCodes);
    }

    [Fact]
    public void 缺口到门槛才排队_不到的单独统计()
    {
        Bars_("000001", Days);
        Flow("000001", Days[^1], Days[0], Days[1]);   // 缺 3 天 → 到门槛
        Bars_("000002", Days);
        Flow("000002", Days[^1], Days[0], Days[1], Days[2], Days[3]);  // 缺 1 天 → 不到门槛

        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001", "000002");

        Assert.Equal(["000001"], q.Todo);
        Assert.Equal(1, q.MinorCodes);
        Assert.Equal(1, q.MinorRows);
    }

    [Fact]
    public void 首次整段回补门槛是一行()
    {
        Bars_("000001", Days);
        Flow("000001", Days[^1], Days[0], Days[1], Days[2], Days[3]);  // 缺 1 天

        var q = Build(MoneyFlowBackfillPlan.BackfillGapThreshold, "000001");

        Assert.Equal(["000001"], q.Todo);
        Assert.Equal(0, q.MinorCodes);
    }

    [Fact]
    public void 窗口外的老数据不能顶替窗口内的缺口()
    {
        // 这张表是累积的：全表行数迟早超过任何固定门槛。用全表计数的话判据恒为假、一只都不排。
        Bars_("000001", Days);
        Flow("000001", Days[^1], Days[0], Days[1]);                       // 窗口内只有 2 天
        Flow("000001", Days[^1], [.. Enumerable.Range(200, 300).Select(i => DateTime.Today.AddDays(-i))]);

        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001");

        Assert.Equal(["000001"], q.Todo);
    }

    [Fact]
    public void 一根日K都没有的票不排队()
    {
        // 还没抓过这只票的日线——那时候去问资金流只会拿回一堆没法对账的行。
        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001");

        Assert.Empty(q.Todo);
        Assert.Equal(0, q.MinorCodes);
    }

    [Fact]
    public void 没有日K的票不算进_一行都没有_那个数()
    {
        // 2026-09-11 实机撞到的：那 22 只连日K都没有的票期望为 0、压根不排队，
        // 却被算进"窗口内一行都没有"，日志于是出现"待补 2 只，其中 22 只一行都没有"。
        Bars_("000001", Days);                       // 有 5 天日K、没有资金流 → 真的一行都没有
        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001", "000002", "600519");

        Assert.Equal(["000001"], q.Todo);
        Assert.Equal(1, q.Never);
    }

    [Fact]
    public void 最久没抓的排前面()
    {
        // 2026-09-04 那个坑：按代码顺序排的话，每天零点一到又从 000001 开始，靠后的票永远轮不到。
        Bars_("000001", Days); Flow("000001", DateTime.Today.AddHours(-1), Days[0]);
        Bars_("000002", Days); Flow("000002", DateTime.Today.AddDays(-9), Days[0]);
        Bars_("600519", Days); Flow("600519", DateTime.Today.AddDays(-3), Days[0]);

        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001", "000002", "600519");

        Assert.Equal(["000002", "600519", "000001"], q.Todo);
    }

    [Fact]
    public void 交易日历不够就说不出窗口()
    {
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM TradingDay";
        cmd.ExecuteNonQuery();

        var q = Build(MoneyFlowBackfillPlan.DefaultGapThreshold, "000001");

        Assert.NotNull(q.Unavailable);
        Assert.Empty(q.Todo);
    }
}
