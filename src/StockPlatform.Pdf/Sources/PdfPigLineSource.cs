using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace StockPlatform.Pdf.Sources;

/// <summary>
/// 首选路子：PdfPig 按坐标取词，再还原成"行"。纯托管，没有外部依赖，所以恒可用。
///
/// ════ 怎么把表格还原成行 ════
/// PdfPig 给的是带坐标的词，不是行。表格文字直接 <c>page.Text</c> 连起来会串行，所以按
/// **基线 Y 坐标聚类**成行、每行内按 X 排序拼接——这样"不良贷款率 0.94 0.94 – 0.95"
/// 才是完整一行，才能确定 0.94 是它的值而不是隔壁行的。
///
/// ════ 读不动的那些 PDF ════
/// PdfPig 对部分 CJK 字体无能为力（缺 Adobe-CNS1/GB1 的 CMap 资源、内嵌 TrueType 缺 head 表）。
/// 那些**不是扫描件**，文本层都在，只是它解不开。这时本类返回空，由 PdfLineExtractor
/// 交给下一条路（poppler）。
/// </summary>
public sealed class PdfPigLineSource : IPdfLineSource
{
    public string Name => "pdfpig";
    public bool IsAvailable => true;            // 纯托管，装都不用装
    public string UnavailableReason => "";

    /// <inheritdoc/>
    public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                              CancellationToken ct = default)
    {
        var lines = new List<PdfLine>();

        // Open 本身就可能因为字体表损坏而抛（"The head table is required"），
        // 这种也要能落到下一条路，所以整个包起来。
        PdfDocument doc;
        try { doc = PdfDocument.Open(pdfPath); }
        catch { return lines; }
        using var _ = doc;

        var only = options.Pages is { Count: > 0 } ? new HashSet<int>(options.Pages) : null;

        // ⚠ **逐页取、每页单独兜异常**，不能用 foreach (var page in doc.GetPages())。
        // doc.GetPages() 是惰性的：某一页的字体坏了，异常会从 foreach 里冒出来，
        // 整份 PDF 就此报废。实测 26 份失败报告里绝大多数是这么丢的——
        //   "Could not find the referenced CMap: Adobe-CNS1-7 / Adobe-GB1-6"
        //     PdfPig 不自带这些 CJK CMap 资源（0.1.2 也没有 SkipMissingFonts 选项可关）
        //   "The head table is required"
        //     内嵌 TrueType 字体缺 head 表
        // 这些都是**个别页**的字体问题，而要的内容往往在别的页上，完全能读出来。
        // 改成按页号取 + 单页 try/catch 之后，坏页跳过、好页照常解析。
        for (int pageNo = 1; pageNo <= doc.NumberOfPages; pageNo++)
        {
            ct.ThrowIfCancellationRequested();
            if (only != null && !only.Contains(pageNo)) continue;

            Page page;
            string pageText;
            try
            {
                page = doc.GetPage(pageNo);
                pageText = page.Text;
            }
            catch { continue; }                      // 这一页读不了，换下一页
            if (string.IsNullOrWhiteSpace(pageText)) continue;

            // 页面筛选。判据由调用方给（见 PdfExtractOptions.PageFilter 的说明）——
            // 工具层不知道"指标表"长什么样，也不该知道。
            if (options.PageFilter != null && !options.PageFilter(pageText)) continue;

            // GetWords() 会重新走一遍字形解析，可能抛出跟 page.Text 不同的异常，
            // 所以这里也得单独兜住。
            List<Word> words;
            try
            {
                words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            }
            catch { continue; }
            if (words.Count == 0) continue;

            foreach (var text in ToLines(words, options))
                lines.Add(new PdfLine(page.Number, text));
        }
        return lines;
    }

    /// <summary>
    /// 把一页的词聚成行。**具体怎么聚**在 WordJoiner 里——表格单元格那边
    /// （RuledTableExtractor）要用同一套规则，两份阈值不能各写各的。
    /// </summary>
    private static IEnumerable<string> ToLines(List<Word> words, PdfExtractOptions options)
    {
        foreach (var group in WordJoiner.ClusterByBaseline(words, options.LineTolerance))
        {
            var text = WordJoiner.JoinLine(group, options.SpaceGapRatio);
            if (text.Length > 0) yield return text;
        }
    }
}
