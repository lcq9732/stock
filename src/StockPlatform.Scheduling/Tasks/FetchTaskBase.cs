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

    /// <summary>报一条"我还活着"（看门狗**不**吃这个，见 <see cref="IFetchTask.OnLiveness"/>）。</summary>
    protected void ReportLiveness(string text, TimeSpan elapsed)
        => Raise(OnLiveness, new TaskLiveness(text, elapsed));

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
