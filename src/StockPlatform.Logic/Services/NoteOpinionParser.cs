namespace StockPlatform.Logic.Services;

/// <summary>
/// 从个股笔记（<c>notes/{code}.md</c>）里摘出**一句话的个人观点**，
/// 给【观察项】页那列显示（2026-09-11）。**纯字符串处理，可直接单测。**
///
/// ════ 约定：正文里以「观点：」开头的那一行 ════
/// 行首可以带 markdown 的 <c>&gt;</c>、<c>-</c>、<c>*</c>、<c>#</c> 和空白，取**第一条**。例：
/// <code>
/// # 宁德时代 300750
/// &gt; 观点：估值压到18x，压制来自政策成本+去宁化；回购是情绪转折点
/// </code>
///
/// ════ 为什么要个标记，而不是取首行 ════
/// 笔记是自由 markdown，首行往往是标题或日期，拿它当观点必错。
/// 但也**只认这一个标记**——不强制模板，其余内容随便写；没写就返回 null，
/// 那一列留空，不影响任何判断。
/// </summary>
public static class NoteOpinionParser
{
    private static readonly string[] Tags = ["观点：", "观点:"];

    /// <summary>抽不到返回 null。</summary>
    public static string? Parse(string? noteText)
    {
        if (string.IsNullOrWhiteSpace(noteText)) return null;

        foreach (var raw in noteText.Split('\n'))
        {
            // 去掉行首的 markdown 装饰符和空白（含全角空格）
            var line = raw.TrimStart(' ', '\t', '>', '-', '*', '#', '　', '\r');
            foreach (var tag in Tags)
            {
                if (!line.StartsWith(tag, StringComparison.Ordinal)) continue;
                var v = line[tag.Length..].Trim().TrimEnd('\r');
                if (v.Length > 0) return v;
            }
        }
        return null;
    }
}
