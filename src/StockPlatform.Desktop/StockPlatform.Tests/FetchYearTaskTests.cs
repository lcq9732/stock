using StockPlatform.Data.Orchestration;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【拉取区间数据】分派器（2026-09-22，见 <see cref="FetchYearTask"/> 的类注释）。
///
/// 它自己不抓任何东西，所以**它的全部价值就是"调谁、按什么顺序、传什么参数、出错怎么办"**——
/// 而这四件事错了都不会有任何地方报：顺序错了是"退市收尾用了旧名单"，
/// 参数漏传是"某一项按日常语义跑了一遍、历史根本没补"，两者都静默。
/// </summary>
public class FetchYearTaskTests
{
    /// <summary>
    /// 一个什么都不做的子任务，只用来把事件播出去——验"分派器有没有原样转发"。
    /// </summary>
    private sealed class FakeSub(FetchActionId id) : IFetchTask
    {
        public FetchActionId Id => id;
        public event Action<TaskProgress>? OnProgress;
        public event Action<TaskLiveness>? OnLiveness;
        public event Action<TaskStateChanged>? OnStateChanged;

        public void EmitProgress(TaskProgress p) => OnProgress?.Invoke(p);
        public void EmitLiveness(TaskLiveness l) => OnLiveness?.Invoke(l);
        public void EmitState(TaskStateChanged s) => OnStateChanged?.Invoke(s);

        public Task<TaskRunResult> RunAsync(TaskRunArgs args, CancellationToken ct)
            => Task.FromResult(TaskRunResult.Ok());
    }

    /// <summary>记下每次分派：调了谁、带了什么参数；并把订阅口子暴露出来。</summary>
    private sealed class RecordingDispatcher : IFetchTaskDispatcher
    {
        public readonly List<(FetchActionId Id, TaskRunArgs Args)> Calls = [];
        public readonly List<FakeSub> Subs = [];
        public HashSet<FetchActionId> ThrowOn { get; init; } = [];
        public HashSet<FetchActionId> NothingToDo { get; init; } = [];

        public Task<FetchResult> RunAsync(FetchActionId id, TaskRunArgs args,
                                          Action<IFetchTask>? subscribe, CancellationToken ct)
        {
            Calls.Add((id, args));
            var sub = new FakeSub(id);
            Subs.Add(sub);
            subscribe?.Invoke(sub);          // 骨架就是在开跑前这么调的
            if (ThrowOn.Contains(id)) throw new InvalidOperationException($"[测试] {id} 按约定失败");
            return Task.FromResult(new FetchResult { NothingToDo = NothingToDo.Contains(id) });
        }
    }

    private static async Task<(RecordingDispatcher D, TaskRunResult R)> RunAsync(
        TaskRunArgs args, RecordingDispatcher? dispatcher = null)
    {
        var d = dispatcher ?? new RecordingDispatcher();
        var result = await new FetchYearTask(d).RunAsync(args, CancellationToken.None);
        return (d, result);
    }

    private static TaskRunArgs Years(int start, int? end = null, bool overwrite = false,
                                     int? maxItems = null, DateTime? deadline = null)
        => new(YearStart: start, YearEnd: end, OverwriteQfq: overwrite,
               MaxItems: maxItems, Deadline: deadline);

    // ── ① 顺序：有依赖，不能乱 ──

    [Fact]
    public async Task 退市名单要排在退市收尾之前()
    {
        var (d, _) = await RunAsync(Years(2016, 2018));

        var ids = d.Calls.Select(c => c.Id).ToList();
        Assert.True(ids.IndexOf(FetchActionId.StepDelistedSupplement)
                  < ids.IndexOf(FetchActionId.StepDelistedTails),
            "名单是收尾那一步的输入，晚一步就等于用旧名单");
    }

    /// <summary>day_adj 是拿不复权算的，板块指数又是拿 day_adj 算的——三者顺序不能乱。</summary>
    [Fact]
    public async Task 不复权在重算回测序列之前_板块指数最后()
    {
        var (d, _) = await RunAsync(Years(2016, 2018));

        var ids = d.Calls.Select(c => c.Id).ToList();
        Assert.True(ids.IndexOf(FetchActionId.StepStockRawBars) < ids.IndexOf(FetchActionId.RebuildAdjSeries));
        Assert.True(ids.IndexOf(FetchActionId.RebuildAdjSeries) < ids.IndexOf(FetchActionId.StepBoardIndex));
        Assert.Equal(FetchActionId.StepBoardIndex, ids[^1]);
    }

