using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// PE/PB 的**股数**用哪个口径（2026-09-14）。
///
/// ════ 修的是什么 ════
/// 这两行一直拿财报的 <c>share_capital</c> 当股数用。那是「实收资本(或股本)」，
/// 是**金额、单位元**：
///     share_capital(元) = 总股本(股) × 每股面值(元/股)
/// A 股绝大多数票面值 1.00 元，两个数恰好相等——这个巧合被当成了定义。
///
/// 2026-09-14 拿东财选股接口的 TOTAL_SHARES 跟库里逐只比对，5561 只里 373 只对不上，
/// 而且**两个方向都有**：
///   · 面值 &lt; 1 元 → 报表股本偏小 → PE 被**低估**（紫金矿业 1.3、分众传媒 0.5）
///   · H 股会计口径（股本科目含溢价）→ 报表股本偏大 → PE 被**高估**
///     （中国移动 348.8，真值 16.1）
///
/// ⚠ 偏大那一类**没有本地判据能发现**：「流通市值÷收盘价 &gt; 报表股本」只抓得出偏小的
/// （流通股不可能多于总股本）。所以没有总股本时一律标识，不去猜这一只准不准。
/// </summary>
public class ShareCountTests
{
    /// <summary>紫金矿业量级：面值 0.1 元，实收资本 26.59 亿元 ↔ 总股本 265.91 亿股。</summary>
    private const double ReportedCapital = 26.59e8;
    private const double RealShares = 265.91e8;

    private static FinancialSnapshot Snap(DateTime date, double npParent) => new()
    {
        ReportDate = date,
        Values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [FinancialKeys.Revenue] = 3000e8,
            [FinancialKeys.NetProfitParent] = npParent,
            [FinancialKeys.NetProfit] = npParent,
            [FinancialKeys.Ocf] = npParent,
            [FinancialKeys.EquityParent] = 1000e8,
            [FinancialKeys.ShareCapital] = ReportedCapital,
            [FinancialKeys.OperCost] = 2400e8,
            [FinancialKeys.TotalAssets] = 2000e8,
            [FinancialKeys.TotalLiabilities] = 1000e8,
        },
    };

    /// <summary>TTM 归母 = 400 亿。股价 20 元 → 真实市值 5318 亿、PE 13.3。</summary>
    private static List<FinancialSnapshot> History() =>
    [
        Snap(new DateTime(2026, 6, 30), 200e8),
        Snap(new DateTime(2025, 12, 31), 400e8),
        Snap(new DateTime(2025, 6, 30), 200e8),
    ];

    private static AnalysisLine Line(string label, double? totalShares)
    {
        var report = new FinancialAnalyzer()
            .Analyze("601899", "紫金矿业", History(), 20.0, null, null, null, totalShares);
        return report.Sections.SelectMany(s => s.Lines).First(l => l.Label == label);
    }

    [Fact]
    public void 有总股本时_PE按真实股数算()
    {
        var line = Line("PE (TTM)", RealShares);

        // 20 元 × 265.91 亿股 ÷ 400 亿 = 13.3
        Assert.Equal("13.3", line.Value);
        Assert.DoesNotContain("没有总股本", line.Change);
    }

    [Fact]
    public void 没有总股本时_回退报表实收资本但要标识()
    {
        var line = Line("PE (TTM)", null);

        // 20 元 × 26.59 亿 ÷ 400 亿 = 1.3 —— 错了 10 倍，而且是"看起来白菜价"的方向。
        // 回退本身是有意为之（总比不显示强），但必须让人看见它不可信。
        Assert.Equal("1.3", line.Value);
        Assert.Contains("没有总股本", line.Change);
    }

    [Fact]
    public void 总股本为零或负数当作没有()
    {
        // 抓取那一侧约定"拿不到就不写行"，但消费端不该依赖上游永远守约。
        Assert.Contains("没有总股本", Line("PE (TTM)", 0).Change);
        Assert.Contains("没有总股本", Line("PE (TTM)", -1).Change);
    }

    [Fact]
    public void PB同样受影响()
    {
        // PB = 价 ÷ (归母净资产 ÷ 股数)，股数偏小 → 每股净资产偏大 → PB 偏小，同一个病根。
        var right = Line("PB", RealShares);
        var wrong = Line("PB", null);

        // 1000 亿净资产 ÷ 265.91 亿股 = 3.76 元/股 → PB 5.32
        Assert.Equal("5.32", right.Value);
        Assert.DoesNotContain("没有总股本", right.Change);

        // 回退口径：1000 亿 ÷ 26.59 亿 = 37.61「元/股」→ PB 0.53
        Assert.Equal("0.53", wrong.Value);
        Assert.Contains("没有总股本", wrong.Change);
    }

    [Fact]
    public void 面值一元的票两个口径一致()
    {
        // 绝大多数 A 股就是这样——正因为如此，这个 bug 藏了这么久。
        var history = History();
        foreach (var s in history) s.Values[FinancialKeys.ShareCapital] = RealShares;

        var withShares = new FinancialAnalyzer()
            .Analyze("600519", "面值一元", history, 20.0, null, null, null, RealShares);
        var without = new FinancialAnalyzer()
            .Analyze("600519", "面值一元", history, 20.0, null, null, null, null);

        string Pe(FinancialAnalysisReport r) =>
            r.Sections.SelectMany(s => s.Lines).First(l => l.Label == "PE (TTM)").Value;

        Assert.Equal(Pe(withShares), Pe(without));
    }
}
