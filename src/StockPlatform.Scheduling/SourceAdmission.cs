namespace StockPlatform.Scheduling;

/// <summary>准入的结果类型——调用方据此决定怎么显示、怎么记结果。</summary>
public enum AdmissionKind
{
    /// <summary>直接拿到了，没等也没抢。</summary>
    Acquired,
    /// <summary>抢占了别人之后拿到的。</summary>
    AcquiredAfterPreempt,
    /// <summary>这一轮让路（不算失败，下一轮重扫再来）。</summary>
    GaveWay,
    /// <summary>抢了但对方在时限内没停下来——**没有硬上**，这一轮放弃。</summary>
    PreemptTimedOut,
}

/// <summary>
/// 一次准入的结果。<see cref="Lease"/> 非空才表示可以开跑。
/// </summary>
/// <param name="Kind">哪种结果。</param>
/// <param name="Lease">拿到的占用登记；null＝这一轮不跑。</param>
/// <param name="Reason">人话说明。让路/超时时会被记进 <c>SkippedReason</c>。</param>
/// <param name="Blocker">挡住我们的那个任务名（没被挡就是 null）。</param>
/// <param name="BlockedSource">撞上的那个数据源（没被挡就是 null）。</param>
public readonly record struct AdmissionResult(
    AdmissionKind Kind,
    RunningTask? Lease,
    string Reason,
    string? Blocker,
    DataSourceId? BlockedSource);

/// <summary>
/// 「这一项现在能不能开跑」的裁决（2026-09-05 从 MainViewModel 抽出来）。
///
/// ════ 为什么单独成类 ════
/// 这套逻辑原来长在 <c>MainViewModel</c> 里，而那个类**测不了**：它要一个有 17 个必需依赖的
/// <c>FetchOrchestrator</c>，所在的 Fetcher 项目又是 WinExe + SelfContained + 单文件
/// （测试项目一引用就撞 NETSDK1151）。于是抢占这么要紧的一段——它有权把用户正在跑的任务停掉
/// ——一行自动化测试都盖不到。搬到这儿之后，整条路径（让路/抢占/超时/被第三方截胡）
/// 都能用假任务在毫秒级跑完，不碰网络、不碰界面。
///
/// UI 的部分**没跟过来**：状态栏文案、行状态、日志格式仍归 ViewModel，
/// 它拿 <see cref="AdmissionResult"/> 自己去渲染。这个类只做决定，不管怎么显示。
///
/// ════ 规则（2026-09-05 跟用户讨论定的）════
/// 计划的**定时项**优先于手工任务：到点了就把占着源的手工任务停掉，等它收尾，然后自己上。
/// 三条边界跟最初设想不同，理由都写在下面的常量和分支上：
///   · 只有**定时项**抢占，空闲项让路——空闲项语义就是"有空才补"，
///     没理由为它掐掉用户主动点的东西（2026-09-05 被误伤的两项恰好都是空闲项）；
///   · 等对方收尾最多 <see cref="WaitLimit"/>，**不是 30 分钟**——实测正常停止是秒级
///     （限流等待、网络请求全都带 ct），只有【优化数据库】的大索引是分钟级；
///   · 等不到就**放弃这一轮，绝不硬上**——停不下来只可能是死锁，那时候再启动一个任务，
///     等于让两个任务同时打同一个数据源、同时写同一张表，而占用机制存在的全部意义就是防这个。
/// </summary>
public sealed class SourceAdmission
{
    /// <summary>抢占后等对方收尾最多等这么久。2 分钟给【优化数据库】的大索引留足余量。</summary>
    public static readonly TimeSpan DefaultWaitLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 同一个任务被抢占后，这段时间内不再抢它——防拉锯。
    /// 没有这道闸：用户被抢占后重新点一次，下一个计划项到点又抢，来回拉扯谁也跑不完。
    /// </summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(5);

    private readonly SourceOccupancy _occupancy;
    private readonly Action<string>? _log;
    private readonly TimeSpan _waitLimit;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _pollInterval;

    /// <summary>被抢占过的任务名 → 那一刻。只活在内存里，重启清零。</summary>
    private readonly Dictionary<string, DateTime> _lastPreempted = new();
    private readonly object _gate = new();

