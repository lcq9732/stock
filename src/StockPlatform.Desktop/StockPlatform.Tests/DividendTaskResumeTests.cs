using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取分红送配】断点续跑的整链验证（2026-09-18）——**离线跑产品代码本身**：
/// 真的 SQLite、真的 manifest、真的 <see cref="DividendTask"/>，只把数据源换成本地假货
/// （见 project_offline_sim_instead_of_refetch：接口返回值能本地造出来时，就别发几千个请求）。
///
/// 测的正是这次迁移的起因：
///   ① 跑完一轮再跑一轮 → 第二轮**一个请求都不发**（老实现会重抓 5800 只）；
///   ② 中途取消 → 抓到的数据和"抓到哪了"都在，失败名单也落了盘（老实现全丢）；
///   ③ 再跑一轮 → 只抓没抓过的那些，不重抓已经抓好的。
/// </summary>
public class DividendTaskResumeTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteDividendRepository _repo;
    private readonly JsonManifestStore _manifest;

    private static readonly string[] Codes =
        ["000001", "000002", "600000", "600001", "600002", "600003", "600004"];

    public DividendTaskResumeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"divTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteDividendRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, Codes.Select(c => (c, c)).ToList(), "stock");
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 离线数据源 ─────────────────────────────────────────────────

    /// <summary>
    /// 本地假的分红页。记下每只票被请求过几次——"第二轮不重抓"这件事只能从请求数看出来。
    /// <see cref="Failing"/> 里的票按限流失败，<see cref="OnFetch"/> 给测试一个插手的钩子（取消）。
    /// </summary>
    private sealed class FakeProvider : IDividendProvider
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public readonly List<string> Requested = [];
        public HashSet<string> Failing { get; init; } = [];
        public Action<string>? OnFetch { get; set; }

        public Task<List<DividendRow>> GetAllAsync(string code, CancellationToken ct = default)
            => Task.FromResult(GetAllWithRightsAsync(code, ct).Result.Dividends);

        public Task<DividendAndRights> GetAllWithRightsAsync(string code, CancellationToken ct = default)
        {
            lock (Requested) Requested.Add(code);
            OnFetch?.Invoke(code);
            ct.ThrowIfCancellationRequested();
            if (Failing.Contains(code))
                throw new RateLimitedException($"{code} 被限流了");

            var rows = new List<DividendRow>
            {
                new()
                {
                    Code = code,
                    AnnounceDate = new DateTime(2025, 4, 10),
                    DividendYuan = 2.5,
                    Progress = "实施",
                    ExDate = new DateTime(2025, 6, 20),
                    FetchedAt = DateTime.Now,
                },
            };
            return Task.FromResult(new DividendAndRights(rows, []));
        }
    }

    /// <summary>批大小默认调成 2——一轮里得有好几批，"中断之后接着跑"才测得出来。</summary>
    private DividendTask NewTask(FakeProvider provider, int batchSize = 2)
        => new(_paths, provider, _repo, _manifest, batchSize);

    private static TaskRunArgs Args(FetchMode mode = FetchMode.Incremental, int? maxItems = null)
        => new(Mode: mode, MaxItems: maxItems);

    private List<string> FailedTodo() =>
        (_manifest.Load().Todo(RetryTaskIds.Dividend, RetryTodoKind.Failed)?.Targets ?? [])
        .Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();

    // ── ① 跑完一轮，第二轮不该再发请求 ────────────────────────────

    [Fact]
    public async Task 第二轮不重抓已经抓过的()
    {
        var p1 = new FakeProvider();
        var r1 = await NewTask(p1).RunAsync(Args(), CancellationToken.None);
        Assert.Equal(TaskState.Completed, r1.State);
        Assert.Equal(Codes.Length, p1.Requested.Count);
        Assert.Equal(Codes.Length, _repo.GetFetchStates().Count);

        var p2 = new FakeProvider();
        var r2 = await NewTask(p2).RunAsync(Args(), CancellationToken.None);

        Assert.Empty(p2.Requested);          // ← 这就是这次迁移要的东西
        Assert.True(r2.NothingToDo);
    }

    [Fact]
    public async Task 整段回补无视水位线重抓()
    {
        await NewTask(new FakeProvider()).RunAsync(Args(), CancellationToken.None);

        var p = new FakeProvider();
        await NewTask(p).RunAsync(Args(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(Codes.Length, p.Requested.Count);
    }

    // ── ② 中途取消：数据和进度都留下 ──────────────────────────────

    [Fact]
    public async Task 取消之后已抓的留下_下一轮只补剩下的()
    {
        // **这一条就是这次迁移的起因**：老实现在取消时什么都不留，重新执行＝从头再来。
        using var cts = new CancellationTokenSource();
        var p1 = new FakeProvider();
        // 一批 2 只。抓到第 5 只（第三批的头一只）时取消 → 前两批已落库，第三批整批作废。
        p1.OnFetch = code => { if (code == Codes[4]) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewTask(p1).RunAsync(Args(), cts.Token));

        var afterCancel = _repo.GetFetchStates();
        Assert.Equal(4, afterCancel.Count);                       // 前两批（4 只）留下了
        Assert.All(afterCancel.Values, st => Assert.NotNull(st.LastOkAt));

        // 下一轮只抓剩下的三只，已经抓好的一个请求都不发
        var p2 = new FakeProvider();
        await NewTask(p2).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(Codes.Except(afterCancel.Keys).OrderBy(c => c, StringComparer.Ordinal),
                     p2.Requested.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(Codes.Length, _repo.GetFetchStates().Count);
    }

    [Fact]
    public async Task 分批跑_到量收尾_下一轮接着抓()
    {
        // MaxItems = 2 批＝4 只（「空闲时」那类触发就是这么用的）。到量收尾算**正常完成**，
        // 所以水位线必须在收尾前落好，否则下一轮会把这 4 只再抓一遍。
        var p1 = new FakeProvider();
        var r1 = await NewTask(p1).RunAsync(Args(maxItems: 2), CancellationToken.None);
        Assert.Equal(TaskState.Completed, r1.State);
        Assert.Equal(4, p1.Requested.Count);
        Assert.Equal(4, _repo.GetFetchStates().Count);

        var p2 = new FakeProvider();
        await NewTask(p2).RunAsync(Args(), CancellationToken.None);
        Assert.Equal(3, p2.Requested.Count);          // 只剩没抓的那三只
    }

    [Fact]
    public async Task 连续三批全因限流失败就收工_记成跳过不是完成()
    {
        // 记成完成的话 AlreadyRanOn 会认，今天就不会再跑了——限流过去也白搭。
        var p = new FakeProvider { Failing = [.. Codes] };
        var r = await NewTask(p).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);   // 骨架层面不是失败
        Assert.NotNull(r.SkippedReason);
        Assert.Contains("限流", r.SkippedReason);
        Assert.Equal(6, p.Requested.Count);           // 三批＝6 只之后就收工，没把 7 只跑满
    }

    // ── ③ 失败名单：落盘、可补、补完清零 ──────────────────────────

    [Fact]
    public async Task 失败的进待办_只补待办时只抓这几只()
    {
        var p1 = new FakeProvider { Failing = { "600000", "600001" } };
        await NewTask(p1).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(["600000", "600001"], FailedTodo());
        // 失败的不该被记成"抓过"——它们的 last_ok_at 必须是空的，下一轮还得抓。
        var states = _repo.GetFetchStates();
        Assert.Null(states["600000"].LastOkAt);
        Assert.Contains("限流", states["600000"].FailReason);

        // 只补待办：只抓这两只
        var p2 = new FakeProvider();
        await NewTask(p2).RunAsync(Args(FetchMode.FillBacklog), CancellationToken.None);
        Assert.Equal(["600000", "600001"], p2.Requested.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Empty(FailedTodo());          // 补上了就从名单里划掉
    }

    [Fact]
    public async Task 空结果不删库里已有的分红()
    {
        // 铁律：ReplaceByCode 是先删后插，限流返回空页时若照写会把整只票的分红删光。
        await NewTask(new FakeProvider()).RunAsync(Args(), CancellationToken.None);
        Assert.Single(_repo.GetByCode("600000"));

        var p = new FakeProvider { Failing = { "600000", "600001", "600002", "600003", "600004",
                                               "000001", "000002" } };
        await NewTask(p).RunAsync(Args(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Single(_repo.GetByCode("600000"));   // 还在
    }

    [Fact]
    public async Task 没有待办时只补待办什么都不做()
    {
        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(Args(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(p.Requested);
        Assert.True(r.NothingToDo);
    }

    [Fact]
    public void 注册表按声明把待办分派给任务()
    {
        // 【重新拉取失败股票】是拿 taskId 字符串走 ITaskBacklogRunner 转交的，
        // 所以"RetryTaskIds.Dividend 解析得成 FetchActionId"这一环断了就会静默跳过分红。
        var registry = new FetchTaskRegistry();
        registry.Register(FetchActionId.FetchDividend,
                          () => new DividendTask(_paths, new FakeProvider(), _repo, _manifest));
        registry.Register(FetchActionId.StepLhb, () => new DummyTask());

        Assert.True(registry.HandlesBacklog(FetchActionId.FetchDividend));
        Assert.False(registry.HandlesBacklog(FetchActionId.StepLhb));

        ITaskBacklogRunner runner = registry;
        Assert.True(runner.Handles(RetryTaskIds.Dividend));
        Assert.False(runner.Handles(RetryTaskIds.Lhb));
        Assert.False(runner.Handles("不存在的任务"));
    }

    /// <summary>只为验分派用的空任务——HandlesBacklog 默认 false。</summary>
    private sealed class DummyTask : FetchTaskBase<int>
    {
        public override FetchActionId Id => FetchActionId.StepLhb;
        protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
            TaskRunArgs args,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    public void 任务声明了自己补待办()
    {
        // 分派靠的就是这个属性（见 IFetchTask.HandlesBacklog）——它要是回落成 false，
        // 【重新拉取失败股票】会跑到 orchestrator 那条已经删掉的分支上。
        Assert.True(new DividendTask(_paths, new FakeProvider(), _repo, _manifest).HandlesBacklog);
    }
}
