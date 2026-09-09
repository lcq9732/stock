using System.Collections.Concurrent;

namespace StockPlatform.Scheduling;

/// <summary>占用表里的一项：谁在跑、占了哪些源、什么时候开始的、怎么叫停它。</summary>
public sealed record RunningTask(
    Guid Id,
    string Name,
    IReadOnlySet<DataSourceId> Sources,
    DateTime StartedAt,
    bool Manual,
    CancellationTokenSource Cts)
{
    public string SourcesText => Sources.Count == 0
        ? "本地计算" : string.Join("、", Sources.Select(DataSourceCatalog.NameOf));
}

/// <summary>
/// 「哪个数据源正被谁占着」的登记表（2026-09-04 新增）。
///
/// ════ 为什么需要它 ════
/// 原来"能不能再跑一个任务"由一个全局 <c>IsBusy</c> 标志决定：有任务在跑就一律拒绝。
/// 可【板块成分股】走东财 push2、要人守着过图片验证码，而【个股日K】走腾讯要跑一个半小时——
/// 这两个压根不抢同一个源，却因为那个全局标志只能排队，板块数据几天都追不上时效性
/// （用户 2026-09-04：1000 个板块跑一天才拿下 207 个）。
///
/// 这张表把"忙"从**一个布尔**换成**按源记账**：只要源不重叠就可以同时跑。
///
/// ════ 顺带解决了停止 ════
/// 表项里带着各自的 CTS，所以停一项就是拿它的 CTS 一 Cancel（界面上「正在执行」那一行的
/// 【停止】走的就是这条路，2026-09-08 起那也是停掉一项任务的唯一入口）；而任务是在自己的
/// <c>finally</c> 里释放登记的，**收尾做完才会从表里消失**——于是"表空了"天然等于
/// "全部真的停干净了"，不需要每个任务再手写一个返回 bool 的 Stop 方法（40 个手写方法漏一个
/// 就永远等不到那个 true）。
///
/// ════ 线程安全 ════
/// 计划线程和界面线程都会读写它，所以底下用 <see cref="ConcurrentDictionary{TKey,TValue}"/>，
/// 且**冲突检查和登记必须是一个原子动作**（见 <see cref="TryAcquire"/>）——分两步做的话，
/// 两个线程可能同时通过检查、同时登记同一个源。
/// </summary>
public sealed class SourceOccupancy
{
    private readonly ConcurrentDictionary<Guid, RunningTask> _running = new();

    /// <summary>登记或释放之后触发——界面据此刷新"当前占用"那块显示。</summary>
    public event Action? Changed;

    /// <summary>当前在跑的全部任务（快照，按开始时间排序）。</summary>
    public IReadOnlyList<RunningTask> Snapshot()
        => _running.Values.OrderBy(t => t.StartedAt).ToList();

    public bool AnyRunning => !_running.IsEmpty;

    /// <summary>
    /// 占住这些源并登记开跑。返回 null 表示**有源已被占用**，同时通过
    /// <paramref name="blockedBy"/> 告诉调用方是谁占着、占的是哪个源——界面要拿它写提示。
    ///
    /// 整个"检查 + 登记"在同一把锁里完成：分两步的话两个线程可能同时通过检查。
    /// 不占源的任务（本地计算）永远成功。
    /// </summary>
    public RunningTask? TryAcquire(
        string name, IReadOnlySet<DataSourceId> sources, bool manual,
        CancellationTokenSource cts, out RunningTask? blockedBy, out DataSourceId? blockedSource)
    {
        blockedBy = null;
        blockedSource = null;
        lock (_gate)
        {
            if (sources.Count > 0)
            {
                foreach (var t in _running.Values)
                {
                    foreach (var s in sources)
                    {
                        if (!t.Sources.Contains(s)) continue;
                        blockedBy = t;
                        blockedSource = s;
                        return null;
                    }
                }
            }
            var task = new RunningTask(Guid.NewGuid(), name, sources, DateTime.Now, manual, cts);
            _running[task.Id] = task;
            RaiseChanged();
            return task;
        }
    }

    /// <summary>释放登记。**必须放在 finally 里**——异常退出也要释放，否则那个源永久锁死。</summary>
    public void Release(RunningTask? task)
    {
        if (task == null) return;
        if (_running.TryRemove(task.Id, out _)) RaiseChanged();
    }

    /// <summary>叫停全部：逐个 Cancel。返回发出了几个停止信号。
    /// 注意它**不等**任务收尾——等的事情交给 <see cref="WaitAllStoppedAsync"/>。</summary>
    public int CancelAll()
    {
        int n = 0;
        foreach (var t in Snapshot())
        {
            try { t.Cts.Cancel(); n++; }
            catch (ObjectDisposedException) { /* 已经收工了，正常 */ }
        }
        return n;
    }

    /// <summary>
    /// 等到占用表清空——也就是所有任务都**收尾完毕**（它们在 finally 里才释放登记）。
    ///
    /// 返回 true = 全部停干净；false = 等到超时还有人没退出。后者要如实告诉用户，
    /// 不能假装停干净了：卡在不响应 CancellationToken 的同步调用里的任务，谁也掐不动它。
    /// </summary>
    public async Task<bool> WaitAllStoppedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.Now + timeout;
        while (AnyRunning)
        {
            if (DateTime.Now >= deadline) return false;
            if (ct.IsCancellationRequested) return false;
            await Task.Delay(200, CancellationToken.None);
        }
        return true;
    }

    /// <summary>
    /// 只叫停其中一项（界面上每行后面那个【停止】，2026-09-05）。
    /// 返回 false = 表里已经没有它了（刚好在点之前跑完），这时候不用再提示什么。
    ///
    /// 注意它**不动计划循环**：计划正在跑的那一项被单独停掉后，计划会当作这一项被取消、
    /// 接着跑后面的项。要让计划别再往下排，用【停止计划】（反过来那个也不会碰这里的任务）。
    /// </summary>
    public bool CancelOne(Guid id)
    {
        if (!_running.TryGetValue(id, out var t)) return false;
        try { t.Cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    private readonly object _gate = new();
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* 界面刷新失败不该影响抓取 */ }
    }
}
