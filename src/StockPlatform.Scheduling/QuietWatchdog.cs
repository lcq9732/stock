using System.Diagnostics;

namespace StockPlatform.Scheduling;

/// <summary>
/// 静默看门狗（2026-09-08）——判断一项任务是不是**真卡死**了。
///
/// ════ 它替掉了什么 ════
/// 原来是「单项硬超时」：预算＝实测中位数×4、夹在 [30分钟, 8小时]，到点无条件掐。
/// 那是拿"跑得久"当"卡死"的代理指标，而这两件事只是相关、不等价——【龙虎榜】切到
/// 「首次整段回补」之后要从 2002 年逐日抓到今天（两小时起步），可它的耗时样本全是
/// 增量模式留下的 2 秒，预算落在 30 分钟下限上，于是每天被掐一次；掐断走的是失败分支、
/// 不记耗时样本，所以样本永远停在 2 秒，第二天照掐。误杀 + 自锁死。
///
/// ════ 换成什么判据 ════
/// 「多久没有进展」。任务本来就在往 <see cref="IProgress{T}"/> 上报进度（"已抓 N 天"
/// "处理中 500/5876"），那是现成的活体信号：只要还在吐进度就说明它在往前走，跑五个小时
/// 也不该打断；真卡住的（卡在网络重试、卡在死循环里）就是彻底不出声，几分钟内必然暴露。
///
/// ════ 为什么判定还是放在外部 ════
/// 因为卡死的任务自己不会动——它内部没有任何一行代码有机会执行，指望它"自己发现自己卡了
/// 然后退出"是做不到的（尤其卡在一个不接受 CancellationToken 的同步调用上时）。所以活体
/// 判定必须由外面看，只是判据从"总时长"换成了"有没有心跳"。
///
/// ⚠ 掐得动的前提仍然是协作式取消：任务内部得真的在检查 token。卡在死等一把 SQLite 写锁
///   那种，这里发了信号也叫不醒——那种只能从任务内部修。
///
/// ════ 定时器心跳不能喂给它 ════
/// 有一类"我还在这一步，已用时 N 分钟（不是卡死）"的定时播报（见 Heartbeat），那种只证明
/// 进程活着、不证明有前进——卡在写锁上时它照样每 30 秒吐一句。要是把它接进这里，看门狗就
/// 成了摆设。所以那类走另一条通道（FetchOrchestrator.Liveness），不经过 <see cref="Wrap"/>。
/// </summary>
public sealed class QuietWatchdog : IDisposable
{
    /// <summary>默认静默上限。实测（2026-09-08 全天日志）任务内最长的一段静默是 2 分 35 秒
    /// （板块指数合成），最长的单次网络阻塞是 3 分钟（银行 PDF 下载的 HttpClient 超时），
    /// 5 分钟对两者都留了余量。放宽要按项来，见 <c>FetchActionInfo.MaxQuiet</c>。</summary>
    public static readonly TimeSpan DefaultMaxQuiet = TimeSpan.FromMinutes(5);

    /// <summary>多久查一次。查得比阈值勤就够了，没必要秒级——晚发现半分钟没有任何代价。</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>跑到这么久就说一声。**只提醒不掐**：跑得久本身不是错（整段回补就是要跑几小时），
    /// 但一项跑过大半天通常值得人看一眼，尤其是那种"每轮都有进展、却跨轮永远做不完"的活循环
    /// （看门狗看不出来，它确实一直在前进）。</summary>
    public static readonly TimeSpan DefaultLongRunNotice = TimeSpan.FromHours(4);

    private readonly TimeSpan _maxQuiet;
    private readonly TimeSpan _longRunNotice;
    private readonly Action<TimeSpan>? _onLongRun;
    private readonly CancellationTokenSource _quietCts = new();
    private readonly CancellationTokenSource _linked;
    private readonly Timer _timer;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly object _gate = new();

    private DateTime _lastBeat = DateTime.Now;
    private string? _lastMessage;
    private bool _longRunNoticed;

    /// <param name="maxQuiet">多久没有进展就判定卡死。</param>
    /// <param name="outer">外层令牌（用户按停止 / 程序退出），跟本表的令牌 link 在一起。</param>
    /// <param name="onLongRun">跑过 <paramref name="longRunNotice"/> 时回调一次，用来打提醒。</param>
    /// <param name="checkInterval">查询周期；测试传很小的值。</param>
    /// <param name="longRunNotice">跑多久算"久"。</param>
    public QuietWatchdog(
        TimeSpan maxQuiet, CancellationToken outer,
        Action<TimeSpan>? onLongRun = null,
        TimeSpan? checkInterval = null, TimeSpan? longRunNotice = null)
    {
        _maxQuiet = maxQuiet;
        _onLongRun = onLongRun;
        _longRunNotice = longRunNotice ?? DefaultLongRunNotice;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _quietCts.Token);

