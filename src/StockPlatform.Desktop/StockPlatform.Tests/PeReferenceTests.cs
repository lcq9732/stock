using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 「PE (TTM)」那一行的参考值和解释（2026-09-14）。
///
/// 补这两样是因为光一个"10.3 倍"没有位置感：不知道它在全市场排哪儿，
/// 也不知道这个倍数意味着什么。补完之后：
///     参考：全市场中位 38.6 倍　处在最便宜的 10%
///     → 已赚到的利润按 12 倍能撑 492 亿，超过市值 420 亿 —— 现价没有为未来付钱
/// </summary>
public class PeReferenceTests
{
    private readonly ITestOutputHelper _out;
    public PeReferenceTests(ITestOutputHelper output) => _out = output;

    private static FinancialSnapshot Snap(DateTime date, double revenue, double npParent,
                                          double shareCapital)
        => new()
        {
            ReportDate = date,
            Values = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [FinancialKeys.Revenue] = revenue,
                [FinancialKeys.NetProfitParent] = npParent,
                [FinancialKeys.NetProfit] = npParent,
                [FinancialKeys.Ocf] = npParent,
                [FinancialKeys.EquityParent] = 100e8,
                [FinancialKeys.ShareCapital] = shareCapital,
                [FinancialKeys.OperCost] = revenue * 0.75,
                [FinancialKeys.TotalAssets] = 140e8,
                [FinancialKeys.TotalLiabilities] = 40e8,
            },
        };

    /// <summary>构造一只 TTM 归母 = 10 亿、股本 10 亿股的票，靠股价调 PE。</summary>
    private static AnalysisLine? PeLine(double price)
    {
        var history = new List<FinancialSnapshot>
        {
            Snap(new DateTime(2026, 6, 30), 100e8, 5e8, 10e8),
            Snap(new DateTime(2025, 12, 31), 200e8, 10e8, 10e8),
            Snap(new DateTime(2025, 6, 30), 100e8, 5e8, 10e8),
        };   // TTM = 5 + 10 − 5 = 10 亿
        var report = new FinancialAnalyzer().Analyze("000001", "样本", history, price);
        return report.Sections.SelectMany(s => s.Lines)
                     .FirstOrDefault(l => l.Label == "PE (TTM)");
    }

    [Fact]
    public void 便宜的票_说清楚现价没有为未来付钱()
    {
        // 股价 10 元 × 10 亿股 = 100 亿市值，TTM 10 亿 → PE 10（低于基准 12）
        var line = PeLine(10.0);
        Assert.NotNull(line);
        _out.WriteLine($"{line!.Value}　{line.Reference}\n→ {line.Note}");

        Assert.Equal("10.0", line.Value);
        Assert.Contains("超过市值", line.Note, StringComparison.Ordinal);
        Assert.Contains("没有为未来付钱", line.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 贵的票_说清楚多少比例靠未来()
    {
        // 股价 120 元 → 市值 1200 亿，PE 120 → 已赚的利润只撑得起 120 亿，其余 90%
        var line = PeLine(120.0);
        Assert.NotNull(line);
        _out.WriteLine($"{line!.Value}　{line.Reference}\n→ {line.Note}");

        Assert.Contains("对未来的定价", line.Note, StringComparison.Ordinal);
        Assert.Contains("90%", line.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ **这条是这组用例里最重要的一条。**
    ///
    /// 界面按 Verdict 上色。把"贵"标成 Warn/Bad 就是在说"贵=要出事"，
    /// 而 FactorLab 十分组实测的结论正相反：D1（最贵那组）年化 +10.6%，是十组里**最高**的，
    /// 而且曲线呈 U 型不单调（两头高、中间低）。颜色表达不了这种形状，只会误导。
    ///
    /// 这一行只陈述事实，不做判断——谁想给它加颜色，先看这段注释和那组回测。
    /// </summary>
    [Theory]
    [InlineData(5.0)]      // PE 5，很便宜
    [InlineData(10.0)]     // PE 10
    [InlineData(50.0)]     // PE 50
    [InlineData(300.0)]    // PE 300，极贵
    public void 无论贵贱都必须是中性_不许上色(double price)
    {
        var line = PeLine(price);
        Assert.NotNull(line);
        Assert.Equal(Verdict.Neutral, line!.Verdict);
    }

    /// <summary>
    /// 分档要真的分开档 —— 但**不钉具体措辞**。
    ///
    /// 2026-09-14 措辞从"处在最便宜的 10%"改成了"全市场PE 最低的 10%"，改得对：
    /// PE 行钉死 Verdict.Neutral 不上色正是因为"贵"不预示跌，却在文字里写"便宜"，
    /// 等于把刚守住的纪律又破了。原来那版用例钉着旧文本，于是在别人改对的时候报了假警。
    /// 措辞该能改，**分档边界不该乱**——钉后者。
    /// </summary>
    [Fact]
    public void 分档边界要真的分开()
    {
        var m = MarketPeStats.Builtin;
        var buckets = new[]
        {
            m.DescribePosition(m.P10 - 1),
            m.DescribePosition(m.P25 - 1),
            m.DescribePosition(m.Median - 1),
            m.DescribePosition(m.Median + 1),
            m.DescribePosition(m.P75 + 1),
            m.DescribePosition(m.P90 + 1),
        };
        foreach (var b in buckets) _out.WriteLine(b);

        Assert.Equal(6, buckets.Distinct().Count());      // 六档互不相同
        Assert.All(buckets, b => Assert.False(string.IsNullOrWhiteSpace(b)));
    }

    /// <summary>
    /// 分位必须单调递增。这是**改不得**的性质：谁更新了 Builtin 的数、顺序写乱了，这条会红。
    ///
    /// ⚠ 原来这里钉的是 <c>Median == 38.6</c>，今天被打红了 —— 因为那个数本身是错的：
    ///   用 share_capital 当股数算 PE，而那是「实收资本」、是金额，面值不是 1 元的票全错
    ///   （中芯国际算出 3.9、真值 139.5）。钉一个会变的数，等于把 bug 也钉了进去。
    /// </summary>
    [Fact]
    public void 分位必须单调递增()
    {
        var m = MarketPeStats.Builtin;
        _out.WriteLine($"P10 {m.P10}　P25 {m.P25}　中位 {m.Median}　P75 {m.P75}　P90 {m.P90}");

        Assert.True(m.P10 < m.P25, "P10 应小于 P25");
        Assert.True(m.P25 < m.Median, "P25 应小于中位");
        Assert.True(m.Median < m.P75, "中位应小于 P75");
        Assert.True(m.P75 < m.P90, "P75 应小于 P90");
        Assert.True(m.SampleSize > 1000, "样本太小，分位没有代表性");
    }
}
