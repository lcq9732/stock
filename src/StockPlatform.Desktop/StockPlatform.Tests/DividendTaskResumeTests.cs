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
        // ⚠ 不调 SqliteConnection.ClearAllPools()：那是**进程级**的，会把别的测试类正在用的
        //   连接池一起清掉——整个测试跑就会随机崩在 testhost 上（2026-09-18 实测：单独跑这一类
        //   31 条全绿、跟全量一起跑必 abort，去掉这一句就稳了）。
        //   代价只是临时目录删不掉时留在 %TEMP% 里，下次开机自然清。
        try { Directory.Delete(_dir, recursive: true); } catch { /* 文件还被连接占着，留着就留着 */ }
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
    private DividendTask NewTask(FakeProvider provider, int batchSize = 2,
                                 IDividendNoticeIndex? index = null)
        => new(_paths, provider, _repo, _manifest, index, batchSize);

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

    // ── ⑤ 公告索引（2026-09-18）──────────────────────────────────

    /// <summary>离线的公告索引。<see cref="Boom"/> 打开就模拟索引源挂了。</summary>
    private sealed class FakeNoticeIndex : IDividendNoticeIndex
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public string SourceName => "假索引";
        /// <summary>命中的票，公告日默认取今天（＝比任何"以前抓过"都新）。</summary>
        public HashSet<string> Hits { get; init; } = [];
        public bool Boom { get; init; }
        public int Calls;

        public Task<IReadOnlyDictionary<string, DateTime>> GetRecentAsync(
            int lookbackDays, CancellationToken ct = default)
        {
            Calls++;
            if (Boom) throw new InvalidOperationException("索引源挂了");
            var today = DateTime.Today;
            return Task.FromResult<IReadOnlyDictionary<string, DateTime>>(
                Hits.ToDictionary(c => c, _ => today, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task 索引命中的票即使刚抓过也重抓()
    {
        // 先跑一轮，全部都是"刚抓过"
        await NewTask(new FakeProvider()).RunAsync(Args(), CancellationToken.None);

        var idx = new FakeNoticeIndex { Hits = { "600000", "000001" } };
        var p = new FakeProvider();
        await NewTask(p, index: idx).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(1, idx.Calls);
        Assert.Equal(["000001", "600000"], p.Requested.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task 索引挂了就退回水位线_不是什么都不抓()
    {
        // **绝不能**因为索引没拿到就"本轮没有要抓的"——那是静默漏抓。
        var idx = new FakeNoticeIndex { Boom = true };
        var p = new FakeProvider();
        var r = await NewTask(p, index: idx).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(Codes.Length, p.Requested.Count);   // 一只不少，照水位线全抓
    }

    [Fact]
    public async Task 索引没命中且都很新鲜时_一个请求都不发()
    {
        await NewTask(new FakeProvider()).RunAsync(Args(), CancellationToken.None);

        var idx = new FakeNoticeIndex();          // 命中为空
        var p = new FakeProvider();
        var r = await NewTask(p, index: idx).RunAsync(Args(), CancellationToken.None);

        Assert.Empty(p.Requested);
        Assert.True(r.NothingToDo);
    }

    [Fact]
    public async Task 只补待办时不问索引()
    {
        // 待办的目标从清单来，问索引是白问一轮请求。
        var idx = new FakeNoticeIndex();
        await NewTask(new FakeProvider(), index: idx).RunAsync(Args(FetchMode.FillBacklog), CancellationToken.None);
        Assert.Equal(0, idx.Calls);
    }

    [Fact]
    public async Task 整段回补时不问索引()
    {
        var idx = new FakeNoticeIndex();
        var p = new FakeProvider();
        await NewTask(p, index: idx).RunAsync(Args(FetchMode.FirstBackfill), CancellationToken.None);
        Assert.Equal(0, idx.Calls);
        Assert.Equal(Codes.Length, p.Requested.Count);
    }

    // ── ④ 水位线播种（2026-09-18）────────────────────────────────

    /// <summary>直接往 Dividend 表塞一条历史行，模拟"老版本抓过、新表还空着"。</summary>
    private void SeedOldDividendRow(string code, DateTime fetchedAt)
        => _repo.ReplaceByCode(code, [new DividendRow
        {
            Code = code,
            AnnounceDate = new DateTime(2025, 4, 10),
            DividendYuan = 1.0,
            Progress = "实施",
            FetchedAt = fetchedAt,
        }]);

    [Fact]
    public async Task 首轮按库里已有的分红播种水位线_不重抓()
    {
        // 老版本抓过的两只：Dividend 表里有行、状态表还空着。
        SeedOldDividendRow("600000", DateTime.Now.AddDays(-3));
        SeedOldDividendRow("000001", DateTime.Now.AddDays(-3));
        Assert.Empty(_repo.GetFetchStates());

        var p = new FakeProvider();
        await NewTask(p).RunAsync(Args(), CancellationToken.None);

        // 播种之后这两只算"3 天前抓过"，本轮只抓剩下的 5 只
        Assert.DoesNotContain("600000", p.Requested);
        Assert.DoesNotContain("000001", p.Requested);
        Assert.Equal(Codes.Length - 2, p.Requested.Count);
    }

    [Fact]
    public async Task 播种只做一次_不覆盖真实的抓取状态()
    {
        // 先跑一轮：状态表里是真的抓取记录（今天）
        await NewTask(new FakeProvider()).RunAsync(Args(), CancellationToken.None);
        var before = _repo.GetFetchStates()["600000"].LastOkAt;

        // 再塞一条"很久以前抓的"分红行——表非空，播种不该动它
        SeedOldDividendRow("600000", DateTime.Now.AddDays(-400));
        Assert.Equal(0, _repo.SeedFetchStatesFromDividends());
        Assert.Equal(before, _repo.GetFetchStates()["600000"].LastOkAt);
    }

    [Fact]
    public void 时刻是空值的老行不播种()
    {
        // 库里有 323 只老 code 的 fetched_at 是 DateTime.MinValue（早期数据没记时刻）。
        // 那不是"抓过"——播成水位线会让它们永远不再被抓。
        SeedOldDividendRow("600000", DateTime.MinValue);
        SeedOldDividendRow("000001", DateTime.Now.AddDays(-3));

        Assert.Equal(1, _repo.SeedFetchStatesFromDividends());
        Assert.DoesNotContain("600000", _repo.GetFetchStates().Keys);
        Assert.Contains("000001", _repo.GetFetchStates().Keys);
    }

    [Fact]
    public void 一行都没有的票播不了种_只能去抓()
    {
        // "无分红"和"没抓过"在 Dividend 表里长得一模一样——这正是要单独建状态表的理由。
        SeedOldDividendRow("600000", DateTime.Now.AddDays(-3));
        _repo.SeedFetchStatesFromDividends();

        var states = _repo.GetFetchStates();
        Assert.Single(states);
        Assert.Equal(Codes.Length - 1,
                     DividendTask.SelectDue(Codes, states, DateTime.Now.AddDays(-25)).Count);
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
        // 【重新拉取失败】是拿 taskId 字符串分派的（RetryFailedTask 里那句 Enum.TryParse），
        // 所以「RetryTaskIds.Dividend 解析得成 FetchActionId」这一环断了就会静默跳过分红。
        // 2026-09-22 那一项迁成任务、ITaskBacklogRunner 端口撤掉，这里改成直接验解析 + 声明。
        var registry = new FetchTaskRegistry();
        registry.Register(FetchActionId.FetchDividend,
                          () => new DividendTask(_paths, new FakeProvider(), _repo, _manifest));
        registry.Register(FetchActionId.StepLhb, () => new DummyTask());

        Assert.True(registry.HandlesBacklog(FetchActionId.FetchDividend));
        Assert.False(registry.HandlesBacklog(FetchActionId.StepLhb));

        Assert.True(Enum.TryParse<FetchActionId>(RetryTaskIds.Dividend, out var dividend)
                    && registry.HandlesBacklog(dividend));
        Assert.True(Enum.TryParse<FetchActionId>(RetryTaskIds.Lhb, out var lhb)
                    && !registry.HandlesBacklog(lhb));
        Assert.False(Enum.TryParse<FetchActionId>("不存在的任务", out _));
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
