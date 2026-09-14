using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 财务分析第三节里**应收账款（存量口径）**那组行的判定（2026-09-14 新增）。
///
/// 起因：600285 2026 中报应收账款同比 +23.2%、营收只有 +2.3%，两年间应收从 3.11 亿涨到
/// 6.07 亿而营收只涨 12.7%——但卡片上一个字都没提，因为原来只用了现金流量表附注的**流量**
/// 口径（"经营性应收占用 1.61 亿"），那个数既看不出存量在累积，也分不清是客户欠钱还是预付
/// 给了供应商。这组测试锁住存量口径的三条：增速背离、周转天数、以及"不跟流量行重复告警"。
///
/// 下面的数字全是 600285 的真实报表值（元），不是编的——阈值调整时能直接看出对这只票的影响。
/// </summary>
public class ReceivableAnalysisTests
{
    private static FinancialSnapshot Snap(string date, double ar, double rev,
        double? noteRecv = null, double? recvDecrease = null, double? npParent = null)
    {
        var v = new Dictionary<string, double>
        {
            [FinancialKeys.AccountsReceivable] = ar,
            [FinancialKeys.Revenue] = rev,
            // 有"营业成本"才会被判成工商企业，否则整节按金融机构跳过
            [FinancialKeys.OperCost] = rev * 0.21,
        };
        if (noteRecv.HasValue) v[FinancialKeys.NoteReceivable] = noteRecv.Value;
        if (recvDecrease.HasValue) v[FinancialKeys.ReceivableDecrease] = recvDecrease.Value;
        if (npParent.HasValue) v[FinancialKeys.NetProfitParent] = npParent.Value;
        return new FinancialSnapshot { ReportDate = DateTime.Parse(date), Values = v };
    }

    /// <summary>600285 的真实历史：中报 + 上年同期 + 两个年末（周转天数的期初）。</summary>
    private static List<FinancialSnapshot> LingRuiHistory(double ar2026 = 607295307.47) =>
    [
        Snap("2026-06-30", ar2026, 2147977056.01, 212698322.46, -160949898.89, 495000000),
        Snap("2025-12-31", 430400535.72, 3853391129.70, 167227781.99),
        Snap("2025-06-30", 493095297.55, 2099205515.81, 191584477.72, -235881828.29, 474000000),
        Snap("2024-12-31", 319370595.37, 3500831286.92, 152368047.41),
    ];

    private static AnalysisSection CashSection(FinancialAnalysisReport r) =>
        r.Sections.Single(s => s.Title.StartsWith("三、"));

    private static AnalysisLine? Line(FinancialAnalysisReport r, string label) =>
        CashSection(r).Lines.FirstOrDefault(l => l.Label.Trim() == label);

    [Fact]
    public void 应收增速远超营收_标黄并写进结论()
    {
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药", LingRuiHistory());

        var ar = Line(r, "应收账款");
        Assert.NotNull(ar);
        Assert.Equal("6.07 亿", ar!.Value);
        // 应收 +23.2% vs 营收 +2.3%，背离 20.8pct —— 过 15pct 的警戒线，没到 30pct 的 Bad
        Assert.Equal(Verdict.Warn, ar.Verdict);
        Assert.Contains("同比 +23.2%", ar.Change);
        Assert.Contains("较上年末 +41.1%", ar.Change);
        Assert.Contains("快 20.8 pct", ar.Note);

        // 顶部异常汇总只收 Bad（见 Analyze 里的 Collect），Warn 不进；背离到 30pct 以上才会上榜
        Assert.DoesNotContain(r.Alerts, a => a.StartsWith("应收账款"));
        // 结论句改用存量背离来说，而不是流量口径那句"应收和存货同时增加"
        Assert.Contains("应收账款同比 +23.2%", CashSection(r).Conclusion);
    }

    [Fact]
    public void 周转天数按上年末做期初_不拿上年同期()
    {
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药", LingRuiHistory());

        var days = Line(r, "└ 应收账款周转天数");
        Assert.NotNull(days);
        // (6.073+4.304)/2 ÷ 21.48 × 180 = 43.5 天；期初是上年末 4.304 亿，不是上年同期 4.931 亿
        Assert.Equal("43.5 天", days!.Value);
        Assert.Equal("去年同期 34.8 天", days.Change);
        // 只多了 8.6 天（没过 10 天的绝对线），但相对拉长 24.8% —— 过相对线就该报
        Assert.Equal(Verdict.Warn, days.Verdict);
    }

