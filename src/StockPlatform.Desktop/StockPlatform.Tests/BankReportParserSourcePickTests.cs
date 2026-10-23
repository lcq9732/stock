using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using StockPlatform.Pdf;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 提取路子之间怎么挑（2026-10-23，见 doc/pdf-source-fallback-design.md）。
///
/// 原来的判据是组合器里的"行数 &gt; 0 就算成了"，治不了"读残但非空"——PdfPig 逐页兜异常，
/// 291 页只读出 4 页也是非空，poppler 永远轮不到。改成每条都解析一遍、取指标最多的那条。
/// </summary>
public class BankReportParserSourcePickTests
{
    private sealed class FixedSource(string name, IReadOnlyList<PdfLine> lines) : IPdfLineSource
    {
        public string Name => name;
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                                  CancellationToken ct = default) => lines;
    }

    /// <summary>瑞丰那种"只读到封面图表"的残缺结果：解得出 3 项，正好卡在 minMetrics 上。</summary>
    private static IReadOnlyList<PdfLine> CoverOnly(double npl = 0.98) =>
    [
        new PdfLine(4, $"不良贷款率 {npl}"),
        new PdfLine(4, "拨备覆盖率 340.28"),
        new PdfLine(4, "资本充足率 14.11"),
    ];

    /// <summary>真正的监管指标表。<paramref name="take"/> 控制给几行，用来摆出"几项对几项"。</summary>
    private static IReadOnlyList<PdfLine> MetricTable(int take = 12, double npl = 0.98)
    {
        string[] rows =
        [
            $"不良贷款率 {npl}", "贷款拨备率 3.35", "拨备覆盖率 340.28",
            "核心一级资本充足率 12.92", "一级资本充足率 12.93", "资本充足率 14.11",
            "净息差 1.46", "净利差 1.34", "成本收入比 26.79",
            "流动性比例 73.9", "流动性覆盖率 115.47", "正常类贷款迁徙率 0.95",
        ];
        return rows.Take(take).Select(r => new PdfLine(10, r)).ToList();
    }

    private static List<BankRegulatoryMetric> Parse(params IPdfLineSource[] sources)
        => new BankReportParser(new PdfLineExtractor(sources))
            .Parse("x.pdf", "601528", new DateTime(2025, 6, 30), FinancialInstitutionKind.Bank);

    /// <summary>第一条读残（3 项）、第二条是完整指标表（12 项）→ 用第二条。这是瑞丰那份。</summary>
    [Fact]
    public void 第一条读残时改用解出更多的那条()
    {
        var rows = Parse(new FixedSource("pdfpig", CoverOnly()),
                         new FixedSource("poppler", MetricTable()));

        Assert.Equal(12, rows.Count);
        Assert.Contains(rows, m => m.MetricKey == BankRegulatoryKeys.CoreTier1Car);
        Assert.Contains(rows, m => m.MetricKey == BankRegulatoryKeys.Lcr);
    }

    /// <summary>
    /// 反过来，第一条解得更多就留着它——PdfPig 按坐标聚类，在不少版式上比 pdftotext 强
    /// （实测青岛银行 15 对 12、重庆银行 11 对 8）。**不是后来者优先**。
    /// </summary>
    [Fact]
    public void 第一条解出更多时不被后面的覆盖()
    {
        var rows = Parse(new FixedSource("pdfpig", MetricTable()),
                         new FixedSource("poppler", MetricTable(8)));

        Assert.Equal(12, rows.Count);
    }

    /// <summary>数量打平时保持 source 顺序——跟改之前的行为一致，免得无谓地换了值的来源。</summary>
    [Fact]
    public void 数量打平时用排在前面的那条()
    {
        var rows = Parse(new FixedSource("pdfpig", MetricTable(8, npl: 0.98)),
                         new FixedSource("poppler", MetricTable(8, npl: 1.98)));

        Assert.Equal(8, rows.Count);
        Assert.Equal(0.98, rows.First(m => m.MetricKey == BankRegulatoryKeys.NplRatio).Value);
    }

    /// <summary>第一条一行都没给（今天就能自愈的情形）→ 照旧走第二条，这个行为不许退化。</summary>
    [Fact]
    public void 第一条返回空时仍然走第二条()
    {
        var rows = Parse(new FixedSource("pdfpig", []),
                         new FixedSource("poppler", MetricTable()));

        Assert.Equal(12, rows.Count);
    }

    /// <summary>两条都解不出指标 → 跟今天一样，整份算失败（minMetrics 那道门槛）。</summary>
    [Fact]
    public void 两条都解不出时整份算失败()
    {
        var noise = new PdfLine[] { new(1, "本公司于二零二五年八月二十日召开董事会") };
        var rows = Parse(new FixedSource("pdfpig", noise), new FixedSource("poppler", noise));

        Assert.Empty(rows);
    }

    /// <summary>一行都提不出来的（扫描件）照旧抛，由调用方记进 BankReportFetchState。</summary>
    [Fact]
    public void 一条路都没有行时抛异常()
    {
        Assert.Throws<InvalidOperationException>(
            () => Parse(new FixedSource("pdfpig", []), new FixedSource("poppler", [])));
    }
}
