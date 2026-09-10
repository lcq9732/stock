using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 收益率自检（<see cref="AdjustFactorCalculator.VerifyReturns"/>）的两档判据（2026-09-10 新增）。
///
/// 这个方法原来一条测试都没有，而它是**复权算法唯一的硬指标**——数据源那份后复权就是栽在这上面
/// （加法式分红项阻尼波动，非除权日收益率被压掉三四成）。没测试的后果是判据写歪了也没人知道：
///
///   老判据：<c>diff &gt; 1e-9 &amp;&amp; diff &lt; 0.001</c> 就计一个"不合格"。
///
/// 它把两件事搞反了。0.1% 以内的偏差是因子累乘的浮点噪声，**无害**，却被报成
/// 「⚠ 收益率对不上真实值（算法可能被改坏了）」——实测每轮几百天（4020 只票、一千多万个交易日
/// 里 743 天，占 0.006%），警告每轮都响，于是没人再看它。而真出算法问题时偏差会**远超** 0.1%，
/// 正好落在那个 <c>&lt; 0.001</c> 之外，被当成"除权日本来就该不一样"一律放过——
/// <b>真问题一天都报不出来</b>。
///
/// 现在两档分开：浮点档只报占比，另一档拿 <see cref="AdjustFactorCalculator.Report.AppliedDays"/>
/// 区分"那天 factor 真动过（应该不一样）"和"没动过却差很多（那就是坏了）"。
/// </summary>
public class ReturnSelfCheckTests
{
    private static Bar Raw(string day, double close, double open = 0) => new()
    {
        Code = "600030",
        Granularity = Granularity.DayRaw,
        PeriodStart = DateTime.Parse(day),
        Open = open > 0 ? open : close, Close = close,
        High = Math.Max(open > 0 ? open : close, close), Low = Math.Min(open > 0 ? open : close, close),
        FetchedAt = new DateTime(2026, 9, 10, 20, 0, 0),
    };

    /// <summary>照着 raw 造一条"完全没有除权"的复权序列，可指定某天乘一个扰动。</summary>
    private static List<Bar> AdjOf(IReadOnlyList<Bar> raw, int perturbIndex = -1, double perturbFactor = 1.0)
    {
        var list = new List<Bar>();
        for (int i = 0; i < raw.Count; i++)
        {
            double k = i == perturbIndex ? perturbFactor : 1.0;
            list.Add(new Bar
            {
                Code = raw[i].Code, Granularity = Granularity.DayAdj, PeriodStart = raw[i].PeriodStart,
                Open = raw[i].Open * k, Close = raw[i].Close * k,
                High = raw[i].High * k, Low = raw[i].Low * k,
                FetchedAt = new DateTime(2026, 9, 10, 21, 0, 0),
            });
        }
        return list;
    }

    private static readonly List<Bar> Plain =
    [
        Raw("2026-08-25", 10.00),
        Raw("2026-08-26", 10.20),
        Raw("2026-08-27", 10.10),
        Raw("2026-08-28", 10.55),
    ];

    // ─────────────────── 浮点档：无害，不该报成警告 ───────────────────

    [Fact]
    public void 没有除权且一模一样_两档都是零()
    {
        var check = AdjustFactorCalculator.VerifyReturns(Plain, AdjOf(Plain), new HashSet<DateTime>());

        Assert.Equal(3, check.Compared);        // 4 根K线只有 3 个收益率
        Assert.Equal(0, check.MinorDrift);
        Assert.Equal(0, check.RealError);
    }

    /// <summary>把最后一天乘 1.00002：只影响最后那一个收益率，偏差约 2e-5，落在浮点档。</summary>
    [Fact]
    public void 千分之一以内的偏差_算浮点噪声不算错()
    {
        var check = AdjustFactorCalculator.VerifyReturns(
            Plain, AdjOf(Plain, perturbIndex: 3, perturbFactor: 1.00002), new HashSet<DateTime>());

        Assert.Equal(1, check.MinorDrift);
        Assert.Equal(0, check.RealError);       // 关键：浮点噪声**不进**警告那一档
    }

    // ─────────────── 真错档：老判据完全没查的那一侧 ───────────────

    /// <summary>把最后一天乘 1.05：偏差 5%，而那天 factor 压根没动过——这就是"算法被改坏了"。</summary>
    [Fact]
    public void 没除权却差出百分之五_必须报成真错()
    {
        var check = AdjustFactorCalculator.VerifyReturns(
            Plain, AdjOf(Plain, perturbIndex: 3, perturbFactor: 1.05), new HashSet<DateTime>());

        Assert.Equal(1, check.RealError);
        Assert.Equal(0, check.MinorDrift);      // 大偏差不该同时落进浮点档
    }

    /// <summary>
    /// 老判据的行为固定下来当反面对照：<c>diff &lt; 0.001</c> 这个上界会把上面那个 5% 的偏差
    /// 直接漏掉。这条测试证明"漏"是判据本身的问题，不是数据的问题。
    /// </summary>
    [Fact]
    public void 老判据的上界会把真错漏掉()
    {
        var adj = AdjOf(Plain, perturbIndex: 3, perturbFactor: 1.05);
        int oldStyleBad = 0;
        for (int i = 1; i < Plain.Count; i++)
        {
            double diff = Math.Abs(adj[i].Close / adj[i - 1].Close - Plain[i].Close / Plain[i - 1].Close);
            if (diff > 1e-9 && diff < AdjustFactorCalculator.MinorLimit) oldStyleBad++;
        }
        Assert.Equal(0, oldStyleBad);           // 老判据：一天都没报
    }

