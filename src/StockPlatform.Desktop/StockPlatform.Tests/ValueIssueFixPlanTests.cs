using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【重新拉取失败股票】修值问题时**要发哪些请求**（<see cref="ValueIssueFixPlan"/>，2026-09-11）。
///
/// 这里最要紧的一条是"inconsistent 连基准 <c>day</c> 一起重抓"：V3 拿 <c>day</c> 当基准逐个比
/// 别的口径，报出来的永远是非基准口径，而 2026-09-11 手工修的 101 行 turnover 错值分布是
/// <c>day</c> 62 / <c>day_hfq</c> 39 / <c>day_raw</c> 0——错值 100% 不在"只抓被报口径"重抓的
/// 那一边，于是每轮都发请求、每轮都修不掉，Tries 从 2 爬到 5。这个错**不报任何异常**，
/// 只能靠这里的用例守住。
/// </summary>
public class ValueIssueFixPlanTests
{
    private static readonly DateTime D1 = new(2026, 8, 27);
    private static readonly DateTime D2 = new(2026, 8, 31);

    private static MissingBarRange R(string code, string gran, string reason,
                                     DateTime? from = null, DateTime? to = null) =>
        new()
        {
            Code = code, Granularity = gran, Reason = reason,
            From = from ?? D1, To = to ?? D1, Days = 1,
        };

    // ─────────────────── 抓不了的挑出来 ───────────────────

    [Fact]
    public void day_adj抓不来_原样留着()
    {
        var plan = ValueIssueFixPlan.Build(
            [R("600000", Granularity.DayAdj, AuditFindingKind.Inconsistent)], supportsHfq: true);

        Assert.Single(plan.Skipped);
        Assert.Empty(plan.Fetches);
    }

    [Fact]
    public void 源不给后复权时_只留下前复权那一段()
    {
        var qfq = R("600000", Granularity.Day, AuditFindingKind.Intraday);
        var hfq = R("600000", Granularity.DayHfq, AuditFindingKind.Intraday);
        var raw = R("600000", Granularity.DayRaw, AuditFindingKind.Intraday);

        var plan = ValueIssueFixPlan.Build([qfq, hfq, raw], supportsHfq: false);

        Assert.Equal([hfq, raw], plan.Skipped.OrderBy(x => x.Granularity).ToList());
        Assert.Equal(Granularity.Day, Assert.Single(plan.Fetches).Granularity);
    }

    // ─────────────────── 一组只抓一次 ───────────────────

    [Fact]
    public void 同一票同一口径的几段_合成一次抓取取并集()
    {
        // 同一 (票,口径) 常有几条不同 Reason 的记录：盘中固化的行量额自然也跟 day 对不上
        var plan = ValueIssueFixPlan.Build(
        [
            R("600000", Granularity.DayRaw, AuditFindingKind.Intraday, D1, D1),
            R("600000", Granularity.DayRaw, AuditFindingKind.Inconsistent, D2, D2),
        ], supportsHfq: true);

        var f = Assert.Single(plan.Fetches);
        Assert.Equal(D1, f.From);
        Assert.Equal(D2, f.To);
    }

    [Fact]
    public void 混着别的Reason就整段重抓_不能只覆盖三列()
    {
        // 盘中固化连 OHLC 都是错的，只刷量额换手修不掉
        var plan = ValueIssueFixPlan.Build(
        [
            R("600000", Granularity.DayRaw, AuditFindingKind.Intraday),
            R("600000", Granularity.DayRaw, AuditFindingKind.Inconsistent),
        ], supportsHfq: true);

        Assert.Equal(ValueFixWrite.WholeSegment, Assert.Single(plan.Fetches).Write);
    }

    [Fact]
    public void 全是inconsistent才只覆盖三列()
    {
        var plan = ValueIssueFixPlan.Build(
            [R("600000", Granularity.DayRaw, AuditFindingKind.Inconsistent)], supportsHfq: true);

        Assert.Equal(ValueFixWrite.ThreeColumns, plan.Fetches[0].Write);
    }

    // ─────────────────── 基准也要重抓 ───────────────────

    [Fact]
    public void inconsistent要连基准day一起抓()
    {
        var plan = ValueIssueFixPlan.Build(
            [R("603890", Granularity.DayRaw, AuditFindingKind.Inconsistent)], supportsHfq: true);

        Assert.Equal(2, plan.Fetches.Count);
        var baseline = Assert.Single(plan.Fetches.Where(f => f.IsBaselineRefetch));
        Assert.Equal(Granularity.Day, baseline.Granularity);
        Assert.Equal("603890", baseline.Code);
        Assert.Equal(D1, baseline.From);
        Assert.Equal(D1, baseline.To);
        // 基准也只覆盖三列——OHLC 是当年的复权基准，任何时候都不能被重抓的价格平移
        Assert.Equal(ValueFixWrite.ThreeColumns, baseline.Write);
    }

    [Fact]
    public void 同票两个口径同一区间_基准只抓一次()
    {
        var plan = ValueIssueFixPlan.Build(
        [
            R("603890", Granularity.DayRaw, AuditFindingKind.Inconsistent),
            R("603890", Granularity.DayHfq, AuditFindingKind.Inconsistent),
        ], supportsHfq: true);

        Assert.Equal(3, plan.Fetches.Count);
        Assert.Single(plan.Fetches.Where(f => f.IsBaselineRefetch));
    }

    [Fact]
    public void 同票两个口径区间不同_基准各抓各的区间()
    {
        var plan = ValueIssueFixPlan.Build(
        [
            R("603890", Granularity.DayRaw, AuditFindingKind.Inconsistent, D1, D1),
            R("603890", Granularity.DayHfq, AuditFindingKind.Inconsistent, D2, D2),
        ], supportsHfq: true);

        var baselines = plan.Fetches.Where(f => f.IsBaselineRefetch).ToList();
        Assert.Equal(2, baselines.Count);
        Assert.Equal([D1, D2], baselines.Select(b => b.From).OrderBy(d => d).ToList());
    }

    [Fact]
    public void 整段重抓那条路不额外抓基准()
    {
        // 盘中固化走 InsertOrRefreshUnconfirmed 整段覆盖，基准那边有自己的段会被单独报出来
        var plan = ValueIssueFixPlan.Build(
            [R("600000", Granularity.DayRaw, AuditFindingKind.Intraday)], supportsHfq: true);

        Assert.Single(plan.Fetches);
        Assert.DoesNotContain(plan.Fetches, f => f.IsBaselineRefetch);
    }

    [Fact]
    public void 段本身就是基准口径时_不会抓第二遍()
    {
        // V3 报不出 day（它是基准），但别的判据会；无论如何不该重复抓同一口径
        var plan = ValueIssueFixPlan.Build(
            [R("600000", Granularity.Day, AuditFindingKind.Inconsistent)], supportsHfq: true);

        Assert.Single(plan.Fetches);
        Assert.False(plan.Fetches[0].IsBaselineRefetch);
    }
}