    // ── ② 参数：吃年份的才给年份 ──

    [Fact]
    public async Task 吃年份的项_拿到整段回补模式和年份区间()
    {
        var (d, _) = await RunAsync(Years(2016, 2018));

        var one = d.Calls.Single(c => c.Id == FetchActionId.StepStockDayBars).Args;
        Assert.True(one.Mode.HasFlag(FetchMode.FirstBackfill));
        Assert.Equal(2016, one.YearStart);
        Assert.Equal(2018, one.YearEnd);
    }

    /// <summary>
    /// 不吃年份的那几项**不能**拿到年份，也不能被切成整段回补模式：
    /// 退市名单是一份全量名单、没有年份维度；重算回测序列和板块指数是本地计算，读多少算多少。
    /// </summary>
    [Theory]
    [InlineData(FetchActionId.StepDelistedSupplement)]
    [InlineData(FetchActionId.StepDelistedTails)]
    [InlineData(FetchActionId.RebuildAdjSeries)]
    [InlineData(FetchActionId.StepBoardIndex)]
    public async Task 不吃年份的项_按日常语义跑(FetchActionId id)
    {
        var (d, _) = await RunAsync(Years(2016, 2018, overwrite: true));

        var one = d.Calls.Single(c => c.Id == id).Args;
        Assert.Null(one.YearStart);
        Assert.Null(one.YearEnd);
        Assert.False(one.Mode.HasFlag(FetchMode.FirstBackfill));
        Assert.False(one.OverwriteQfq);
    }

    [Fact]
    public async Task 结束年留空_按今年算()
    {
        var (d, _) = await RunAsync(Years(2016));

        Assert.Equal(DateTime.Today.Year,
                     d.Calls.First(c => c.Args.YearEnd != null).Args.YearEnd);
    }

    /// <summary>Deadline **往下传**：不然一个跑一小时的子任务会把整轮的空窗约定拖穿。</summary>
    [Fact]
    public async Task Deadline往下传()
    {
        var due = DateTime.Now.AddHours(1);

        var (d, _) = await RunAsync(Years(2016, 2018, deadline: due));

        Assert.All(d.Calls, c => Assert.Equal(due, c.Args.Deadline));
    }

    /// <summary>
    /// MaxItems **不往下传**：这一层的"批"是子任务，传下去会变成"每个子任务只做 N 批"，
    /// 语义完全不同（那会让每一项都只补一点点、看着像跑完了其实没有）。
    /// </summary>
    [Fact]
    public async Task MaxItems不往下传()
    {
        var (d, _) = await RunAsync(Years(2016, 2018, maxItems: 2));

        Assert.All(d.Calls, c => Assert.Null(c.Args.MaxItems));
    }

    /// <summary>MaxItems 截在**子任务之间**——一批＝一个子任务。</summary>
    [Fact]
    public async Task MaxItems截断在子任务之间()
    {
        var (d, result) = await RunAsync(Years(2016, 2018, maxItems: 3));

        Assert.Equal(3, d.Calls.Count);
        Assert.Equal(TaskState.Completed, result.State);
    }

    // ── ②″ 事件要**原样**转发出去 ──

    /// <summary>
    /// ⭐ 子任务的 <b>Quiet</b> 心跳必须原样传上去。
    ///
    /// 这是整个复合任务最容易做错的地方，而且错了**不会报**：Quiet 的进展是给静默看门狗喂心跳用的，
    /// 丢掉它，外层在看门狗眼里就是哑的——2026-09-22 第一版把子任务压扁成 <c>IProgress&lt;string&gt;</c>
    /// 转发，实测【补全退市名单】探测 1342 只连哑 5 分 3 秒，整轮被掐断、后面 8 项一项没跑。
    /// </summary>
    [Fact]
    public async Task 子任务的Quiet心跳_原样转发()
    {
        var d = new RecordingDispatcher();
        var task = new FetchYearTask(d);
        var got = new List<TaskProgress>();
        task.OnProgress += got.Add;
        d.Subs.Clear();

        // 订阅是在 RunAsync 里挂的，所以先跑一轮、再让子任务发
        await task.RunAsync(Years(2016, 2018), CancellationToken.None);
        d.Subs[0].EmitProgress(new TaskProgress("探测中 500/1342", 500, 1342, Quiet: true));

        var beat = got.LastOrDefault(p => p.Quiet);
        Assert.NotNull(beat);
        Assert.Contains("500/1342", beat!.Text);
        Assert.Equal(500, beat.Done);
        Assert.Equal(1342, beat.Total);
    }

