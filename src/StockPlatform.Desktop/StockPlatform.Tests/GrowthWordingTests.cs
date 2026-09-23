using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 财务分析的措辞不许把**增长**说成**下跌**（2026-09-14）。
///
/// ════ 这个 bug 是怎么活下来的 ════
/// 四处判据都只比相对大小、不看正负号：
///     ocfYoY &lt; profYoY - 0.15  →  直接套用"跌得比利润还狠 / 失血"的模板
/// 华鲁恒升 2026 中报归母净利 +50.0%、经营现金流 +15.5%，两个都在涨，
/// +15.5% &lt; +50% − 15% 成立，于是报告写出了：
///     "现金流失血比利润更严重（15.5% vs 50.0%）"
///     "单季降幅比累计更大 —— 恶化在加速"（单季其实是 +43.4%）
/// 而同一节里另一行是"现金流/归母净利 1.14 → 利润基本都收成了现金"——自己打自己。
///
/// **之前的用例全是下跌场景**，那时候措辞恰好是对的，所以没人发现。
/// 这组用例把正增长、负增长、一正一负三种都钉住。
/// </summary>
public class GrowthWordingTests
{
    private readonly ITestOutputHelper _out;
    public GrowthWordingTests(ITestOutputHelper output) => _out = output;

    /// <summary>造一份最小可用的报告期数据。金额单位：元。</summary>
    private static FinancialSnapshot Snap(DateTime date, double revenue, double npParent, double ocf,
                                          double equity = 100e8, double shareCapital = 10e8)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [FinancialKeys.Revenue] = revenue,
            [FinancialKeys.NetProfitParent] = npParent,
            [FinancialKeys.NetProfit] = npParent,
            [FinancialKeys.Ocf] = ocf,
            [FinancialKeys.EquityParent] = equity,
            [FinancialKeys.ShareCapital] = shareCapital,
            [FinancialKeys.OperCost] = revenue * 0.75,
            [FinancialKeys.TotalAssets] = equity * 1.4,
            [FinancialKeys.TotalLiabilities] = equity * 0.4,
        };
        return new FinancialSnapshot { ReportDate = date, Values = values };
    }

    private static string AllText(FinancialAnalysisReport r)
    {
        var parts = new List<string> { r.Headline ?? "" };
        foreach (var s in r.Sections)
        {
            parts.Add(s.Conclusion ?? "");
            foreach (var l in s.Lines) { parts.Add(l.Note ?? ""); parts.Add(l.Change ?? ""); }
        }
        return string.Join("\n", parts);
    }

    /// <summary>华鲁恒升那组真实数字：两个都在涨，一个字的"跌/失血/恶化"都不许出现。</summary>
    [Fact]
    public void 两个都在增长时_不许出现下跌措辞()
    {
        var history = new List<FinancialSnapshot>
        {
            // 2026H1：营收 171.62亿(+8.9%)、归母 23.53亿(+50.0%)、经营现金流 26.92亿(+15.5%)
            Snap(new DateTime(2026, 6, 30), 171.62e8, 23.53e8, 26.92e8),
            Snap(new DateTime(2025, 12, 31), 320e8, 38.31e8, 45e8),
            Snap(new DateTime(2025, 6, 30), 157.6e8, 15.69e8, 23.31e8),
        };

        var report = new FinancialAnalyzer().Analyze("600426", "华鲁恒升", history, 19.87, 0.50);
        var text = AllText(report);
        _out.WriteLine("标题：" + report.Headline);

        foreach (var bad in new[] { "失血", "跌得", "降幅", "恶化在加速", "萎缩" })
            Assert.False(text.Contains(bad, StringComparison.Ordinal),
                $"两个指标都在正增长，却出现了「{bad}」：\n{text}");
    }

    /// <summary>真下跌的场景：原来的措辞是对的，不能被这次修改弄没了。</summary>
    [Fact]
    public void 两个都在下跌且现金流跌更多时_保留原措辞()
    {
        var history = new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 80e8, 6e8, 1e8),      // 归母 -40%、现金流 -80%
            Snap(new DateTime(2025, 12, 31), 200e8, 20e8, 18e8),
            Snap(new DateTime(2025, 6, 30), 100e8, 10e8, 5e8),
        };

        var report = new FinancialAnalyzer().Analyze("000001", "下跌样本", history, 10.0, null);
        var text = AllText(report);
        _out.WriteLine("标题：" + report.Headline);

        // 现金流跌 80%、利润跌 40%，"跌得比利润还狠"这时候是准确的
        Assert.Contains("跌", text, StringComparison.Ordinal);
    }

    /// <summary>利润涨、现金流跌——最该提示的一种，不能因为"不是两个都跌"就闭嘴。</summary>
    [Fact]
    public void 利润涨而现金流跌时_要明确说出来()
    {
        var history = new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 120e8, 15e8, 2e8),    // 归母 +50%、现金流 -80%
            Snap(new DateTime(2025, 12, 31), 220e8, 22e8, 20e8),
            Snap(new DateTime(2025, 6, 30), 100e8, 10e8, 10e8),
        };

        var report = new FinancialAnalyzer().Analyze("000002", "背离样本", history, 10.0, null);
        var text = AllText(report);
        _out.WriteLine("标题：" + report.Headline);

        Assert.Contains("利润在增长", text, StringComparison.Ordinal);
        // 但不能说成"失血比利润更严重"——利润根本没失血
        Assert.DoesNotContain("失血比利润更严重", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 基期亏损（2026-09-23）：天华新能 2026 中报那组真实数字。去年同期归母 -0.91 亿，
    /// 百分比算不出来，原来标题整句退成"数据不足，无法给出总体判断"。
    /// </summary>
    [Fact]
    public void 去年同期亏损_今年盈利_要说扭亏而不是数据不足()
    {
        var history = new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 77.80e8, 22.92e8, 1.78e8),
            Snap(new DateTime(2025, 12, 31), 70e8, 1e8, 5e8),
            Snap(new DateTime(2025, 6, 30), 34.58e8, -0.91e8, 2.66e8),
        };

        var report = new FinancialAnalyzer().Analyze("300390", "天华新能", history, 54.23, 0);
        var text = AllText(report);
        _out.WriteLine("标题：" + report.Headline);

        Assert.DoesNotContain("数据不足", report.Headline, StringComparison.Ordinal);
        Assert.Contains("扭亏为盈", report.Headline, StringComparison.Ordinal);
        Assert.Contains("营收快速增长", report.Headline, StringComparison.Ordinal);
        Assert.Contains("现金流在往下走", report.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("营收持稳", report.Headline, StringComparison.Ordinal);

        var npp = report.Sections.SelectMany(s => s.Lines).First(l => l.Label == "归母净利");
        // 跨零轴的百分比按基期绝对值算：(22.92 + 0.91) ÷ 0.91
        Assert.Equal("+2618.7%", npp.Change);
        Assert.Equal(Verdict.Good, npp.Verdict);
        Assert.DoesNotContain("失血", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 两期都亏损_要说收窄或扩大()
    {
        var narrowed = new FinancialAnalyzer().Analyze("000001", "测试", new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 50e8, -1e8, 2e8),
            Snap(new DateTime(2025, 12, 31), 100e8, -5e8, 3e8),
            Snap(new DateTime(2025, 6, 30), 50e8, -3e8, 2e8),
        }, 10, 0);
        _out.WriteLine("收窄：" + narrowed.Headline);
        Assert.Contains("亏损收窄（-3.00 亿 → -1.00 亿，+66.7%）", narrowed.Headline, StringComparison.Ordinal);

        var widened = new FinancialAnalyzer().Analyze("000001", "测试", new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 50e8, -3e8, 2e8),
            Snap(new DateTime(2025, 12, 31), 100e8, -5e8, 3e8),
            Snap(new DateTime(2025, 6, 30), 50e8, -1e8, 2e8),
        }, 10, 0);
        _out.WriteLine("扩大：" + widened.Headline);
        Assert.Contains("亏损扩大（-1.00 亿 → -3.00 亿，-200.0%）", widened.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void 由盈转亏_不能只说大幅下滑()
    {
        var report = new FinancialAnalyzer().Analyze("000001", "测试", new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 50e8, -2e8, 2e8),
            Snap(new DateTime(2025, 12, 31), 100e8, 5e8, 3e8),
            Snap(new DateTime(2025, 6, 30), 50e8, 3e8, 2e8),
        }, 10, 0);
        _out.WriteLine("标题：" + report.Headline);
        Assert.Contains("由盈转亏（+3.00 亿 → -2.00 亿，-166.7%）", report.Headline, StringComparison.Ordinal);
    }

    /// <summary>标题每档都要带数字（2026-09-23）：只写"利润下滑"不说跌了多少太笼统。</summary>
    [Fact]
    public void 标题要带同比百分比()
    {
        var report = new FinancialAnalyzer().Analyze("000001", "测试", new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 51e8, 2.64e8, 3e8),
            Snap(new DateTime(2025, 12, 31), 100e8, 5e8, 6e8),
            Snap(new DateTime(2025, 6, 30), 50e8, 3e8, 3e8),
        }, 10, 0);
        _out.WriteLine("标题：" + report.Headline);
        Assert.Contains("营收持稳（+2.0%）", report.Headline, StringComparison.Ordinal);
        Assert.Contains("利润下滑（归母净利 -12.0%）", report.Headline, StringComparison.Ordinal);
    }
}
