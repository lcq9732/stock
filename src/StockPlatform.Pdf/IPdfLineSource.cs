namespace StockPlatform.Pdf;

/// <summary>
/// 一条"把 PDF 变成行"的路子。
///
/// ════ 职责边界 ════
/// 实现者只回答一件事：**给我一份 PDF，还给我一批行**。怎么做是它自己的事——
/// 按坐标聚类、调外部程序、还是渲染成图去 OCR，外面不需要知道。
///
/// 尤其是<b>页面筛选由各实现自己做</b>（照 <see cref="PdfExtractOptions.PageFilter"/> 办）。
/// 拆分前这件事是漏出去的：pdftotext 那条路的筛页写在 BankReportParser 里，业务解析类
/// 替某个 source 干了它自己该干的活。下沉之后，组合器就真的只剩"排顺序"一件事。
/// </summary>
public interface IPdfLineSource
{
    /// <summary>
    /// 这条路的名字："pdfpig" / "poppler" / "ocr"。
    /// 用在日志里说明这批行是怎么来的——OCR 出来的值有认错的可能，要能追溯。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 这条路现在能不能用。纯托管的实现恒为 true；靠外部程序的实现要看程序装没装。
    /// 探测有代价（要实际跑一次），所以实现方自己缓存结果。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>不可用的一句话原因，能用时返回空串。给进度日志用，让人知道该装什么。</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// 提取。**没结果时返回 null 或空列表都可以**，两者对调用方等价——
    /// 组合器只认"有没有拿到行"，不区分"不可用"和"可用但没提到东西"。
    /// </summary>
    IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                       CancellationToken ct = default);
}

/// <summary>
/// 把 PDF 的页读成**原始文本**（不做坐标聚类、不筛页）。
///
/// 跟 <see cref="IPdfLineSource"/> 分开是因为它们回答的是不同的问题：
/// 这个答"这页上有哪些字"，那个答"这页排成什么样"。
/// 两处要用：① 各 source 的页面筛选要先拿到页文本；
/// ② 业务侧粗判"这份 PDF 是不是年报正文"，只要前几页的字，不需要版面结构。
/// </summary>
public interface IPdfTextReader
{
    /// <summary>
    /// 逐页读文本。<paramref name="maxPages"/> 限制页数（0 = 不限）。
    /// 某一页读不了就跳过那页，不影响其余页——整份读不了返回空。
    /// </summary>
    IReadOnlyList<PdfLine> ReadPages(string pdfPath, int maxPages = 0);
}
