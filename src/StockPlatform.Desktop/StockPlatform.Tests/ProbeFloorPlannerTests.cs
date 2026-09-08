using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// "数据源没有更早数据"水位的判定（<see cref="ProbeFloorPlanner"/>，写进 BarProbeFloor 表）。
///
/// 这里每一条都是安全用例，不是功能用例：水位记高了，那只票的历史会被**永久跳过**，而且不报错、
/// 日志上只显示成"本地已是最新"。所以两道前提各有一条回归测试钉着——
/// 尤其 <see cref="DoesNotRecordWhenRequestEndIsNotBeforeLocalEarliest"/>：日常增量抓的是
/// "到今天为止"，周末跑一次也会返回空，那种空要是记了水位，水位就会被抬到明天。
/// </summary>
public class ProbeFloorPlannerTests
{
    private static Dictionary<string, DateTime> Earliest(params (string Code, DateTime Day)[] rows)
        => rows.ToDictionary(r => r.Code, r => r.Day, StringComparer.Ordinal);

    // ── 正常情形：往前补缺口、返回空 → 记水位 ────────────────────────────

    [Fact]
    public void RecordsFloorJustAfterRequestedEnd()
    {
        // 2020-06-01 上市的票，被请求 1990~2016（区间回补），返回空 → 2017-01-01 之前确认没有
        var plan = ProbeFloorPlanner.Plan(
            [("300750", new DateTime(2016, 12, 31))],
            Earliest(("300750", new DateTime(2020, 6, 1))));

        Assert.Equal([("300750", new DateTime(2017, 1, 1))], plan);
    }

    [Fact]
    public void FloorNeverGoesPastLocalEarliest()
    {
        // 缺口恰好补到本地最早一根的前一天：水位最多只能抬到本地最早那天，不能再往后
        var plan = ProbeFloorPlanner.Plan(
            [("600000", new DateTime(1991, 1, 1))],
            Earliest(("600000", new DateTime(1991, 1, 2))));

        Assert.Equal([("600000", new DateTime(1991, 1, 2))], plan);
    }

    [Fact]
    public void TakesLatestEndWhenProbedMoreThanOnce()
    {
        var plan = ProbeFloorPlanner.Plan(
            [("300750", new DateTime(2010, 12, 31)), ("300750", new DateTime(2016, 12, 31))],
            Earliest(("300750", new DateTime(2020, 6, 1))));

        Assert.Equal([("300750", new DateTime(2017, 1, 1))], plan);
    }

    // ── 前提 1：本地一根都没有 → 不敢下结论 ──────────────────────────────

    [Fact]
    public void DoesNotRecordWhenNoLocalBarsAtAll()
    {
        // 本地没有这只票的任何K线：返回空可能是代码不存在、也可能是接口抽风，没有旁证。
        // 而且这种票下一轮也只花 1 个请求，不值得为省它冒"永久跳过"的风险。
        var plan = ProbeFloorPlanner.Plan(
            [("999999", new DateTime(2016, 12, 31))],
            Earliest());

        Assert.Empty(plan);
    }

    // ── 前提 2（回归）：请求终点不早于本地最早一根 → 不是"往前补缺口"，不许记 ──

    [Fact]
    public void DoesNotRecordWhenRequestEndIsNotBeforeLocalEarliest()
    {
        var today = new DateTime(2026, 9, 5);       // 周六
        var earliest = Earliest(("600000", new DateTime(2016, 1, 4)));

        // 日常增量：窗口是 [水位线, 今天]，周末跑就会"成功但返回 0 行"。
        // 要是这种空也记水位，水位会被抬到 2026-09-06，这只票的全部历史从此永久跳过。
        Assert.Empty(ProbeFloorPlanner.Plan([("600000", today)], earliest));

        // 边界：终点正好等于本地最早那天，也不算"更早的那段没有"
        Assert.Empty(ProbeFloorPlanner.Plan([("600000", new DateTime(2016, 1, 4))], earliest));
    }

    [Fact]
    public void MixedBatchKeepsOnlyTheSafeOnes()
    {
        var plan = ProbeFloorPlanner.Plan(
            [
                ("300750", new DateTime(2016, 12, 31)),   // 可判定
                ("999999", new DateTime(2016, 12, 31)),   // 本地没有 → 不记
                ("600000", new DateTime(2026, 9, 5)),     // 终点是今天 → 不记
            ],
            Earliest(("300750", new DateTime(2020, 6, 1)), ("600000", new DateTime(2016, 1, 4))));

        Assert.Equal([("300750", new DateTime(2017, 1, 1))], plan);
    }

