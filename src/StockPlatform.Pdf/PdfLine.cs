namespace StockPlatform.Pdf;

/// <summary>
/// 从 PDF 里还原出来的一行文字。<paramref name="Page"/> 是 1 起的页码——
/// 解析出来的值要能对回原文让人工核对，页码不能丢。
/// </summary>
public readonly record struct PdfLine(int Page, string Text);

/// <summary>
/// 提取参数。这些阈值原来是 BankReportParser 里的私有常量，提出来是因为
/// **不同版式的 PDF 需要不同的值**（银行财报的指标表和年报里的子公司清单表就不一样），
/// 而调用方比工具层更清楚自己在解析什么。默认值就是银行财报上量出来的那一套。
/// </summary>
public sealed record PdfExtractOptions
{
    /// <summary>
    /// 页面筛选：收这一页的原始文本，返回要不要这一页。null = 全要。
    ///
    /// ⚠ 这是**工具层和业务层的分界线**。原来的签名是
    /// <c>ExtractLines(path, (string Label, string Key)[] labels)</c>——工具层拿着银行的标签表
    /// 自己判断"这页像不像指标表"，业务知识渗进了工具层。改成回调之后，
    /// "哪几页有用"的判断权回到调用方，工具层只管照做。
    ///
    /// 为什么要筛页：招行年报 309 页，真正有指标表的就那几页。全篇扫既慢，又容易在正文
    /// 叙述段落里误命中（"本集团核心一级资本充足率满足监管要求"这种句子里没有数）。
    /// </summary>
    public Func<string, bool>? PageFilter { get; init; }

    /// <summary>
    /// 同一行的基线 Y 容差（PDF 单位）。取 5.0 是在银行财报上量出来的：表格里多行单元格的
    /// 值与标签基线只差约 2，而正常行距在 16 上下——5 落在两者中间，既能把被排版拆开的值和
    /// 标签并回一行，又不会把相邻两行粘在一起。
    /// </summary>
    public double LineTolerance { get; init; } = 5.0;

    /// <summary>
    /// 判断两个字之间要不要补空格的阈值，按**字宽**的比例算。
    ///
    /// ⚠ 阈值必须按字宽算，**不能用 BoundingBox.Height**——财报 PDF 里 Height 恒为 0
    /// （字形没带高度信息），拿它当基准会让阈值变成 0、每个字之间都插空格。
    /// 实测：相邻中文字的间距约 0.2pt、字宽 8pt，而表格列之间的空白有 20pt 以上，
    /// 取两侧字宽较小者的三成（约 2.4pt）能干净地分开列、又不拆散词。
    /// </summary>
    public double SpaceGapRatio { get; init; } = 0.3;

    /// <summary>
    /// 只处理这几页（1 起）。null = 全部。
    /// 给 OCR 用的：一页要 4~5 秒，三百页的年报全跑要 20 分钟，而要的东西就在两三页里。
    /// </summary>
    public IReadOnlyList<int>? Pages { get; init; }

    /// <summary>进度回调。OCR 那条路一页好几秒，不报会像卡死。</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>什么都不限制的默认参数。</summary>
    public static PdfExtractOptions Default { get; } = new();
}