    /// <summary>子任务没自报阶段名时，补上它的中文名——日志里看得出是哪一项在说话。</summary>
    [Fact]
    public async Task 子任务没报阶段名_补上它的名字()
    {
        var d = new RecordingDispatcher();
        var task = new FetchYearTask(d);
        var got = new List<TaskProgress>();
        task.OnProgress += got.Add;

        await task.RunAsync(Years(2016, 2018), CancellationToken.None);
        d.Subs[0].EmitProgress(new TaskProgress("抓取中"));

        Assert.Equal(FetchTaskCatalog.Info(d.Calls[0].Id).Name, got[^1].Phase);
    }

    /// <summary>"我还活着"那条也要转发（它不喂看门狗，但界面靠它知道还没死）。</summary>
    [Fact]
    public async Task 子任务的活着播报_也转发()
    {
        var d = new RecordingDispatcher();
        var task = new FetchYearTask(d);
        var got = new List<TaskLiveness>();
        task.OnLiveness += got.Add;

        await task.RunAsync(Years(2016, 2018), CancellationToken.None);
        d.Subs[0].EmitLiveness(new TaskLiveness("这一步已用时 3 分钟", TimeSpan.FromMinutes(3)));

        Assert.Contains(got, l => l.Text.Contains("3 分钟"));
    }

    /// <summary>
    /// ⚠ 子任务的**状态变化不能**转发：外层的状态由骨架发，
    /// 把子任务的 Completed 也播出去，订阅方会以为外层这一轮做完了。
    /// </summary>
    [Fact]
    public async Task 子任务的状态变化_不转发()
    {
        var d = new RecordingDispatcher();
        var task = new FetchYearTask(d);
        var got = new List<TaskStateChanged>();

        await task.RunAsync(Years(2016, 2018), CancellationToken.None);
        got.Clear();
        task.OnStateChanged += got.Add;
        d.Subs[0].EmitState(new TaskStateChanged(d.Calls[0].Id, TaskState.Completed));

        Assert.Empty(got);
    }

    // ── ③ 单项失败不带倒后面的 ──

    [Fact]
    public async Task 某一项失败_后面的照跑()
    {
        var d = new RecordingDispatcher { ThrowOn = { FetchActionId.StepEtfBars } };

        var (_, result) = await RunAsync(Years(2016, 2018), d);

        Assert.Contains(d.Calls, c => c.Id == FetchActionId.StepBoardIndex);
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Contains(result.Errors, e => e.Contains("ETF"));
    }

    [Fact]
    public async Task 全都没活可干_算NothingToDo()
    {
        var d = new RecordingDispatcher();
        foreach (var c in Enum.GetValues<FetchActionId>()) d.NothingToDo.Add(c);

        var (_, result) = await RunAsync(Years(2016, 2018), d);

        Assert.True(result.NothingToDo);
    }

    // ── ④ 年份填错就明确失败，别按错的年份跑一个多小时 ──

    [Fact]
    public async Task 没填起始年_失败()
    {
        var (d, result) = await RunAsync(new TaskRunArgs());

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Empty(d.Calls);
    }

    [Theory]
    [InlineData(1980, null)]                 // 早于 A股开市
    [InlineData(2018, 2016)]                 // 结束早于起始
    public async Task 年份不合法_失败且一个请求都不发(int start, int? end)
    {
        var (d, result) = await RunAsync(Years(start, end));

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Empty(d.Calls);
    }

    [Fact]
    public async Task 结束年晚于今年_失败()
    {
        var (_, result) = await RunAsync(Years(2016, DateTime.Today.Year + 1));

        Assert.Equal(TaskState.Failed, result.State);
    }
}
