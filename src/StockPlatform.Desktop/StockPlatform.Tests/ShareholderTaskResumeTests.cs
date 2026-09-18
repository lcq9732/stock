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
/// 【拉取股东数据】断点续跑的整链验证（2026-09-18）——**离线跑产品代码本身**：
/// 真的 SQLite、真的 manifest、真的 <see cref="ShareholderTask"/>，只把数据源换成本地假货
/// （见 project_offline_sim_instead_of_refetch）。
///
/// 测的是这次迁移的起因：老实现每轮全量重抓、取消时失败名单写不进去（写在
/// <c>Task.WhenAll</c> 之后），被限流打断就整轮白跑。
/// </summary>
public class ShareholderTaskResumeTests : IDisposable
{
    private readonly string _dir;
    private readonly FetchPaths _paths;
    private readonly SqliteShareholderRepository _repo;
    private readonly JsonManifestStore _manifest;

    private static readonly string[] Codes =
        ["000001", "000002", "600000", "600001", "600002", "600003", "600004"];

    public ShareholderTaskResumeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"shTask_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteShareholderRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, Codes.Select(c => (c, c)).ToList(), "stock");
        _manifest = new JsonManifestStore(_paths.ManifestPath);
    }

    public void Dispose()
    {
        // 不调 SqliteConnection.ClearAllPools()——进程级，会崩掉别的测试类（2026-09-18 踩过）。
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
    }

    // ── 离线数据源 ─────────────────────────────────────────────────

    /// <summary>
    /// 本地假的股东页。记下每只票被请求过几次——"第二轮不重抓"这件事只能从请求数看出来。
    /// <see cref="Failing"/> 里的按限流失败，<see cref="Empty"/> 里的返回空聚合（真没数据）。
    /// </summary>
    private sealed class FakeProvider : IShareholderProvider
    {
        public event Action<string>? OnStatus { add { } remove { } }
        public readonly List<string> Requested = [];
        public HashSet<string> Failing { get; init; } = [];
        public HashSet<string> Empty { get; init; } = [];
        public Action<string>? OnFetch { get; set; }
        public DateTime Period { get; init; } = new(2026, 6, 30);

        public Task<ShareholderData> GetAsync(string code, CancellationToken ct = default)
        {
            lock (Requested) Requested.Add(code);
            OnFetch?.Invoke(code);
            ct.ThrowIfCancellationRequested();
            if (Failing.Contains(code)) throw new RateLimitedException($"{code} 被限流了");
            if (Empty.Contains(code)) return Task.FromResult(new ShareholderData());

            var now = DateTime.Now;
            return Task.FromResult(new ShareholderData
            {
                Counts =
                [
                    new ShareholderCountRow
                    {
                        Code = code, ReportDate = Period, HolderNum = 12345, AvgShares = 678,
                        FetchedAt = now,
                    },
                ],
                TopHolders =
                [
                    new TopShareholderRow
                    {
                        Code = code, ReportDate = Period, Kind = TopShareholderRow.KindFloat,
                        Rank = 1, HolderName = "香港中央结算有限公司", Shares = 1000, Ratio = 1.5,
                        FetchedAt = now,
                    },
                ],
            });
        }
    }

    /// <summary>批大小默认 2——一轮里得有好几批，"中断之后接着跑"才测得出来。</summary>
    private ShareholderTask NewTask(FakeProvider provider, int batchSize = 2)
        => new(_paths, provider, _repo, _manifest, batchSize);

    private static TaskRunArgs Args(FetchMode mode = FetchMode.Incremental, int? maxItems = null)
        => new(Mode: mode, MaxItems: maxItems);

    private List<string> FailedTodo() =>
        (_manifest.Load().Todo(RetryTaskIds.Shareholder, RetryTodoKind.Failed)?.Targets ?? [])
        .Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();

    // ── ① 抓过就不再抓 ────────────────────────────────────────────

    [Fact]
    public async Task 第二轮不重抓已经抓到最新报告期的()
    {
        // provider 给的报告期用"法定截止口径的最新一期"，抓完就该判定为最新。
        var expected = new ShareholderFetchPlanner(_paths).Plan().ExpectedPeriod;
        var p1 = new FakeProvider { Period = expected };
        var r1 = await NewTask(p1).RunAsync(Args(), CancellationToken.None);
        Assert.Equal(TaskState.Completed, r1.State);
        Assert.Equal(Codes.Length, p1.Requested.Count);

        var p2 = new FakeProvider { Period = expected };
        var r2 = await NewTask(p2).RunAsync(Args(), CancellationToken.None);

        Assert.Empty(p2.Requested);          // ← 这就是这次迁移要的东西
        Assert.True(r2.NothingToDo);
    }

    [Fact]
    public async Task 整段回补无视判据重抓()
    {
        var expected = new ShareholderFetchPlanner(_paths).Plan().ExpectedPeriod;
        await NewTask(new FakeProvider { Period = expected }).RunAsync(Args(), CancellationToken.None);

        var p = new FakeProvider { Period = expected };
        await NewTask(p).RunAsync(Args(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.Equal(Codes.Length, p.Requested.Count);
    }

    // ── ② 中途取消：数据和进度都留下 ──────────────────────────────

    [Fact]
    public async Task 取消之后已抓的留下_下一轮只补剩下的()
    {
        // **这一条就是这次迁移的起因**：老实现取消时什么都不留，重新执行＝从头再来。
        var expected = new ShareholderFetchPlanner(_paths).Plan().ExpectedPeriod;
        using var cts = new CancellationTokenSource();
        var p1 = new FakeProvider { Period = expected };
        // 一批 2 只。抓到第 5 只（第三批的头一只）时取消 ⇒ 前两批已落库、第三批整批作废。
        p1.OnFetch = code => { if (code == Codes[4]) cts.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewTask(p1).RunAsync(Args(), cts.Token));

        var after = _repo.GetFetchStateByCode();
        Assert.Equal(4, after.Count);

        var p2 = new FakeProvider { Period = expected };
        await NewTask(p2).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(Codes.Except(after.Keys).OrderBy(c => c, StringComparer.Ordinal),
                     p2.Requested.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(Codes.Length, _repo.GetFetchStateByCode().Count);
    }

    [Fact]
    public async Task 分批跑_到量收尾_下一轮接着抓()
    {
        // MaxItems = 2 批＝4 只。到量收尾算**正常完成**，所以水位线必须在收尾前落好。
        var expected = new ShareholderFetchPlanner(_paths).Plan().ExpectedPeriod;
        var p1 = new FakeProvider { Period = expected };
        var r1 = await NewTask(p1).RunAsync(Args(maxItems: 2), CancellationToken.None);
        Assert.Equal(TaskState.Completed, r1.State);
        Assert.Equal(4, p1.Requested.Count);

        var p2 = new FakeProvider { Period = expected };
        await NewTask(p2).RunAsync(Args(), CancellationToken.None);
        Assert.Equal(3, p2.Requested.Count);
    }

    [Fact]
    public async Task 连续三批全因限流失败就收工_记成跳过不是完成()
    {
        // 记成完成的话 AlreadyRanOn 会认，今天就不会再跑了——限流过去也白搭。
        var p = new FakeProvider { Failing = [.. Codes] };
        var r = await NewTask(p).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.NotNull(r.SkippedReason);
        Assert.Contains("限流", r.SkippedReason);
        Assert.Equal(6, p.Requested.Count);      // 三批＝6 只就收工，没把 7 只跑满
    }

    // ── ③ 失败名单 ────────────────────────────────────────────────

    [Fact]
    public async Task 失败的进待办_只补待办时只抓这几只()
    {
        var p1 = new FakeProvider { Failing = { "600000", "600001" } };
        await NewTask(p1).RunAsync(Args(), CancellationToken.None);

        Assert.Equal(["600000", "600001"], FailedTodo());

        var p2 = new FakeProvider();
        await NewTask(p2).RunAsync(Args(FetchMode.FillBacklog), CancellationToken.None);
        Assert.Equal(["600000", "600001"], p2.Requested.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Empty(FailedTodo());
    }

    [Fact]
    public async Task 没有待办时只补待办什么都不做()
    {
        var p = new FakeProvider();
        var r = await NewTask(p).RunAsync(Args(FetchMode.FillBacklog), CancellationToken.None);

        Assert.Empty(p.Requested);
        Assert.True(r.NothingToDo);
    }

    // ── ④ 铁律：空结果不删 ────────────────────────────────────────

    [Fact]
    public async Task 空结果不删库里已有的股东数据()
    {
        // ReplaceByCode 是先删该 code 两表旧行再写，限流/改版返回空页时若照写会把历史整只删掉。
        await NewTask(new FakeProvider()).RunAsync(Args(), CancellationToken.None);
        Assert.NotEmpty(_repo.GetCountSeries("600000"));

        var p = new FakeProvider { Empty = [.. Codes] };
        await NewTask(p).RunAsync(Args(FetchMode.FirstBackfill), CancellationToken.None);

        Assert.NotEmpty(_repo.GetCountSeries("600000"));   // 还在
    }

    // ── ⑤ 分派 ────────────────────────────────────────────────────

    [Fact]
    public void 任务声明了自己补待办_注册表按声明分派()
    {
        // 【重新拉取失败股票】是拿 taskId 字符串走 ITaskBacklogRunner 转交的，
        // 所以"RetryTaskIds.Shareholder 解析得成 FetchActionId"这一环断了就会静默跳过。
        var registry = new FetchTaskRegistry();
        registry.Register(FetchActionId.FetchShareholder,
                          () => new ShareholderTask(_paths, new FakeProvider(), _repo, _manifest));

        Assert.True(registry.HandlesBacklog(FetchActionId.FetchShareholder));
        Assert.True(((ITaskBacklogRunner)registry).Handles(RetryTaskIds.Shareholder));
    }

    // ── ⑥ 熔断判据 ────────────────────────────────────────────────

    [Fact]
    public void 整批因限流失败才算全军覆没()
    {
        Assert.True(ShareholderTask.IsDeadBatch([Fail("1", true), Fail("2", false)]));
        Assert.False(ShareholderTask.IsDeadBatch([Fail("1", true), Good("2")]));
        // 整批失败但跟限流无关（比如页面改版把解析全打挂了）：该报失败，不该悄悄收工
        Assert.False(ShareholderTask.IsDeadBatch([Fail("1", false), Fail("2", false)]));
    }

    private static ShareholderTask.ShareholderOutcome Good(string code)
        => new(code, new ShareholderData(), true, null, false);

    private static ShareholderTask.ShareholderOutcome Fail(string code, bool limited)
        => new(code, null, false, "boom", limited);
}
