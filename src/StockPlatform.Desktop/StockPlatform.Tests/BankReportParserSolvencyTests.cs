using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using StockPlatform.Pdf;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 保险偿付能力充足率的「整表成对取」（2026-10-23，见 doc/solvency-paired-table-design.md）。
///
/// 这些用例不需要真 PDF：BankReportParser 的构造参数就是 PdfLineExtractor，塞一个假 source
/// 进去就能把各家的真实排版摆出来——行文本全部抄自本地那 20 份报告的文本层原文。
/// </summary>
public class BankReportParserSolvencyTests
{
    /// <summary>只负责把事先摆好的行交出去，不筛页、不读文件。</summary>
    private sealed class FixedSource(IReadOnlyList<PdfLine> lines) : IPdfLineSource
    {
        public string Name => "fixed";
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                                  CancellationToken ct = default) => lines;
    }

    private static List<BankRegulatoryMetric> Parse(params PdfLine[] lines)
        => new BankReportParser(new PdfLineExtractor(new FixedSource(lines)))
            .Parse("x.pdf", "601318", new DateTime(2026, 6, 30), FinancialInstitutionKind.Insurer);

    private static BankRegulatoryMetric? Get(List<BankRegulatoryMetric> rows, string key)
        => rows.FirstOrDefault(r => r.MetricKey == key);

    /// <summary>
    /// 平安型：摘要页只有集团综合，核心在 70 页之后的「集团偿付能力」表里，中间还隔着一张
    /// 子公司横排表。改之前核心取到子公司的 133.8、再被跨页保护丢掉，结果是空的。
    /// </summary>
    [Fact]
    public void 摘要页只有综合时_核心取自综合对得上的那张表()
    {
        var rows = Parse(
            new PdfLine(15, "集团综合偿付能力充足率(%) 198.1 193.3 上升4.8个百分点，与上年末比"),
            new PdfLine(48, "核心偿付能力充足率(%) 133.8 123.3 318.9 247.1 247.5 267.7"),
            new PdfLine(48, "综合偿付能力充足率(%) 185.5 175.7 430.0 343.3 289.0 312.1"),
            new PdfLine(85, "核心偿付能力充足率(%) 165.6 160.7"),
            new PdfLine(85, "综合偿付能力充足率(%) 198.1 193.3"));

        var core = Get(rows, InsurerRegulatoryKeys.CoreSolvency);
        Assert.NotNull(core);
        Assert.Equal(165.6, core!.Value);
        Assert.Equal(85, core.SourcePage);
        // 综合不动：值本来就等于锚，页码还是摘要页
        Assert.Equal(198.1, Get(rows, InsurerRegulatoryKeys.ComprehensiveSolvency)!.Value);
        Assert.Equal(15, Get(rows, InsurerRegulatoryKeys.ComprehensiveSolvency)!.SourcePage);
    }

    /// <summary>太保型：值是整数，列数判据在它身上退化，只能靠锚值把子公司表分出去。</summary>
    [Fact]
    public void 整数值也能配对()
    {
        var rows = Parse(
            new PdfLine(23, "综合偿付能力充足率 256"),
            new PdfLine(62, "核心偿付能力充足率 182"),
            new PdfLine(62, "综合偿付能力充足率 256"),
            new PdfLine(289, "核心偿付能力充足率 130"),
            new PdfLine(289, "综合偿付能力充足率 210"));

        var core = Get(rows, InsurerRegulatoryKeys.CoreSolvency);
        Assert.NotNull(core);
        Assert.Equal(182, core!.Value);
        Assert.Equal(62, core.SourcePage);
    }

    /// <summary>
    /// 国寿型：附注里有个整数精度版（140 / 191），跟锚 190.94 只差 0.06。
    /// 它排在高精度表**前面**，容差一旦放松就会被选中、把精度弄丢。
    /// </summary>
    [Fact]
    public void 整数精度版不被当成同一张表()
    {
        var rows = Parse(
            new PdfLine(9, "综合偿付能力充足率(%) 190.94"),
            new PdfLine(12, "核心偿付能力充足率 140"),
            new PdfLine(12, "综合偿付能力充足率 191"),
            new PdfLine(20, "核心偿付能力充足率(%) 139.54"),
            new PdfLine(20, "综合偿付能力充足率(%) 190.94"));

        var core = Get(rows, InsurerRegulatoryKeys.CoreSolvency);
        Assert.NotNull(core);
        Assert.Equal(139.54, core!.Value);
        Assert.Equal(20, core.SourcePage);
    }

    /// <summary>配不上成对表时退回老行为：跨页的核心照旧丢掉，不能把子公司数放进来。</summary>
    [Fact]
    public void 没有成对表时_跨页的核心照旧丢掉()
    {
        var rows = Parse(
            new PdfLine(15, "集团综合偿付能力充足率(%) 198.1 193.3"),
            new PdfLine(48, "核心偿付能力充足率(%) 133.8 123.3 318.9"));

        Assert.Null(Get(rows, InsurerRegulatoryKeys.CoreSolvency));
        Assert.Equal(198.1, Get(rows, InsurerRegulatoryKeys.ComprehensiveSolvency)!.Value);
    }

    /// <summary>核心资本必然 ≤ 实际资本，核心比综合还高的那页是取错了列，不能用。</summary>
    [Fact]
    public void 核心大于综合的页被拒()
    {
        var rows = Parse(
            new PdfLine(10, "综合偿付能力充足率(%) 150.0"),
            new PdfLine(20, "核心偿付能力充足率(%) 180.0"),
            new PdfLine(20, "综合偿付能力充足率(%) 150.0"));

        Assert.Null(Get(rows, InsurerRegulatoryKeys.CoreSolvency));
    }

    /// <summary>新华这类单一主体公司本来就只有一张表，两行同页，改完照样取对。</summary>
    [Fact]
    public void 单一主体同页两行不受影响()
    {
        var rows = Parse(
            new PdfLine(29, "核心偿付能力充足率(%) 170.72 157.01"),
            new PdfLine(29, "综合偿付能力充足率(%) 256.01 278.43"));

        Assert.Equal(170.72, Get(rows, InsurerRegulatoryKeys.CoreSolvency)!.Value);
        Assert.Equal(256.01, Get(rows, InsurerRegulatoryKeys.ComprehensiveSolvency)!.Value);
    }

    /// <summary>连综合都没解析出来时没有锚可用，核心保持原样，别把它也赔进去。</summary>
    [Fact]
    public void 没有综合时核心保留()
    {
        var rows = Parse(new PdfLine(20, "核心偿付能力充足率(%) 139.54"));

        Assert.Equal(139.54, Get(rows, InsurerRegulatoryKeys.CoreSolvency)!.Value);
    }

    /// <summary>
    /// 同一页上的注释行和敏感性测试表的表头不能被当成数据行——它们的标签要么不在行首，
    /// 要么后面跟的根本不是充足率。这是「行首 6 字以内」那条约束在兜。
    /// </summary>
    [Fact]
    public void 注释行与敏感性表头不被当成数据()
    {
        var rows = Parse(
            new PdfLine(15, "集团综合偿付能力充足率(%) 198.1 193.3"),
            new PdfLine(85, "核心偿付能力充足率(%) 165.6 160.7"),
            new PdfLine(85, "综合偿付能力充足率(%) 198.1 193.3"),
            new PdfLine(85, "注：(1) 核心偿付能力充足率=核心资本╱最低资本；综合偿付能力充足率=实际资本╱最低资本。"),
            new PdfLine(85, "(2) 上表中核心偿付能力充足率和综合偿付能力充足率的最低监管要求分别为50%、100%。"),
            new PdfLine(85, "核心偿付能力充足率 综合偿付能力充足率"),
            new PdfLine(85, "当期利率下降50个基点 下降1.5个百分点 下降8.4个百分点 上升2.6个百分点"));

        var core = Get(rows, InsurerRegulatoryKeys.CoreSolvency);
        Assert.Equal(165.6, core!.Value);
        Assert.Equal(85, core.SourcePage);
    }
}
