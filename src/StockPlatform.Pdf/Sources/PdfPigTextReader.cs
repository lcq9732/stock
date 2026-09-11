using UglyToad.PdfPig;

namespace StockPlatform.Pdf.Sources;

/// <summary>
/// 用 PdfPig 逐页读**原始文本**（不聚类、不筛页）。
///
/// 跟 <see cref="PdfPigLineSource"/> 分开，是因为两者回答的问题不同：
/// 这个答"这页上有哪些字"，那个答"这页排成什么样"。业务侧要粗判"这份 PDF 是不是年报正文"
/// 时只要前几页的字，用不着版面结构——那种活儿不该逼它去开一个行提取器。
/// </summary>
public sealed class PdfPigTextReader : IPdfTextReader
{
    /// <inheritdoc/>
    public IReadOnlyList<PdfLine> ReadPages(string pdfPath, int maxPages = 0)
    {
        var result = new List<PdfLine>();

        // Open 本身就可能因为字体表损坏而抛（"The head table is required"）。
        // 整份打不开就返回空，让调用方按"读不出文本"处理。
        PdfDocument doc;
        try { doc = PdfDocument.Open(pdfPath); }
        catch { return result; }
        using var _ = doc;

        int last = maxPages > 0 ? Math.Min(maxPages, doc.NumberOfPages) : doc.NumberOfPages;

        // ⚠ **逐页取、每页单独兜异常**，不能用 foreach (var page in doc.GetPages())。
        // doc.GetPages() 是惰性的：某一页的字体坏了，异常会从 foreach 里冒出来，整份 PDF
        // 就此报废。而坏的往往只是个别页，要的内容在别的页上，完全能读出来。
        for (int n = 1; n <= last; n++)
        {
            try { result.Add(new PdfLine(n, doc.GetPage(n).Text)); }
            catch { }                                   // 这一页读不了，换下一页
        }
        return result;
    }
}
