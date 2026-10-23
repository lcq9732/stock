using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using StockPlatform.Pdf;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 指标合理区间的两头（2026-10-23，见 doc/metric-value-audit-findings.md）。
///
/// 上限卡太死的代价不是"这一项空着"，而是"存了同页邻居的数"——被区间拒掉的值不占位，
/// 后面的扫描会接着往下找。券商那两项就是这么错的，这里把放宽后的真值钉住；
/// 同时钉住那些**该拦还得拦**的，免得以后有人顺手把所有上限一起放宽。
/// </summary>
public class BankReportParserRangeTests
{
    private sealed class FixedSource(IReadOnlyList<PdfLine> lines) : IPdfLineSource
    {
        public string Name => "fixed";
        public bool IsAvailable => true;
        public string UnavailableReason => "";
        public IReadOnlyList<PdfLine>? TryExtract(string pdfPath, PdfExtractOptions options,
                                                  CancellationToken ct = default) => lines;
    }

    private static List<BankRegulatoryMetric> Parse(FinancialInstitutionKind kind, params PdfLine[] lines)
        => new BankReportParser(new PdfLineExtractor(new FixedSource(lines)))
            .Parse("x.pdf", "601099", new DateTime(2026, 6, 30), kind);

    private static double? Get(List<BankRegulatoryMetric> rows, string key)
        => rows.FirstOrDefault(r => r.MetricKey == key)?.Value;

    /// <summary>
    /// 太平洋证券 2026 中报那一页。净资本/负债 917.93%、流动性覆盖率 2030.55%
    /// 都曾经被上限拒掉，于是这两个 key 存成了同页净资本/净资产和风险覆盖率的数。
    /// </summary>
    [Fact]
    public void 券商这两项的高值不再被拒()
    {
        var rows = Parse(FinancialInstitutionKind.Broker,
            new PdfLine(9, "净资本/负债（%） 917.93 798.28"),
            new PdfLine(9, "净资本/净资产（%） 78.65 78.30"),
            new PdfLine(9, "流动性覆盖率（%） 2,030.55 1,963.29"),
            new PdfLine(9, "风险覆盖率（%） 526.29 514.79"));

        Assert.Equal(917.93, Get(rows, BrokerRegulatoryKeys.NetCapitalToLiabilities));
        Assert.Equal(2030.55, Get(rows, BrokerRegulatoryKeys.LiquidityCoverage));
        // 邻居没被牵连
        Assert.Equal(78.65, Get(rows, BrokerRegulatoryKeys.NetCapitalToNetAssets));
        Assert.Equal(526.29, Get(rows, BrokerRegulatoryKeys.RiskCoverage));
    }

    /// <summary>
    /// 反过来，银行那几个上限是在干活的：把它们一起放宽，农业银行的不良率会变成 292.55、
    /// 民生银行的资本充足率会变成 44。这条用例是给后来人的提醒——别顺手全放宽。
    /// </summary>
    [Fact]
    public void 银行侧明显超限的数照旧拒掉()
    {
        // ⚠ 这里**故意不放**核心一级/一级资本充足率那两行：它们相邻时，若资本充足率自己那行
        // 的值被拒掉，ScanWrappedLabels 会把两行拼起来凑出一个假的"资本充足率"（取窗口第一个
        // 数 12.92），接着 DropInconsistentCapital 发现 12.93 > 12.92 就把三兄弟整组丢掉——
        // 那是另一条路上的毛病（见 doc/metric-value-audit-findings.md 的 C 类），不该混进这条用例。
        var rows = Parse(FinancialInstitutionKind.Bank,
            new PdfLine(10, "不良贷款率 292.55"),            // 上限 6
            new PdfLine(10, "资本充足率 44"),                // 上限 35
            new PdfLine(10, "拨备覆盖率 340.28"),
            new PdfLine(10, "成本收入比 26.79"),
            new PdfLine(10, "净息差 1.46"),
            new PdfLine(10, "净利差 1.34"));

        Assert.Null(Get(rows, BankRegulatoryKeys.NplRatio));
        Assert.Null(Get(rows, BankRegulatoryKeys.TotalCar));
        Assert.Equal(340.28, Get(rows, BankRegulatoryKeys.ProvisionCoverage));
        Assert.Equal(26.79, Get(rows, BankRegulatoryKeys.CostIncomeRatio));
    }

    /// <summary>渝农商行 2026 中报踩过的：`流动性覆盖率 2026` 里的 2026 是**年份**。</summary>
    [Fact]
    public void 年份不会被当成流动性覆盖率()
    {
        var rows = Parse(FinancialInstitutionKind.Bank,
            new PdfLine(66, "流动性覆盖率 2026"),
            new PdfLine(66, "流动性比例 108.06"),
            new PdfLine(66, "不良贷款率 1.21"),
            new PdfLine(66, "拨备覆盖率 299.61"));

        Assert.Null(Get(rows, BankRegulatoryKeys.Lcr));
        Assert.Equal(108.06, Get(rows, BankRegulatoryKeys.LiquidityRatio));
    }
}
