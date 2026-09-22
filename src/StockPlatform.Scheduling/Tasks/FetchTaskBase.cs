using System.Diagnostics;

namespace StockPlatform.Scheduling.Tasks;

/// <summary>
/// 新式任务的骨架（2026-09-08）。子类只写三件事：**抓什么、怎么存、抓完之后做什么**。
///
/// 骨架统一处理的四件横切的事：
///   ① 状态事件的发射（Running → Completed/Failed/Stopped）
///   ② 流式"抓一批存一批"——理由见设计文档 3.4：个股日K 1,300 万行，先抓完再存是几个 GB；
///      分档资金流跑 3 小时，中途停止意味着三小时全白费。抓一批存一批才能"停在哪都不丢"
///   ③ <see cref="TaskRunArgs.MaxItems"/>（分批跑）和 <see cref="TaskRunArgs.Deadline"/>
///      （空闲窗口收尾）——每个子类各写一遍就会各写错一遍
///   ④ 异常翻译成 <see cref="TaskState.Failed"/>，取消翻译成 <see cref="TaskState.Stopped"/>
///
/// 骨架**不做**准入和数据源占用登记——那些在调度侧（见 <see cref="IFetchTask"/> 的类注释）。
/// </summary>
/// <typeparam name="TItem">一条数据的类型。批＝<c>IReadOnlyList&lt;TItem&gt;</c>。</typeparam>
public abstract class FetchTaskBase<TItem> : IFetchTask
{
    public abstract FetchActionId Id { get; }

    /// <inheritdoc cref="IFetchTask.HandlesBacklog"/>
    public virtual bool HandlesBacklog => false;

    public event Action<TaskProgress>? OnProgress;
    public event Action<TaskLiveness>? OnLiveness;
    public event Action<TaskStateChanged>? OnStateChanged;

    // ─────────────────── 子类要实现的 ───────────────────

    /// <summary>
    /// 从数据源抓，**流式产出**：每 yield 一批，骨架就存一批。
    /// 一批多大由子类定（一个月的日历、2000 行席位、一只票的K线）。
    /// </summary>
    protected abstract IAsyncEnumerable<IReadOnlyList<TItem>> FetchAsync(
        TaskRunArgs args, CancellationToken ct);

    /// <summary>存一批。</summary>
    protected abstract Task SaveBatchAsync(IReadOnlyList<TItem> batch, CancellationToken ct);

    /// <summary>
    /// 全部抓完之后（正常结束这一路）。水位线更新、对账、副产物计算都放这。
    /// 返回 null＝按默认结果（Completed，条数为 0 时算 NothingToDo）。
    /// </summary>
    protected virtual Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
        => Task.FromResult<TaskRunResult?>(null);

    /// <summary>
    /// 被取消时的收尾。**已落库的部分是有效的**，默认什么都不做（流式落库天然就是断点续）。
    /// 要记"下次从哪接"的任务在这里记。⚠ 这时 ct 已经取消了，别在这里 await 带 ct 的调用。
    /// </summary>
    protected virtual Task OnStoppedAsync(TaskRunStats stats) => Task.CompletedTask;

    // ─────────────────── 给子类用的发射器 ───────────────────

    /// <summary>报一条真进展（看门狗吃这个）。</summary>
    protected void Report(string text, int? done = null, int? total = null, string? phase = null)
        => Raise(OnProgress, new TaskProgress(text, done, total, phase));

    /// <summary>
    /// 报一条真进展，但**只喂看门狗、不写日志**（2026-09-19）。
    ///
    /// 给"一批 30 秒、要跑几百批"的活用：每批都报这个，日志文本仍按稀疏间隔用
    /// <see cref="Report"/> 打。不这么分开的话只有两个坏选择——日志刷屏，
    /// 或者像【资金净流入】那样每 300 只才出声、被 5 分钟静默上限误判成卡死。
    ///
    /// ⚠ 不是 <see cref="ReportLiveness"/>：那个是定时播报、不证明有前进、看门狗不吃。
    ///    这里的前提仍然是"真的做完了一批"。
    /// </summary>
    protected void ReportQuiet(string text, int? done = null, int? total = null, string? phase = null)
        => Raise(OnProgress, new TaskProgress(text, done, total, phase, Quiet: true));

    /// <summary>报一条"我还活着"（看门狗**不**吃这个，见 <see cref="IFetchTask.OnLiveness"/>）。</summary>
    protected void ReportLiveness(string text, TimeSpan elapsed)
        => Raise(OnLiveness, new TaskLiveness(text, elapsed));

    /// <summary>
    /// 把 <see cref="Report"/> 包成 <c>IProgress&lt;string&gt;</c>：
    /// <c>ProgressThrottle</c> 和那些还在用 <c>OnStatus</c>／<c>IProgress</c> 的 provider 要的是这个形状。
    ///
    /// 不用 <c>Progress&lt;string&gt;</c>：那个是异步 post 的，几十分钟的循环里日志顺序会乱。
    ///
    /// ⚠ 它只服务 **provider**（那些还在用 <c>OnStatus</c>／<c>IProgress</c> 的取数类）。
    /// **别拿它转发子任务**——它是个压扁成字符串的通道，<see cref="TaskProgress.Quiet"/>、
    /// Done/Total、Phase、<see cref="IFetchTask.OnLiveness"/> 全都过不来。
    /// 任务套任务用 <see cref="ForwardFrom"/>。
    /// </summary>
    protected IProgress<string> ProgressSink => _sink ??= new ReportSink(Report);
    private IProgress<string>? _sink;

