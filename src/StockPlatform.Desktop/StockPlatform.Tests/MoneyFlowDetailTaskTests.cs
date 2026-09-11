using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取分档资金流】迁成新式任务之后的行为（2026-09-11）。
///
/// ════ 为什么有这个文件 ════
/// 迁移的直接动因是一次**误判卡死**：老实现每 100 只才报一句进度，2026-09-11 那轮待办只剩
/// 72 只——一句都报不出来，而 push2his 慢（5 秒间隔、每 15 个请求歇 2 分钟、单只失败还要静默
/// 重试），于是必然哑过 5 分钟，被静默看门狗掐断。掐断不丢数据，但这一项每天记一次失败，
/// 而且待办只要少于 100 只就永远卡在原地。
/// 所以第一条测试钉的就是：**待办不足 100 只时，每一只都要有进度出来**。
/// </summary>
public class MoneyFlowDetailTaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mfTask_{Guid.NewGuid():N}");
    private readonly FetchPaths _paths;
    private readonly SqliteNetInflowDetailRepository _repo;

    public MoneyFlowDetailTaskTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteNetInflowDetailRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
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
    private static string Kline(string day) =>
        $"\"{day},100,-70,-30,40,60,1.0,-0.7,-0.3,0.4,0.6,10.0,1.0\"";

    private static string Body(params string[] klines) =>
        "{\"rc\":0,\"data\":{\"code\":\"x\",\"klines\":[" + string.Join(",", klines) + "]}}";

    private static string Empty() => "{\"rc\":100,\"data\":null}";

    private static EastMoneyMoneyFlowProvider NewPerStock(PerStockHandler handler) =>
        new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
            new HttpClient(handler));

    private void Roster(params string[] codes) =>
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, codes.Select(c => (c, c)));

    /// <summary>跑一轮，把广播出来的进度接住。</summary>
    private async Task<(TaskRunResult Result, List<TaskProgress> Progress)> RunAsync(
        EastMoneyMoneyFlowProvider? perStock,
        EastMoneyMoneyFlowSnapshotProvider? snapshot = null,
        TaskRunArgs? args = null)
    {
        var task = new MoneyFlowDetailTask(_paths, _repo, perStock, snapshot,
                                           progressInterval: TimeSpan.Zero);
        var seen = new List<TaskProgress>();
        task.OnProgress += p => seen.Add(p);
        var result = await task.RunAsync(args ?? new TaskRunArgs(), CancellationToken.None);
        return (result, seen);
    }

    // ───────────────────────── 测试 ─────────────────────────

    [Fact]
    public async Task 待办不足一百只也要每只都报进度()
    {
        // 这正是被误判卡死的那个场景：待办 3 只，老实现（每 100 只一句）全程哑火。
        Roster("000001", "000002", "600519");
        var handler = new PerStockHandler(_ => Body(Kline("2026-09-10")));

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
        Roster("000001", "000002", "600519");
        var handler = new PerStockHandler(_ => Body(Kline("2026-09-09"), Kline("2026-09-10")));

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
        Roster("000001", "000002", "600519");
        var handler = new PerStockHandler(code => code == "600519" ? Empty() : Body(Kline("2026-09-10")));

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
        var handler = new PerStockHandler(_ => Empty());

        var (result, _) = await RunAsync(NewPerStock(handler));

        Assert.Contains(result.Errors, e => e.Contains("只接口返回空"));
    }

    [Fact]
    public async Task 已经补齐的票不再排队()
    {
        // 判据是"库里这只票有多少行"，不是"今天抓过没有"——快照每天会把每只票都刷一遍。
        Roster("000001", "000002");
        _repo.Upsert(Enumerable.Range(0, MoneyFlowBackfillPlan.FullWindowRows)
            .Select(i => new NetInflowDetail
            {
                Code = "000001",
                TradeDate = new DateTime(2026, 1, 1).AddDays(i),
                FetchedAt = new DateTime(2026, 9, 10),
            }));
        var handler = new PerStockHandler(_ => Body(Kline("2026-09-10")));

        await RunAsync(NewPerStock(handler));

        Assert.Equal(["000002"], handler.Asked);
    }

    [Fact]
    public async Task 两条通道都没配就没事可做()
    {
        var (result, _) = await RunAsync(perStock: null, snapshot: null);

        Assert.True(result.NothingToDo);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task 历史都齐了也算没事可做()
    {
        Roster("000001");
        _repo.Upsert(Enumerable.Range(0, MoneyFlowBackfillPlan.FullWindowRows)
            .Select(i => new NetInflowDetail
            {
                Code = "000001",
                TradeDate = new DateTime(2026, 1, 1).AddDays(i),
                FetchedAt = new DateTime(2026, 9, 10),
            }));
        var handler = new PerStockHandler(_ => Body(Kline("2026-09-10")));

        var (result, _) = await RunAsync(NewPerStock(handler));

        Assert.Empty(handler.Asked);
        Assert.True(result.NothingToDo);
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

    public MoneyFlowBackfillPlanTests()
    {
        _repo = new SqliteNetInflowDetailRepository(_db);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    private void Fill(string code, int rows, DateTime fetchedAt) =>
        _repo.Upsert(Enumerable.Range(0, rows).Select(i => new NetInflowDetail
        {
            Code = code,
            TradeDate = new DateTime(2026, 1, 1).AddDays(i),
            FetchedAt = fetchedAt,
        }));

    [Fact]
    public void 行数不足门槛的才排队()
    {
        Fill("000001", MoneyFlowBackfillPlan.FullWindowRows, new DateTime(2026, 9, 1));
        Fill("000002", MoneyFlowBackfillPlan.FullWindowRows - 1, new DateTime(2026, 9, 1));

        var (todo, never) = MoneyFlowBackfillPlan.Build(["000001", "000002", "000003"], _repo);

        Assert.Equal(["000003", "000002"], todo);   // 从没抓过的排最前
        Assert.Equal(1, never);
    }

    [Fact]
    public void 最久没抓的排前面()
    {
        // 2026-09-04 那个坑：按代码顺序排的话，每天零点一到又从 000001 开始，靠后的票永远轮不到。
        Fill("000001", 1, new DateTime(2026, 9, 10));
        Fill("000002", 1, new DateTime(2026, 9, 1));
        Fill("600519", 1, new DateTime(2026, 9, 5));

        var (todo, never) = MoneyFlowBackfillPlan.Build(["000001", "000002", "600519"], _repo);

        Assert.Equal(["000002", "600519", "000001"], todo);
        Assert.Equal(0, never);
    }
}
