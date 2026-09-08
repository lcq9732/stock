namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 按**时间**节流的进度上报（2026-09-08）。
///
/// 原来那些长循环是按个数报的（"每 40 个板块一句""每 500 只一句"），问题是单位耗时差得很远：
/// 【重算回测序列】里"增量"的票几毫秒就过、"整段重算"的要几百毫秒，同样 500 只，快的 3 秒、
/// 慢的两分半。于是静默时长完全不可控，实测能哑到 2 分 24 秒——而这正是判断"卡没卡死"要看的量
/// （见 <c>QuietWatchdog</c>）。按时间报就把这个量钉死了：最长静默＝间隔，跟单位耗时无关。
///
/// 传 lambda 而不是拼好的字符串：没到点的时候连字符串都不拼，几十万次循环里这点开销不该白花。
/// </summary>
public sealed class ProgressThrottle(IProgress<string>? inner, TimeSpan interval)
{
    /// <summary>本地长循环的默认间隔。看门狗默认阈值是 5 分钟，30 秒留了十倍余量，
    /// 同时日志也不会被刷屏。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    // 从"现在"起算，所以循环刚开头不会立刻重复报一遍——那句"开始…"通常刚说过。
    private DateTime _last = DateTime.Now;

    public ProgressThrottle(IProgress<string>? inner) : this(inner, DefaultInterval) { }

    /// <summary>到点了才报。</summary>
    public void Report(Func<string> message)
    {
        if (inner == null) return;
        var now = DateTime.Now;
        if (now - _last < interval) return;
        _last = now;
        inner.Report(message());
    }

    /// <summary>无视节流立刻报（阶段收尾的汇总用），并重置计时。</summary>
    public void ReportNow(string message)
    {
        if (inner == null) return;
        _last = DateTime.Now;
        inner.Report(message);
    }
}
