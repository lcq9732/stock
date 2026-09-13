using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 按量分档的判据（2026-09-12）。
///
/// 起因：实机上康辰药业解禁 **74 股**（两千块钱），跟"解禁占流通 30%"用的是同一个 A 档。
/// 日期回答不了"要不要管"，**量才回答**——而量在挂观察项的时候还不知道，
/// 所以档位必须在求值时按实际的数重算。
/// </summary>
public class WatchPriorityTests
{
    private static WatchItem ShareLift() => new()
    {
        Code = "603590", Kind = WatchKind.ScheduleAhead, Expr = "ShareLift",
        Op = WatchOp.Within, Threshold = 30, Priority = "A", Reason = "限售解禁",
    };

    [Theory]
    [InlineData(30.0, "A")]    // 实打实的抛压
    [InlineData(10.0, "A")]    // 边界
    [InlineData(9.99, "B")]
    [InlineData(3.0, "B")]     // 边界
    [InlineData(2.99, "C")]
    [InlineData(0.0032, "C")]  // ★ 康辰药业那 74 股
    [InlineData(0.03, "C")]    // ★ 中国神华那 4.58 万股
    public void 解禁按占流通比分档(double freeRatio, string expected)
        => Assert.Equal(expected, WatchPriority.Resolve(ShareLift(), freeRatio));

    /// <summary>抽不到占比时**不猜**，退回兜底档。</summary>
    [Fact]
    public void 占比抽不到_退回兜底档()
        => Assert.Equal("B", WatchPriority.Resolve(ShareLift(), null));

    /// <summary>
    /// ★ 只有"轻重取决于量"的事项才按量分档。
    /// 回购 stage 跃迁（开始买了没有）跟金额无关——买 1 亿还是 100 亿，
    /// "从没买变成买了"这件事本身都是 A 档。
    /// </summary>
    [Fact]
    public void 回购stage跃迁_不按量降档()
    {
        var item = new WatchItem
        {
            Code = "300750", Kind = WatchKind.PlanStage, Expr = PlanKind.Buyback,
            Op = WatchOp.StageChange, Priority = "A", Reason = "回购方案进行中",
        };

        Assert.Equal("A", WatchPriority.Resolve(item, 0.001));
        Assert.Equal("A", WatchPriority.Resolve(item, null));
    }

    /// <summary>业绩预告不按幅度降档——预增 5% 和预增 300% 都值得看一眼。</summary>
    [Fact]
    public void 业绩预告_保持挂上时定的档()
    {
        var item = new WatchItem
        {
            Code = "000425", Kind = WatchKind.EventRecent, Expr = "EarningsForecast",
            Op = WatchOp.Within, Threshold = 7, Priority = "A", Reason = "业绩预告",
        };

        Assert.Equal("A", WatchPriority.Resolve(item, 0.5));
    }

    /// <summary>★ Magnitude 跟 Value 是两个数，不能混：解禁那路 Value 是"还有几天"。</summary>
    [Fact]
    public void 别把还有几天当成占流通比()
    {
        // 还有 27 天，占流通 0.03% —— 如果误把 27 当占比就会判成 A 档
        Assert.Equal("C", WatchPriority.Resolve(ShareLift(), 0.03));
    }
}
