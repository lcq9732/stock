using StockPlatform.Pdf;
using StockPlatform.Pdf.Sources;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 线框表格还原（2026-09-11）。
///
/// 这是整件事的起点：年报里的子公司清单有两种版式，**窄表**（单元格内换行）用"把页面聚成行"
/// 根本处理不了——一条记录散成十几行，记录边界彻底丢失。华鲁恒升 2025 年报 p160 就是典型：
///     纯文本行拿到的是  "华鲁恒升（荆" / "州）有限公司" / "湖北省荆州市" …
/// 用 pdfplumber 验证过那页是 3 行 × 8 列，这里要求 PdfPig 这条路给出同样的结果。
///
/// 依赖本机 publish/data/reports 下的 PDF，没有就跳过（别的机器上跑 dotnet test 不该红）。
/// </summary>
public class RuledTableExtractorTests
{
    private const string Reports = @"C:\Chingli\Git\stock\publish\data\annual-reports";
    private readonly ITestOutputHelper _out;
    public RuledTableExtractorTests(ITestOutputHelper output) => _out = output;

    private static string? Pdf(string code)
    {
        var p = Path.Combine(Reports, code, "2025-12-31.pdf");
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public void 窄表_华鲁恒升p160_应还原成3行8列()
    {
        var pdf = Pdf("600426");
        if (pdf == null) { _out.WriteLine("没有 600426 的 PDF，跳过"); return; }

        var tables = new RuledTableExtractor()
            .ExtractTables(pdf, new PdfTableOptions { Pages = [160] });

        var t = Assert.Single(tables);
        _out.WriteLine($"{t.RowCount} 行 × {t.ColCount} 列");
        foreach (var row in t.Rows)
            _out.WriteLine("| " + string.Join(" | ", row.Select(c => c.Replace("\n", "⏎"))));

        // pdfplumber 在同一页上给的也是 3×8
        Assert.Equal(3, t.RowCount);
        Assert.Equal(8, t.ColCount);
    }

    [Fact]
    public void 窄表_单元格内换行的公司名要完整还原()
    {
        var pdf = Pdf("600426");
        if (pdf == null) { _out.WriteLine("没有 600426 的 PDF，跳过"); return; }

        var t = new RuledTableExtractor()
            .ExtractTables(pdf, new PdfTableOptions { Pages = [160] }).Single();

        // 第 1 列是子公司名。排版把它拆成了两行，还原后必须能拼回完整名字——
        // 这正是"纯文本行"那条路做不到的事。
        var names = t.Rows.Select(r => r[0].Replace("\n", "").Replace(" ", "")).ToList();
        _out.WriteLine("第 1 列：" + string.Join(" / ", names));

        Assert.Contains(names, n => n.Contains("华鲁恒升") && n.EndsWith("公司"));
        // 断成半截的名字（"州）有限公司"）说明还原失败
        Assert.DoesNotContain(names, n => n.StartsWith("州）") || n.StartsWith("州)"));
    }

    [Fact]
    public void 宽表_比亚迪p226没有线框_应如实返回空()
    {
        var pdf = Pdf("002594");
        if (pdf == null) { _out.WriteLine("没有 002594 的 PDF，跳过"); return; }

        // 这一页 1421 条路径几乎全是字形填充，构不成网格。pdfplumber 同样报"找到 0 张表"。
        // **不许硬凑**：认不出就返回空，让调用方退回坐标聚类（那种版式本来一行一条，够用）。
        var tables = new RuledTableExtractor()
            .ExtractTables(pdf, new PdfTableOptions { Pages = [226] });

        _out.WriteLine($"找到 {tables.Count} 张表");
        Assert.Empty(tables);
    }

    [Fact]
    public void 页眉那种孤线不能凭空多切出一行()
    {
        // 华鲁恒升 p160 有一条 Y=786 的页眉线，几何上是合格的横线，但它不跟任何竖线相交。
        // 留着它表格上方会多出一整行空行——所以 3 行这个数字本身就是这条判据的验收。
        var pdf = Pdf("600426");
        if (pdf == null) { _out.WriteLine("没有 600426 的 PDF，跳过"); return; }

        var t = new RuledTableExtractor()
            .ExtractTables(pdf, new PdfTableOptions { Pages = [160] }).Single();

        Assert.Equal(3, t.RowCount);
        Assert.DoesNotContain(t.Rows, r => r.All(string.IsNullOrEmpty));   // 没有整行空的
    }

    [Fact]
    public void 越界访问返回null_不抛()
    {
        var t = new PdfTable(1, [["a", "b"], ["c", "d"]]);
        Assert.Equal("a", t[0, 0]);
        Assert.Equal("d", t[1, 1]);
        Assert.Null(t[2, 0]);
        Assert.Null(t[0, 5]);
        Assert.Null(t[-1, 0]);
    }

    [Fact]
    public void 文件不存在时返回空_不抛()
    {
        var tables = new RuledTableExtractor()
            .ExtractTables(@"C:\不存在的目录\没有这份.pdf", PdfTableOptions.Default);
        Assert.Empty(tables);
    }
}
