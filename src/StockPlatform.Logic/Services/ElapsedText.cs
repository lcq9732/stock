namespace StockPlatform.Logic.Services;

/// <summary>
/// 用时的显示格式（2026-09-10 提到这里）。
///
/// 原来 <c>FormatElapsed</c> 在 <c>FetchOrchestrator</c>（34 处调用）和 <c>FullAuditTask</c> 各有
/// 一份一模一样的私有实现，再迁一个任务就是第三份。两处的调用点都留着不动、只让它们转发到这里，
/// 这样既去掉了重复的逻辑，又不至于为了一个格式化函数改 34 行。
/// </summary>
public static class ElapsedText
{
    /// <summary>
    /// 不足一小时报 <c>mm:ss</c>，够一小时报 <c>h:mm:ss</c>。
    /// 不用 <c>TimeSpan.ToString</c> 是因为那个会把不足一小时的也写成 <c>00:48:48</c>，
    /// 前面那两个零在日志里白占位置。
    /// </summary>
    public static string Format(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
}
