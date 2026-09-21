using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「抓回来这一段K线，哪几根该插、哪几根该覆盖、算不算漂移」的判据
/// （2026-09-21 从 <c>FetchOrchestrator.ProcessOneStockAsync</c> 抽出来，见
/// <see cref="BarWritePlanner"/> 和 doc/bar-tasks-migration-design.md §1）。
///
/// ⚠ 这套判据错一处全是**静默**的：今天那几根走错路 → 盘中半成品被永久固化；
/// 漏判漂移 → 那只票的历史留着旧基准、跟新段接不上。所以它是纯函数、单独测。
/// </summary>
public class BarWritePlannerTests
{
    private static readonly DateTime Today = new(2026, 9, 21);

    private static Bar Row(DateTime day, double close) =>
        new() { Code = "600000", Granularity = Granularity.Day, PeriodStart = day, Close = close };

    private static Dictionary<DateTime, double> Stored(params (DateTime Day, double Close)[] rows)
        => rows.ToDictionary(r => r.Day.Date, r => r.Close);

    // ── ① 今天那几根必须覆盖写（盘中抓的半成品不能被永久固化）──

    [Fact]
    public void 今天那根单独归到Today不进插入队列()
    {
        var plan = BarWritePlanner.Plan([Row(Today, 10.0)], Today, Stored());

        Assert.Single(plan.Today);
        Assert.Empty(plan.ToInsert);
        Assert.Empty(plan.ToOverwrite);
    }

    /// <summary>库里今天那根已经有了，照样要走覆盖——它可能是盘中抓的。</summary>
    [Fact]
    public void 今天那根库里已有也仍要覆盖()
    {
        var plan = BarWritePlanner.Plan([Row(Today, 10.5)], Today, Stored((Today, 10.0)));

        Assert.Single(plan.Today);
        Assert.Empty(plan.ToInsert);
    }

    // ── ② 更早的日期只插库里没有的 ──

    [Fact]
    public void 库里没有的历史行才插()
    {
        var d1 = Today.AddDays(-2);
        var d2 = Today.AddDays(-1);
        var plan = BarWritePlanner.Plan([Row(d1, 9.0), Row(d2, 9.5)], Today, Stored((d1, 9.0)));

        Assert.Equal([d2], plan.ToInsert.Select(b => b.PeriodStart));
        Assert.Empty(plan.ToOverwrite);
        Assert.False(plan.Drifted);
    }

    /// <summary>值对得上的历史行什么都不做——重复抓到的是同一个事实。</summary>
    [Fact]
    public void 值对得上的历史行不动()
    {
        var d = Today.AddDays(-3);
        var plan = BarWritePlanner.Plan([Row(d, 9.0)], Today, Stored((d, 9.0)));

        Assert.False(plan.HasWork);
    }

    // ── ③ 漂移才覆盖，而且要开着开关 ──

    [Fact]
    public void 值对不上且开了检测_覆盖并标漂移()
    {
        var d = Today.AddDays(-3);
        var plan = BarWritePlanner.Plan([Row(d, 8.5)], Today, Stored((d, 9.0)), driftCheck: true);

        Assert.Single(plan.ToOverwrite);
        Assert.True(plan.Drifted);
        Assert.Empty(plan.ToInsert);
    }

    /// <summary>后复权/不复权那两路不开检测：基准本来就不随分红变，比对纯属浪费。</summary>
    [Fact]
    public void 值对不上但没开检测_什么都不做()
    {
        var d = Today.AddDays(-3);
        var plan = BarWritePlanner.Plan([Row(d, 8.5)], Today, Stored((d, 9.0)), driftCheck: false);

        Assert.False(plan.HasWork);
        Assert.False(plan.Drifted);
    }

    /// <summary>阈值＝"相对 0.2% 与绝对 0.005 元的较大者"：浮点噪声不算漂移，几分钱的分红调整算。</summary>
    [Theory]
    [InlineData(10.000, 10.000, false)]   // 完全相同
    [InlineData(10.000, 10.004, false)]   // 0.004 < max(0.005, 0.02) → 噪声
    [InlineData(10.000, 9.970, true)]     // 0.03 > 0.02 → 漂移
    [InlineData(1.000, 1.004, false)]     // 低价股：0.004 < 0.005 绝对下限
    [InlineData(1.000, 0.990, true)]      // 0.01 > 0.005 → 漂移
    public void 漂移阈值(double stored, double fresh, bool drifted)
        => Assert.Equal(drifted, BarWritePlanner.IsDrifted(stored, fresh));

    // ── ④ 覆盖重抓模式：整段以数据源当前基准为准 ──

    [Fact]
    public void 覆盖重抓_旧行整段覆盖且不比对()
    {
        var d1 = Today.AddDays(-2);
        var d2 = Today.AddDays(-1);
        var plan = BarWritePlanner.Plan(
            [Row(d1, 9.0), Row(d2, 9.5), Row(Today, 10.0)], Today, Stored(), overwrite: true);

        Assert.Equal(2, plan.ToOverwrite.Count);
        Assert.Empty(plan.ToInsert);
        Assert.Single(plan.Today);
        Assert.False(plan.Drifted);       // 覆盖重抓不是"发现漂移"，不该排进重取名单
    }

    [Fact]
    public void 一根都没抓到_空方案()
    {
        var plan = BarWritePlanner.Plan([], Today, Stored());

        Assert.False(plan.HasWork);
        Assert.False(plan.Drifted);
    }
}

/// <summary>
/// 「漂移的这些票，哪几只真需要重取更早历史」的判据（<see cref="QfqRepairPlanner"/>）。
/// 判据只有一条：**历史比手上那一页更长的才需要**——那一页已经就地覆盖好了。
/// </summary>
public class QfqRepairPlannerTests
{
    private static readonly DateTime Today = new(2026, 9, 21);

    /// <summary>手上那一页盖到哪天（含）。</summary>
    private static DateTime PageStart => Today.AddDays(-BarWritePlanner.DriftCheckLookbackDays);

    [Fact]
    public void 历史比那一页更长_要重取()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = PageStart.AddDays(-1) };

        Assert.Equal(["600000"], QfqRepairPlanner.SelectForRepair(["600000"], earliest, Today));
    }

    [Fact]
    public void 短历史已被那一页盖全_不重取()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = PageStart.AddDays(1) };

        Assert.Empty(QfqRepairPlanner.SelectForRepair(["600000"], earliest, Today));
    }

    /// <summary>库里查不到最早日期的（比如刚建档）不排进去——排了也算不出该补哪一段。</summary>
    [Fact]
    public void 没有最早日期的跳过()
        => Assert.Empty(QfqRepairPlanner.SelectForRepair(["600000"], new Dictionary<string, DateTime>(), Today));

    [Fact]
    public void 去重并排序()
    {
        var old = PageStart.AddDays(-10);
        var earliest = new Dictionary<string, DateTime> { ["600519"] = old, ["000001"] = old };

        Assert.Equal(["000001", "600519"],
            QfqRepairPlanner.SelectForRepair(["600519", "000001", "600519"], earliest, Today));
    }
}