    [Fact]
    public void EmptyInputYieldsEmptyPlan()
        => Assert.Empty(ProbeFloorPlanner.Plan([], Earliest(("600000", new DateTime(2016, 1, 4)))));

    // ── 零请求回填（PlanFromLocalHistory）────────────────────────────────

    private static Dictionary<string, List<(string Code, DateTime NoDataBefore)>> PlanLocal(
        Dictionary<string, DateTime> d, Dictionary<string, DateTime> h, Dictionary<string, DateTime> r,
        out int agreed, out int disagreed, out int dayOnly)
        => ProbeFloorPlanner.PlanFromLocalHistory(d, h, r, "day", "day_hfq", "day_raw",
            out agreed, out disagreed, out dayOnly);

    [Fact]
    public void FillsAllThreeWhenAllThreeAgree()
    {
        var day = new DateTime(2020, 6, 1);
        var plan = PlanLocal(
            Earliest(("300750", day)), Earliest(("300750", day)), Earliest(("300750", day)),
            out int agreed, out int disagreed, out int dayOnly);

        Assert.Equal(1, agreed);
        Assert.Equal(0, disagreed);
        Assert.Equal(0, dayOnly);
        Assert.Equal([("300750", day)], plan["day"]);
        Assert.Equal([("300750", day)], plan["day_hfq"]);
        Assert.Equal([("300750", day)], plan["day_raw"]);
    }

    [Fact]
    public void FillsNothingWhenThreePathsDisagree()
    {
        // day 比另两路更早 → 那两路确实还缺前段，一条都不能填（本机实测有 33 只这样）
        var plan = PlanLocal(
            Earliest(("600000", new DateTime(1996, 5, 1))),
            Earliest(("600000", new DateTime(2016, 1, 4))),
            Earliest(("600000", new DateTime(2016, 1, 4))),
            out int agreed, out int disagreed, out int dayOnly);

        Assert.Equal(0, agreed);
        Assert.Equal(1, disagreed);
        Assert.Equal(0, dayOnly);
        Assert.All(plan.Values, Assert.Empty);
    }

    [Fact]
    public void FillsNothingForDayOnlyInstruments()
    {
        // ETF / 大盘指数 / 板块指数只有前复权一路，没有交叉印证 → 不填，留给真探测
        var plan = PlanLocal(
            Earliest(("sh510300", new DateTime(2016, 1, 5)), ("gn_abc", new DateTime(2016, 1, 5))),
            Earliest(), Earliest(),
            out int agreed, out int disagreed, out int dayOnly);

        Assert.Equal(0, agreed);
        Assert.Equal(0, disagreed);
        Assert.Equal(2, dayOnly);
        Assert.All(plan.Values, Assert.Empty);
    }

    [Fact]
    public void SplitsAMixedUniverseTheRightWay()
    {
        var agree = new DateTime(2020, 6, 1);
        var plan = PlanLocal(
            Earliest(("300750", agree), ("600000", new DateTime(1996, 5, 1)), ("sh510300", new DateTime(2016, 1, 5))),
            Earliest(("300750", agree), ("600000", new DateTime(2016, 1, 4))),
            Earliest(("300750", agree), ("600000", new DateTime(2016, 1, 4))),
            out int agreed, out int disagreed, out int dayOnly);

        Assert.Equal(1, agreed);
        Assert.Equal(1, disagreed);
        Assert.Equal(1, dayOnly);
        Assert.Equal([("300750", agree)], plan["day"]);
        Assert.Single(plan["day_hfq"]);
        Assert.Single(plan["day_raw"]);
    }

    [Fact]
    public void OneDayApartIsStillADisagreement()
    {
        // 判据是"落在同一天"，差一天也不算——差一天多半就是某一路少抓了一根
        var plan = PlanLocal(
            Earliest(("600000", new DateTime(2016, 1, 4))),
            Earliest(("600000", new DateTime(2016, 1, 5))),
            Earliest(("600000", new DateTime(2016, 1, 4))),
            out int agreed, out int disagreed, out _);

        Assert.Equal(0, agreed);
        Assert.Equal(1, disagreed);
        Assert.All(plan.Values, Assert.Empty);
    }

    [Fact]
    public void EmptyUniverseIsANoOp()
    {
        var plan = PlanLocal(Earliest(), Earliest(), Earliest(), out int a, out int d, out int o);
        Assert.Equal(0, a + d + o);
        Assert.All(plan.Values, Assert.Empty);
    }
}
