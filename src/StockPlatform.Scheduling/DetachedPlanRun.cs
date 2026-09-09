using StockPlatform.Data.Orchestration;

namespace StockPlatform.Scheduling;

/// <summary>
/// 一项**脱离了计划、但还在跑**的任务（2026-09-08）。
///
/// ════ 它是干什么用的 ════
/// 【停止计划】的语义是"别再挑下一项了"，**不是"把正在抓的东西掐掉"**（用户 2026-09-08 定的：
/// 计划只是排期，跟"此刻有没有任务在做"是两回事）。于是点停止之后会出现一个中间态：
/// 计划已经显示未运行，而那一项还在抓——它就变成这里说的"脱离运行"。
///
/// 脱离期间它并不失管：
///   · 界面上它照常列在「正在执行」里，那一行的【停止】随时能停它；
///   · 跑完之后要照常记账（结果、耗时样本、日志、那一行从"执行中"变回结果）。
///     这件事由**谁**来做取决于计划回没回来，见下面的 <see cref="TryTakeOver"/>。
///
/// ════ 记账权只能有一个 ════
/// 两个候选：① 挂在任务上的后台延续（没人来认领时自己收尾）；② 重新开始的计划（把它认领回去，
/// 当成自己的当前项等它跑完）。两者可能同时发生——任务正好在用户点【开始执行计划】那一刻结束。
/// 所以用一个原子标志抢：抢到的负责记账，另一个直接放手。双份记账会把同一项写两遍
/// 日志和耗时样本，耗时样本进了中位数就会影响以后的空闲调度。
/// </summary>
public sealed class DetachedPlanRun(
    FetchPlanItem item, Task<FetchResult> task, QuietWatchdog dog, DateTime? deadline)
{
    private int _taken;

    public FetchPlanItem Item { get; } = item;

    /// <summary>任务本体。**已经在跑了**——脱离只是没人 await 它。</summary>
    public Task<FetchResult> Task { get; } = task;

    /// <summary>它的静默看门狗。收尾时要读 LastMessage / Starved，也由收尾方 Dispose。</summary>
    public QuietWatchdog Dog { get; } = dog;

    /// <summary>空闲项那条路上的"要在几点前收尾"，认领后接着用。</summary>
    public DateTime? Deadline { get; } = deadline;

    /// <summary>抢记账权。返回 true 的那一方负责收尾（包括 Dispose 看门狗）。</summary>
    public bool TryTakeOver() => Interlocked.Exchange(ref _taken, 1) == 0;
}

/// <summary>
/// 脱离运行的交接处：<see cref="PlanRunner"/> 停止时往里放，下一次开始时从里取。
///
/// 为什么不放在 PlanRunner 自己身上：每点一次【开始执行计划】就是一个**新的** PlanRunner，
/// 上一个已经返回了。要让新的那个认领上一个留下的任务，这份清单就得活在两者之外
/// （宿主 MainViewModel 持有一份，构造 runner 时传进去）。
///
/// ⚠ 只装**计划自己跑的**那一项。手动点某一行【执行】、或【执行整组】跑起来的不进这里——
/// 那些本来就不在计划序列上，计划没有理由去接管它们（用户 2026-09-08 明确区分过）。
/// </summary>
public sealed class DetachedPlanRuns
{
    private readonly List<DetachedPlanRun> _runs = [];
    private readonly object _gate = new();

    public void Add(DetachedPlanRun run)
    {
        lock (_gate) _runs.Add(run);
    }

    /// <summary>取走全部（并清空）。给新一轮计划认领用。</summary>
    public List<DetachedPlanRun> TakeAll()
    {
        lock (_gate)
        {
            var copy = new List<DetachedPlanRun>(_runs);
            _runs.Clear();
            return copy;
        }
    }

    /// <summary>还挂着几项没人认领（只给日志和测试看）。</summary>
    public int Count { get { lock (_gate) return _runs.Count; } }
}

/// <summary>
/// **这一项被人单独停掉了**（右上角「正在执行」里那一行的【停止】）。
///
/// 为什么要一个专门的异常类型：光看 <see cref="OperationCanceledException"/> 分不出是谁停的——
/// HttpClient 超时抛的也是它（TaskCanceledException），而两者的处置完全相反：
///   · 人停的 → 记「已取消」，是预期内的操作；
///   · 网络抖动 → 记「失败」，那是真出了问题，人得知道。
/// 也不能靠比较 token：任务实际拿到的是**再往下 link 一层**的令牌（占用表里那个 itemCts），
/// 跟计划这一层手里的不是同一个对象，比出来永远是 false（2026-09-08 就这么踩过）。
/// 所以由知情的那一方（执行入口）明确抛出来。
/// </summary>
public sealed class PlanItemStoppedException(string itemName)
    : OperationCanceledException($"【{itemName}】被单独停止")
{
    public string ItemName { get; } = itemName;
}