        // 周期不能比阈值还长，否则阈值形同虚设（单元测试把阈值设成几百毫秒时就会撞上）。
        var period = checkInterval ?? DefaultCheckInterval;
        if (period > maxQuiet) period = maxQuiet;
        _timer = new Timer(_ => Check(), null, period, period);
    }

    /// <summary>传给任务用的令牌：外层停止、或判定卡死，都会从这里取消。</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>是不是**这里**判定卡死掐的（用来跟"用户按了停止"区分）。</summary>
    public bool Starved { get; private set; }

    /// <summary>掐断时任务说的最后一句话。写进失败原因里——比"跑了 30 分钟"精确得多，
    /// 直接指向它停在哪一步。</summary>
    public string? LastMessage { get { lock (_gate) return _lastMessage; } }

    /// <summary>静默了多久（给日志用）。</summary>
    public TimeSpan QuietFor { get { lock (_gate) return DateTime.Now - _lastBeat; } }

    public TimeSpan MaxQuiet => _maxQuiet;

    /// <summary>
    /// 把 <paramref name="log"/> 包成一个会顺手记心跳的 <see cref="IProgress{T}"/>，
    /// 交给任务去用。
    ///
    /// ⚠ 心跳是**同步**记的、日志仍走 <see cref="Progress{T}"/> 异步派发：原来这里就是
    ///   <c>new Progress&lt;string&gt;(log)</c>，那东西的回调是排到线程池再执行的，拿它
    ///   当心跳时刻会有不确定的延迟；而把 log 也改成同步调用又会让写日志的耗时算进任务里。
    ///   分开处理，两边的语义都不变。
    /// </summary>
    public IProgress<string> Wrap(Action<string> log) => new BeatingProgress(this, new Progress<string>(log));

    /// <summary>
    /// 心跳和日志分开的口子（2026-09-19）——"我往前走了一步，但这一步不值得单独写一行日志"。
    ///
    /// 起因：【资金净流入】每 300 只才吐一句进度，而 300 只实测要 5 分半，比默认静默上限
    /// （5 分钟）还长——它一路正常抓着，却每天被判成卡死掐断（09-18、09-19 两轮）。
    /// 把日志打密十倍（每 30 只一行、186 行一轮）不合适，把 <see cref="DefaultMaxQuiet"/>
    /// 放宽又等于把"发现真卡死"一起推迟；所以让任务能报**只喂狗的进展**：
    /// 每批都喂，日志文本仍按原来的间隔打。
    ///
    /// ⚠ 这跟 <c>IFetchTask.OnLiveness</c> 不是一回事：那个是定时播报、**不能**喂狗
    /// （卡在写锁上时它照样每 30 秒吐一句）。这里喂的前提仍然是"真的做完了一批"。
    /// </summary>
    public interface IBeatOnlySink
    {
        /// <summary>记一次心跳，不写日志。</summary>
        void BeatOnly(string? message = null);
    }

    /// <summary>记一次心跳。任务每说一句话就是一次。</summary>
    public void Beat(string? message = null)
    {
        lock (_gate)
        {
            _lastBeat = DateTime.Now;
            if (message != null) _lastMessage = message;
        }
    }

    /// <summary>
    /// 定时检查。
    ///
    /// ⚠ <b>这是 <see cref="Timer"/> 的回调，跑在线程池线程上——所以整个方法体必须把异常
    /// 全吞掉</b>。线程池线程上的未捕获异常不是"记一笔错"，是**直接终止整个进程**：
    /// 看门狗的职责是发现别人卡死，它自己把宿主掀了是最坏的结果，而且现场极难认
    /// （崩在哪个线程、哪一刻全看 Timer 什么时候触发，跟当时在跑什么毫无关系）。
    ///
    /// 里面有两处会抛，各自单独兜，见下面的注释。
    /// </summary>
    private void Check()
    {
        try
        {
            TimeSpan? longRun = null;
            lock (_gate)
            {
                if (_quietCts.IsCancellationRequested) return;

                var ran = DateTime.Now - _startedAt;
                if (!_longRunNoticed && ran >= _longRunNotice)
                {
                    _longRunNoticed = true;
                    longRun = ran;
                }

                if (DateTime.Now - _lastBeat < _maxQuiet)
                {
                    // 提醒要在锁外发，别让回调把锁攥着
                    if (longRun == null) return;
                }
                else
                {
                    // ⚠ 先立旗再取消：catch 那边靠这个旗子认"是不是我掐的"，
                    //    顺序反了会有一瞬间读到 false，被当成普通异常。
                    Starved = true;
                }
            }

            // ⚠ 这是**外部代码**：PlanRunner 传的是写日志的委托，单测传的可能是
            //   非线程安全的 List.Add。它抛出来不该连累下面的掐断判定，所以单独兜。
            if (longRun is { } ran2)
            {
                try { _onLongRun?.Invoke(ran2); }
                catch (Exception ex) { Debug.WriteLine($"[QuietWatchdog] 长跑提醒回调抛异常已忽略：{ex.Message}"); }
            }

            if (Starved)
            {
                // ⚠ Cancel() 会**同步执行注册在这个令牌上的所有回调**，任何一个抛出都会被包成
                //   AggregateException 扔回来。原来这里只 catch ObjectDisposedException，
                //   于是别人注册的回调一抛，异常就从 Timer 回调逃出去、把进程一起带走。
                try { _quietCts.Cancel(); }
                catch (ObjectDisposedException) { /* 任务同时收工了，正常 */ }
                catch (AggregateException ex) { Debug.WriteLine($"[QuietWatchdog] 取消回调抛异常已忽略：{ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            // 兜底。上面每处都各自兜了，走到这儿说明是没预料到的——但宁可**漏报一次卡死**，
            // 也不能让看门狗把进程掀了：漏报的代价是那一项多跑一会儿，掀进程的代价是全丢。
            Debug.WriteLine($"[QuietWatchdog] 检查本身抛异常已忽略：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _linked.Dispose();
        _quietCts.Dispose();
    }

    private sealed class BeatingProgress(QuietWatchdog dog, IProgress<string> inner)
        : IProgress<string>, IBeatOnlySink
    {
        public void Report(string value)
        {
            dog.Beat(value);
            inner.Report(value);
        }

        /// <summary>只喂狗，不往日志走（见 <see cref="IBeatOnlySink"/>）。</summary>
        public void BeatOnly(string? message = null) => dog.Beat(message);
    }
}