    /// <param name="waitLimit">抢占后等对方收尾的上限；测试传很小的值。</param>
    /// <param name="cooldown">同一个被抢占者的冷却期。</param>
    /// <param name="pollInterval">等待期间多久探一次占用表。</param>
    public SourceAdmission(SourceOccupancy occupancy, Action<string>? log = null,
                           TimeSpan? waitLimit = null, TimeSpan? cooldown = null,
                           TimeSpan? pollInterval = null)
    {
        _occupancy = occupancy;
        _log = log;
        _waitLimit = waitLimit ?? DefaultWaitLimit;
        _cooldown = cooldown ?? DefaultCooldown;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// 拿数据源占用；拿不到时按规则决定**抢占**还是**让路**。
    /// </summary>
    /// <param name="taskName">要跑的这一项叫什么（占用表里就用这个名字）。</param>
    /// <param name="sources">它要用哪些数据源。</param>
    /// <param name="isTimedItem">是不是**定时项**（到点必须跑）。空闲项传 false。</param>
    /// <param name="fromPlan">是不是**计划**在跑。手动点【执行】传 false。</param>
    /// <param name="itemCts">这一项自己的取消源，登记进占用表用（别人要停它就靠它）。</param>
    /// <param name="progress">过程说明，给日志/界面。</param>
    /// <param name="onPreemptStart">
    /// 真的要抢占了、发出停止信号**之前**回调一次，参数是被抢者的名字。
    /// 给界面用——这一段可能要等上两分钟，不说一声界面看着像卡住了。
    /// 这个类自己不碰 UI，所以留成钩子。
    /// </param>
    public async Task<AdmissionResult> AcquireAsync(
        string taskName, IReadOnlySet<DataSourceId> sources,
        bool isTimedItem, bool fromPlan,
        CancellationTokenSource itemCts, IProgress<string>? progress = null,
        CancellationToken ct = default, Action<string>? onPreemptStart = null)
    {
        RunningTask? TryNow(out RunningTask? blockedBy, out DataSourceId? blockedSource) =>
            _occupancy.TryAcquire(taskName, sources, manual: !fromPlan, itemCts,
                                  out blockedBy, out blockedSource);

        // 原来这里打头还有一道 manualBigTaskRunning：【手动】页那几个大按钮横跨所有数据源、
        // 又不进占用表，只能靠一个外部标志让路。2026-09-08 那一页整个撤掉之后，
        // **每个任务都按源登记在占用表里**，判据只剩下面这一套，不再有账外的任务。
        var lease = TryNow(out var blocker, out var source);
        if (lease != null)
            return new AdmissionResult(AdmissionKind.Acquired, lease, "", null, null);

        var sourceName = DataSourceCatalog.NameOf(source!.Value);
        var blockerName = blocker!.Name;

        // ② 够不够格抢占
        bool cooling;
        lock (_gate)
            cooling = _lastPreempted.TryGetValue(blockerName, out var last)
                      && DateTime.Now - last < _cooldown;

        if (!fromPlan || !isTimedItem || cooling)
        {
            var why = !fromPlan ? "手动触发的项不抢占"
                    : !isTimedItem ? "空闲项不抢占（它本来就是有空才补的）"
                    : $"【{blockerName}】刚被抢占过，{_cooldown.TotalMinutes:0} 分钟内不再抢";
            progress?.Report($"　{sourceName} 正被【{blockerName}】占着——{why}，这一轮让路，下次重扫再来。");
            return new AdmissionResult(AdmissionKind.GaveWay, null,
                $"{sourceName} 正被【{blockerName}】占着，本轮让路（{why}）", blockerName, source);
        }

        // ③ 抢占：先礼后兵——发停止信号，等它自己收尾
        lock (_gate) _lastPreempted[blockerName] = DateTime.Now;
        try { onPreemptStart?.Invoke(blockerName); }
        catch { /* 界面回调出事不能把抢占带塌 */ }
        var ranFor = DateTime.Now - blocker.StartedAt;
        _log?.Invoke($"⏫ 计划抢占：【{taskName}】到点要跑，但 {sourceName} 被【{blockerName}】占着"
                   + $"（已跑 {ranFor.TotalMinutes:F0} 分钟）——正在停止它，已抓到的数据不会丢。");

        try { blocker.Cts.Cancel(); }
        catch (Exception ex) { _log?.Invoke($"　给【{blockerName}】发停止信号时出错：{ex.Message}"); }

        // 等它从占用表里消失。任务是在自己的 finally 里释放登记的，
        // 所以"从表里消失"＝它真的收尾干净了，不是猜的。
        var startedWaiting = DateTime.Now;
        var deadline = startedWaiting + _waitLimit;
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(_pollInterval, ct);

            lease = TryNow(out var stillBlocker, out _);
            if (lease != null)
            {
                var waited = DateTime.Now - startedWaiting;
                _log?.Invoke($"　【{blockerName}】已停止（等了 {waited.TotalSeconds:F0} 秒），"
                           + $"【{taskName}】接着跑。");
                return new AdmissionResult(AdmissionKind.AcquiredAfterPreempt, lease,
                    $"抢占了【{blockerName}】", blockerName, source);
            }

            // 被**别人**抢先占了（不是原来那个）——不再连环抢占，让路
            if (stillBlocker != null && stillBlocker.Name != blockerName)
            {
                progress?.Report($"　{sourceName} 又被【{stillBlocker.Name}】占上了，这一轮让路。");
                return new AdmissionResult(AdmissionKind.GaveWay, null,
                    $"{sourceName} 被【{stillBlocker.Name}】占着，本轮让路", stillBlocker.Name, source);
            }
        }

        // ④ 等不到——**不硬上**
        _log?.Invoke($"⚠ 计划抢占失败：【{blockerName}】收到停止信号后 {_waitLimit.TotalMinutes:0} 分钟还没退出，"
                   + $"多半是卡死了（正常停止是秒级的）。【{taskName}】这一轮放弃，下次重扫再试。"
                   + $"→ 请去日志里看【{blockerName}】最后停在哪一步，这是个需要修的 bug。");
        return new AdmissionResult(AdmissionKind.PreemptTimedOut, null,
            $"抢占【{blockerName}】超时，它停不下来（疑似卡死），本轮放弃", blockerName, source);
    }
}
