using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 回测序列（day_adj）的时间戳语义（2026-09-04）。
///
/// 修的是一个**沉默失效**的判据：FetchOrchestrator.CodesWithStaleAdjEvents 用
/// 「除权事件.fetched_at &gt; day_adj.fetched_at」判断"事件变新了、该重算"，但
/// BuildAdjusted 当时是把 rawBars 的 FetchedAt 原样抄进 day_adj 的——那是**源K线的抓取时刻**，
/// 跟"上次什么时候重算过"毫无关系。
///
/// 后果不是报错，而是判据静默为假：实测配股 09-02 抓入、K线 09-03 抓取，
/// 判据算出 09-02 &gt; 09-03 = false，库里 642 只有配股记录的票**一只都没被检出**
/// （全库只碰巧检出 7 只，那 7 只是事件恰好抓得比K线晚）。
///
/// 这种 bug 没有任何症状——数据静静地错着。所以这里把时间戳的语义钉死。
/// </summary>
public class AdjSeriesStalenessTests
{
    private static List<Bar> RawBars(DateTime fetchedAt) =>
    [
        Bar("2026-08-25", 10.00, fetchedAt),
        Bar("2026-08-26", 10.20, fetchedAt),
        Bar("2026-08-27", 10.10, fetchedAt),
    ];

    private static Bar Bar(string day, double close, DateTime fetchedAt) => new()
    {
        Code = "600030",
        Granularity = Granularity.DayRaw,
        PeriodStart = DateTime.Parse(day),
        Open = close, Close = close, High = close, Low = close,
        FetchedAt = fetchedAt,
    };

    /// <summary>day_adj 的时间戳必须是"算出来的时刻"，不能是源K线的抓取时刻。</summary>
    [Fact]
    public void FetchedAt_IsComputeTime_NotSourceBarTime()
    {
        var sourceFetchedAt = new DateTime(2026, 9, 3, 20, 35, 17);   // K线抓取时刻
        var computedAt = new DateTime(2026, 9, 4, 9, 0, 0);           // 重算时刻

        var adj = AdjustFactorCalculator.BuildAdjusted(
            "600030", RawBars(sourceFetchedAt), [], out _, computedAt);

        Assert.NotEmpty(adj);
        Assert.All(adj, b => Assert.Equal(computedAt, b.FetchedAt));
        // 关键：不能等于源K线的时间——那正是原来的 bug
        Assert.All(adj, b => Assert.NotEqual(sourceFetchedAt, b.FetchedAt));
    }

    /// <summary>
    /// 判据本身：事件抓取时刻晚于重算时刻 = 该重算。
    /// 这里直接断言那条比较的语义，把"拿哪个时间当基准"固定下来——
    /// 用源K线时刻当基准时，下面第二个断言会翻过来（事件 09-02 &lt; K线 09-03），
    /// 那就是 642 只票被漏掉的原因。
    /// </summary>
    [Fact]
    public void EventNewerThanRebuild_MeansStale()
    {
        var sourceFetchedAt = new DateTime(2026, 9, 3, 20, 35, 17);
        var rebuiltAt = new DateTime(2026, 9, 4, 9, 0, 0);
        var rightsFetchedAt = new DateTime(2026, 9, 4, 14, 15, 20);   // 重算之后才抓到的配股

        var adj = AdjustFactorCalculator.BuildAdjusted(
            "600030", RawBars(sourceFetchedAt), [], out _, rebuiltAt);
        var adjStamp = adj.Max(b => b.FetchedAt);

        Assert.True(rightsFetchedAt > adjStamp, "重算之后抓到的事件，必须被判为 stale");
        // 反例留个记录：拿源K线时刻当基准的话，同一个事件会被判成"不用重算"
        Assert.False(rightsFetchedAt < sourceFetchedAt);
    }

    /// <summary>
    /// 顺带把配股的除权公式钉住：除权参考价 = (前收 − 分红 + 配股价×比例) ÷ (1 + 送转 + 比例)。
    /// 中信证券 2022-01 那次 10配1.5@14.43、前收 25.70，理论跳空 −5.72%。
    /// </summary>
    [Fact]
    public void RightsIssue_AppliesToAdjustFactor()
    {
        List<Bar> raw =
        [
            Bar("2022-01-18", 25.70, DateTime.Now),
            Bar("2022-01-27", 24.15, DateTime.Now),   // 配股除权日（缴款期停牌，效应落在复牌日）
        ];
        var ex = new AdjustFactorCalculator.ExDividend(
            new DateTime(2022, 1, 27), 0, 0, RightsRatio: 0.15, RightsPrice: 14.43);

        var withRights = AdjustFactorCalculator.BuildAdjusted("600030", raw, [ex], out var rp);
        var without = AdjustFactorCalculator.BuildAdjusted("600030", raw, [], out _);

        Assert.Equal(1, rp.Applied);           // 通过了价格校验、真的应用了
        Assert.Equal(0, rp.Skipped);

        // 不还原配股：除权日看着跌了 6%（假阴线）
        double gapWithout = without[1].Close / without[0].Close - 1;
        Assert.True(gapWithout < -0.05, $"不还原时该看到假阴线，实际 {gapWithout:P2}");

        // 还原之后只剩当天的真实涨跌（理论 −5.72% 与实际 −6.03% 的差，约 −0.33%）
        double gapWith = withRights[1].Close / withRights[0].Close - 1;
        Assert.True(gapWith > -0.01, $"还原后不该还有大阴线，实际 {gapWith:P2}");
    }
}
