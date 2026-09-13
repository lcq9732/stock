namespace StockPlatform.Logic.Services;

/// <summary>
/// 拼【观察项】页左表那一列：**日期 + 事项 + 公告里的数据**。纯字符串处理，可单测。
///
/// 目标形状（2026-09-11 按用户给的样例定的）：
/// <code>
/// 2026-11-08（还有 58 天） 限售解禁 100 万股，占流通 2%
/// </code>
///
/// ⚠ **日期排最前面**：左表是一份待办，人先看"什么时候"再看"什么事"；
/// 日期埋在句子中间的话，得逐行读完才能排出优先级。
/// </summary>
public static class WatchItemDisplay
{
    /// <param name="reason">事项名，如「限售解禁」。</param>
    /// <param name="detail">
    /// 公告里的数据，一般形如「2026-11-08（还有 58 天），100 万股，占流通 2%」。
    /// 取不到就传 null/空——那时只显示事项名，不编数据。
    /// </param>
    public static string Compose(string reason, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return reason;
        detail = detail.Trim();

        // 不是日期开头的（比如回购的「进展（已回购 8.5 亿元）」），保持 "事项：内容"
        if (!char.IsDigit(detail[0])) return $"{reason}：{detail}";

        var cut = detail.IndexOf('，');
        if (cut <= 0) return $"{detail} {reason}";          // 只有日期，没有别的数据

        var head = detail[..cut].Trim();                     // 日期段
        var rest = detail[(cut + 1)..].Trim();               // 剩下的数据
        return rest.Length == 0 ? $"{head} {reason}" : $"{head} {reason} {rest}";
    }
}