    [Fact]
    public void 缺上年末余额时整行不显示_不退化成期末余额()
    {
        // 去掉两个年末，cur 和 prior 的期初都没了
        var history = LingRuiHistory().Where(h => h.ReportDate.Month != 12).ToList();
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药", history);

        Assert.Null(Line(r, "└ 应收账款周转天数"));
        // 增速背离不依赖期初，照常报
        Assert.Equal(Verdict.Warn, Line(r, "应收账款")!.Verdict);
    }

    [Fact]
    public void 应收涨得比营收慢时不报警()
    {
        // 把 2026 中报的应收压到只比上年同期涨 3%（营收 +2.3%），背离不到 1pct
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药",
            LingRuiHistory(ar2026: 493095297.55 * 1.03));

        var ar = Line(r, "应收账款");
        Assert.Equal(Verdict.Good, ar!.Verdict);
        Assert.DoesNotContain("pct", ar.Note);
        Assert.DoesNotContain("应收账款同比", CashSection(r).Conclusion);
    }

    [Fact]
    public void 应收行已报警时流量行降级_同一件事不上色两遍()
    {
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药", LingRuiHistory());

        var flow = Line(r, "营运资金净占用");
        Assert.NotNull(flow);
        // 1.61 亿的占用没超过当期净利（4.95 亿），本来是 Warn；应收行已经点名同一件事，降成陈述，
        // 免得界面上下两行都标黄、看着像两个问题
        Assert.Equal(Verdict.Neutral, flow!.Verdict);
        Assert.Equal(Verdict.Warn, Line(r, "应收账款")!.Verdict);
    }

    [Fact]
    public void 占用超过当期净利时流量行仍然报警()
    {
        // 占用额（3 亿）超过归母净利（0.5 亿）是量级问题，跟应收增速说的不是一回事，不该被降级
        var history = LingRuiHistory();
        history[0].Values[FinancialKeys.ReceivableDecrease] = -3e8;
        history[0].Values[FinancialKeys.NetProfitParent] = 0.5e8;
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药", history);

        Assert.Equal(Verdict.Bad, Line(r, "营运资金净占用")!.Verdict);
    }

    [Fact]
    public void 应收加票据占营收只陈述不判定()
    {
        var r = new FinancialAnalyzer().Analyze("600285", "羚锐制药", LingRuiHistory());

        var share = Line(r, "└ 应收+票据 占营收");
        Assert.NotNull(share);
        // (6.073+2.127)/21.48 = 38.2%
        Assert.Equal("38.2%", share!.Value);
        Assert.Equal(Verdict.Neutral, share.Verdict);
        Assert.Contains("不设阈值", share.Note);
        // 半年报口径的营收只有大半年，比例天然偏高，得说清楚
        Assert.Contains("比例天然比年报高", share.Note);
    }

    [Fact]
    public void 应收接近零的预收款生意_整组不显示()
    {
        // 茅台形态：2026H1 应收 0.01 亿、占营收 0.0%，同比 -98.5% 这种数字摆出来只是噪音
        var history = new List<FinancialSnapshot>
        {
            Snap("2026-06-30", 1_000_000, 91_000_000_000, 0, -160949898.89, 45_000_000_000),
            Snap("2025-12-31", 4_600_000, 174_000_000_000, 0),
            Snap("2025-06-30", 67_000_000, 83_000_000_000, 0, null, 45_000_000_000),
            Snap("2024-12-31", 4_000_000, 170_000_000_000, 0),
        };
        var r = new FinancialAnalyzer().Analyze("600519", "贵州茅台", history);

        Assert.Null(Line(r, "应收账款"));
        Assert.Null(Line(r, "└ 应收账款周转天数"));
        Assert.Null(Line(r, "└ 应收+票据 占营收"));
        Assert.DoesNotContain("应收账款同比", CashSection(r).Conclusion);
    }
}