    private sealed class ReportSink(Action<string, int?, int?, string?> report) : IProgress<string>
    {
        public void Report(string value) => report(value, null, null, null);
    }

    /// <summary>
    /// 订阅数据源的状态播报并转成日志（2026-09-22）——限流退避、重试、"未发任何请求"那类。
    ///
    /// ⚠ **不订阅的代价是静默**：那些消息大多来自限流器（<c>RateLimiter.OnStatus</c>），
    /// 被退避/重试时日志里一个字都没有，抓得慢看着就像卡住。
    /// 2026-09-22 查出资金净流入／融资余额／龙虎榜／大宗交易四项一直没订阅——
    /// 它们都已经是新框架任务，不会随别的迁移自动补上。
    ///
    /// 事件不能当参数传，所以收两个委托：<c>h =&gt; provider.OnStatus += h</c> / <c>-=</c>。
    /// 用 <c>using</c> 保证退订——provider 实例是注册时建的、跨轮复用，漏退订就是重复日志加内存泄漏。
    /// </summary>
    protected IDisposable ForwardStatus(Action<Action<string>> add, Action<Action<string>> remove)
    {
        void Forward(string m) => Report(m);
        add(Forward);
        return new StatusUnsubscriber(() => remove(Forward));
    }

    private sealed class StatusUnsubscriber(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>
    /// **任务套任务**：把子任务的事件原样转发出去（2026-09-22）。
    ///
    /// 这是新框架本来就支持的形状——任务是独立的，别人调用时只要转发它的事件即可
    /// （注册表的 <c>RunAsync</c> 一直留着 <c>subscribe</c> 这个口子）。
    ///
    /// ⚠ **必须转发事件、不能退化成 <see cref="ProgressSink"/>**：那个通道只有一个 string，
    /// <see cref="TaskProgress.Quiet"/> 会在那一层丢掉，于是靠 <see cref="ReportQuiet"/> 喂狗的
    /// 子任务（"一批很快、但要跑几百批"那类）在外层看来是**哑的**。
    /// 2026-09-22 实测：【拉取区间数据】分派到【补全退市名单】，探测 1342 只连哑 5 分 3 秒，
    /// 整轮被静默看门狗掐断、后面 8 项一项没跑。
    ///
    /// ⚠ **不转发 <see cref="IFetchTask.OnStateChanged"/>**：外层任务的状态由骨架发，
    /// 把子任务的 Completed 也播出去，订阅方会以为外层这一轮做完了。
    /// </summary>
    /// <param name="sub">子任务。</param>
    /// <param name="phase">子任务没自报阶段名时，用它当阶段名（通常是子任务的中文名）。</param>
    protected void ForwardFrom(IFetchTask sub, string? phase = null)
    {
        sub.OnProgress += p => Raise(OnProgress,
            phase is null || p.Phase != null ? p : p with { Phase = phase });
        sub.OnLiveness += l => Raise(OnLiveness, l);
    }

    /// <summary>
    /// 逐个订阅者隔离地发。一个订阅者抛异常会中断多播链——后面的订阅者收不到，异常还会冒进
    /// 任务里把它整成失败。所以这里逐个调、逐个吞。
    /// </summary>
    private static void Raise<T>(Action<T>? handlers, T payload)
    {
        if (handlers == null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try { h(payload); }
            catch (Exception ex) { Debug.WriteLine($"[FetchTask] 订阅者抛异常已忽略：{ex.Message}"); }
        }
    }

    // ─────────────────── 生命周期 ───────────────────

    public async Task<TaskRunResult> RunAsync(TaskRunArgs args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int batches = 0, items = 0;
        Raise(OnStateChanged, new TaskStateChanged(Id, TaskState.Running));

        try
        {
            await foreach (var batch in FetchAsync(args, ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (batch.Count > 0)
                {
                    await SaveBatchAsync(batch, ct);
                    batches++;
                    items += batch.Count;
                }

                // 分批跑的两个上限。放在存完之后判：已经抓回来的这一批不能丢。
                if (args.MaxItems is { } max && batches >= max)
                {
                    Report($"本轮已做满 {max} 批，收尾（下轮接着来）");
                    break;
                }
                if (args.Deadline is { } due && DateTime.Now >= due)
                {
                    Report($"到了空窗截止时刻 {due:HH:mm}，收尾（下轮接着来）");
                    break;
                }
            }

            var stats = new TaskRunStats(batches, items, sw.Elapsed);
            var result = await OnCompletedAsync(stats, args, ct)
                         ?? TaskRunResult.Ok(nothingToDo: items == 0);
            Raise(OnStateChanged, new TaskStateChanged(Id, result.State));
            return result;
        }
        catch (OperationCanceledException)
        {
            await OnStoppedAsync(new TaskRunStats(batches, items, sw.Elapsed));
            Raise(OnStateChanged, new TaskStateChanged(Id, TaskState.Stopped, $"已落库 {items} 条"));
            throw;   // 取消照旧往上抛：现有引擎靠它区分"被停止"和"失败"
        }
        catch (Exception ex)
        {
            Raise(OnStateChanged, new TaskStateChanged(Id, TaskState.Failed, ex.Message));
            return new TaskRunResult(TaskState.Failed, [$"{FetchTaskCatalog.Info(Id).Name}：{ex.Message}"]);
        }
    }
}