    // ─────────────── 真除权日的大偏差是应该的 ───────────────

    /// <summary>
    /// 10派5：除权日复权后的收益率必然跟不复权差 4.9%，那正是复权在起作用。
    /// 拿 <see cref="AdjustFactorCalculator.Report.AppliedDays"/> 判就不会误报；
    /// 同一份数据把名单换成空集，立刻变成 1 个真错——说明这个区分确实是靠名单做到的。
    /// </summary>
    [Fact]
    public void 真除权日差很多_不算错()
    {
        List<Bar> raw =
        [
            Raw("2026-08-25", 10.00),
            Raw("2026-08-26", 10.20),
            Raw("2026-08-27", 9.70, open: 9.70),   // 前收 10.20 − 每股派 0.5 = 除权参考价 9.70
        ];
        var events = new[] { new AdjustFactorCalculator.ExDividend(DateTime.Parse("2026-08-27"), 0, 0.5) };

        var adj = AdjustFactorCalculator.BuildAdjusted("600030", raw, events, out var report);
        Assert.Equal(1, report.Applied);
        Assert.Contains(DateTime.Parse("2026-08-27"), report.AppliedDays);

        var check = AdjustFactorCalculator.VerifyReturns(raw, adj, report.AppliedDays);
        Assert.Equal(0, check.RealError);
        Assert.Equal(0, check.MinorDrift);      // 复权抵得精确，除权日之外零偏差

        // 同样的偏差，只是"那天没动过 factor" —— 判据必须翻过来
        Assert.Equal(1, AdjustFactorCalculator.VerifyReturns(raw, adj, new HashSet<DateTime>()).RealError);
    }

    /// <summary>
    /// 除权日**停牌**：事件挂到复牌那天，所以名单里记的是复牌日、不是公告的除权日。
    /// 这就是为什么不能直接拿入参 events 的 ExDate 去比——比错日子会凭空报一个真错。
    /// </summary>
    [Fact]
    public void 除权日停牌_名单记的是复牌那天()
    {
        List<Bar> raw =
        [
            Raw("2026-08-25", 10.00),
            Raw("2026-08-26", 10.20),
            Raw("2026-08-31", 9.70, open: 9.70),   // 08-27 除权，停牌到 08-31 才复牌
        ];
        var events = new[] { new AdjustFactorCalculator.ExDividend(DateTime.Parse("2026-08-27"), 0, 0.5) };

        var adj = AdjustFactorCalculator.BuildAdjusted("600030", raw, events, out var report);
        Assert.Contains(DateTime.Parse("2026-08-31"), report.AppliedDays);
        Assert.DoesNotContain(DateTime.Parse("2026-08-27"), report.AppliedDays);
        Assert.Equal(0, AdjustFactorCalculator.VerifyReturns(raw, adj, report.AppliedDays).RealError);

        // 拿公告日当名单（错的做法）：复牌那天不在名单里，于是误报
        var wrong = new HashSet<DateTime> { DateTime.Parse("2026-08-27") };
        Assert.Equal(1, AdjustFactorCalculator.VerifyReturns(raw, adj, wrong).RealError);
    }

    /// <summary>
    /// 被价格校验剔掉的假除权（破产重整的资本公积转增）：factor 没动，所以不在名单里，
    /// 而复权价也就等于原价——两边收益率一致，**不该**报错。剔除本身已经在别处报了。
    /// </summary>
    [Fact]
    public void 假除权被剔除后_收益率自检仍然干净()
    {
        List<Bar> raw =
        [
            Raw("2026-08-25", 10.00),
            Raw("2026-08-26", 10.20),
            Raw("2026-08-27", 10.33, open: 10.33),   // 记着 10转25，实际只涨了 1.27%
        ];
        var events = new[] { new AdjustFactorCalculator.ExDividend(DateTime.Parse("2026-08-27"), 2.5, 0) };

        var adj = AdjustFactorCalculator.BuildAdjusted("600030", raw, events, out var report);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(report.AppliedDays);

        var check = AdjustFactorCalculator.VerifyReturns(raw, adj, report.AppliedDays);
        Assert.Equal(0, check.RealError);
        Assert.Equal(0, check.MinorDrift);
    }

    // ─────────────────── 边界 ───────────────────

    /// <summary>不传名单 = 反向判据关闭（只有测试和外部调用方图省事时才会这样）。</summary>
    [Fact]
    public void 不传除权名单时_反向判据不生效()
    {
        var check = AdjustFactorCalculator.VerifyReturns(Plain, AdjOf(Plain, 3, 1.05));
        Assert.Equal(0, check.RealError);
    }

    /// <summary>前收 ≤0 的天不参与——前复权减法式会算出负价（万科 1997 年 −8.17），除法没意义。</summary>
    [Fact]
    public void 前收非正的天不计入比较()
    {
        List<Bar> raw = [Raw("2026-08-25", 0), Raw("2026-08-26", 10.20), Raw("2026-08-27", 10.10)];
        var check = AdjustFactorCalculator.VerifyReturns(raw, AdjOf(raw), new HashSet<DateTime>());

        Assert.Equal(1, check.Compared);         // 只有 08-26→08-27 那一个收益率算数
    }

    [Fact]
    public void 只有一根K线时不比较()
    {
        List<Bar> raw = [Raw("2026-08-25", 10.00)];
        var check = AdjustFactorCalculator.VerifyReturns(raw, AdjOf(raw), new HashSet<DateTime>());

        Assert.Equal(0, check.Compared);
        Assert.Equal(0, check.MinorDrift);
        Assert.Equal(0, check.RealError);
    }
}
