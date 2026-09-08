using System.Diagnostics;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 黑盒步骤的定时播报（2026-09-08）——"我还在这一步，已经多久了"。
///
/// ════ 用在哪 ════
/// 整段是一次没法插进度的同步调用：<c>CREATE INDEX</c>、<c>ANALYZE</c> 这种。界面上什么都不动
/// 十几分钟，人会以为死了，所以每隔一会儿说一句。
///
/// ════ ⚠ 它不是心跳 ════
/// 这条播报只证明**进程还活着**，不证明那句调用有在前进——任务卡在一把 SQLite 写锁上时，
/// 定时器照样每 30 秒吐一句。所以它绝不能喂给 <c>QuietWatchdog</c>，否则看门狗就成了摆设：
/// 一个真卡死的任务会靠自己的定时器把自己"证明"成健康的。
///
/// 通道上就分开了：这里写的是 <see cref="FetchOrchestrator.Liveness"/>（只进日志），
/// 而心跳走 <c>IProgress&lt;string&gt;</c>。两者别接到一起。
/// </summary>
public sealed class Heartbeat : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly Timer _timer;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private Heartbeat(Action<string> say, string what, TimeSpan interval)
    {
        _timer = new Timer(_ =>
        {
            var t = _sw.Elapsed;
            var used = t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分钟" : $"{(int)t.TotalSeconds} 秒";
            say($"　仍在{what}，已用时 {used}（不是卡死，这一步中间没法报进度）");
        }, null, interval, interval);
    }

    /// <summary>开始播报；<paramref name="say"/> 为 null 时返回一个什么都不做的壳，
    /// 调用方不用到处判空。</summary>
    public static IDisposable Start(Action<string>? say, string what, TimeSpan? interval = null)
        => say == null ? new Noop() : new Heartbeat(say, what, interval ?? DefaultInterval);

    public void Dispose() => _timer.Dispose();

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}
