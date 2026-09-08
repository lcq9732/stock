using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 往年回补的缺口计算（【拉取区间数据】用的那套）。
///
/// 这里最要紧的是 <see cref="SkipsOnlyWhenCalendarActuallyCoversTheGap"/> 那条回归用例：
/// 2026-09-06 线上实测，日历（当时取自上证指数的 day 序列）自己只有 2016-01-04 起，
/// 却被拿去断言"1990~2015 没有交易日"，把 2,360 只最该补历史的老股一个请求都不发地跳过了，
/// 日志上还显示成"跳过 2360 只（本地已是最新）"——错得非常安静。
/// 判据一句话：**日历覆盖不到的区间，"查不到交易日"只说明日历不知道，不能当成"没有交易日"。**
/// </summary>
public class YearGapCalculatorTests
{
    private static readonly DateTime YearStart = new(1990, 1, 1);
    private static readonly DateTime YearEnd = new(2016, 12, 31);

    /// <summary>只有 2016 年之后日期的日历——复现线上那份残缺日历。</summary>
    private static TradingCalendar CalendarFrom2016() =>
        new(Enumerable.Range(0, 200).Select(i => new DateTime(2016, 1, 4).AddDays(i)));

    /// <summary>覆盖到 1990 年的完整日历（简化：每年只放几天，够判断有无即可）。</summary>
    private static TradingCalendar FullCalendar()
    {
        var days = new List<DateTime>();
        for (int y = 1990; y <= 2016; y++)
            for (int m = 1; m <= 12; m++)
                days.Add(new DateTime(y, m, 15));          // 每月 15 号当作交易日
        days.Add(new DateTime(2016, 1, 4));                // 2016 年首个交易日
        return new TradingCalendar(days);
    }

    private static bool IsSkip((DateTime Start, DateTime End) w) => w.Start > w.End;

    // ── 回归：日历覆盖不到缺口时，必须放行去抓 ──────────────────────────────

    [Fact]
    public void SkipsOnlyWhenCalendarActuallyCoversTheGap()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2016, 1, 4) };

        // 残缺日历（只有 2016 之后）：缺口是 1990-01-01~2016-01-03，日历一无所知 → 必须抓
        var w = YearGapCalculator.For("600000", earliest, YearStart, YearEnd, CalendarFrom2016());
        Assert.False(IsSkip(w));
        Assert.Equal(YearStart, w.Start);
        Assert.Equal(new DateTime(2016, 1, 3), w.End);
    }

    [Fact]
    public void SkipsWhenFullCalendarShowsNoTradingDayInGap()
    {
        // 日历完整，且缺口 2016-01-01~2016-01-03 里确实没有交易日（元旦假期）→ 跳过才对
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2016, 1, 4) };
        var w = YearGapCalculator.For("600000", earliest, new DateTime(2016, 1, 1), YearEnd, FullCalendar());
        Assert.True(IsSkip(w));
    }

    [Fact]
    public void FetchesWhenFullCalendarHasTradingDayInGap()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2016, 1, 4) };
        var w = YearGapCalculator.For("600000", earliest, YearStart, YearEnd, FullCalendar());
        Assert.False(IsSkip(w));
        Assert.Equal(new DateTime(2016, 1, 3), w.End);
    }

    // ── 其余分支 ────────────────────────────────────────────────────────

    [Fact]
    public void FetchesWholeRangeWhenNoLocalData()
    {
        var w = YearGapCalculator.For("600000", new Dictionary<string, DateTime>(), YearStart, YearEnd, FullCalendar());
        Assert.Equal((YearStart, YearEnd), w);
    }

    [Fact]
    public void SkipsWhenLocalAlreadyStartsBeforeRange()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(1989, 6, 1) };
        Assert.True(IsSkip(YearGapCalculator.For("600000", earliest, YearStart, YearEnd, FullCalendar())));
    }

    [Fact]
    public void FetchesWholeRangeWhenLocalStartsAfterRange()
    {
        // 2018 年才上市：整个 1990~2016 都缺（抓回来多半是空，但那是数据源的事，判据上就该发请求）
        var earliest = new Dictionary<string, DateTime> { ["300750"] = new(2018, 6, 11) };
        Assert.Equal((YearStart, YearEnd),
            YearGapCalculator.For("300750", earliest, YearStart, YearEnd, FullCalendar()));
    }

    [Fact]
    public void WithoutCalendarAlwaysFetchesTheGap()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2016, 1, 4) };
        var w = YearGapCalculator.For("600000", earliest, new DateTime(2016, 1, 1), YearEnd, calendar: null);
        Assert.False(IsSkip(w));
    }

    // ── 已探明水位（BarProbeFloor / ProbeFloorPlanner）─────────────────────

    [Fact]
    public void SkipsWhenProbeFloorCoversWholeRange()
    {
        // 2020 年上市的票，上一轮已探明"2017-01-01 之前数据源没有" → 整个 1990~2016 都不用再试。
        // 这一条就是 2026-09-07 那次空跑要省掉的主体：5558 只里绝大多数属于这种。
        var earliest = new Dictionary<string, DateTime> { ["300750"] = new(2020, 6, 1) };
        var floors = new Dictionary<string, DateTime> { ["300750"] = new(2017, 1, 1) };

        Assert.True(IsSkip(YearGapCalculator.For("300750", earliest, YearStart, YearEnd, FullCalendar(), floors)));
    }

    [Fact]
    public void SkipsWhenProbeFloorCoversTheGap()
    {
        // 1991-01-02 上市的老票：缺口只有开市首日到年末那几天，上一轮已探明那段没有
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(1991, 1, 2) };
        var floors = new Dictionary<string, DateTime> { ["600000"] = new(1991, 1, 2) };

        Assert.True(IsSkip(YearGapCalculator.For(
            "600000", earliest, new DateTime(1990, 12, 19), YearEnd, FullCalendar(), floors)));
    }

    [Fact]
    public void FetchesOnlyThePartAboveTheProbeFloor()
    {
        // 水位只盖住缺口的前半段 → 剩下那段仍要抓，起点抬到水位
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2010, 6, 1) };
        var floors = new Dictionary<string, DateTime> { ["600000"] = new(2000, 1, 1) };

        var w = YearGapCalculator.For("600000", earliest, YearStart, YearEnd, FullCalendar(), floors);
        Assert.False(IsSkip(w));
        Assert.Equal(new DateTime(2000, 1, 1), w.Start);
        Assert.Equal(new DateTime(2010, 5, 31), w.End);
    }

    [Fact]
    public void ProbeFloorEarlierThanRangeStartChangesNothing()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2010, 6, 1) };
        var floors = new Dictionary<string, DateTime> { ["600000"] = new(1985, 1, 1) };

        var w = YearGapCalculator.For("600000", earliest, YearStart, YearEnd, FullCalendar(), floors);
        Assert.Equal(YearStart, w.Start);
        Assert.Equal(new DateTime(2010, 5, 31), w.End);
    }

    [Fact]
    public void ProbeFloorAppliesEvenWithNoLocalBars()
    {
        // 本地一根都没有、但上一轮探明过水位（比如更早那次是从别的粒度补的）→ 起点照样抬
        var floors = new Dictionary<string, DateTime> { ["300750"] = new(2010, 1, 1) };
        var w = YearGapCalculator.For("300750", new Dictionary<string, DateTime>(),
            YearStart, YearEnd, FullCalendar(), floors);

        Assert.Equal(new DateTime(2010, 1, 1), w.Start);
        Assert.Equal(YearEnd, w.End);
    }

    [Fact]
    public void OtherCodesAreUnaffectedByAnotherCodesFloor()
    {
        var earliest = new Dictionary<string, DateTime> { ["600000"] = new(2010, 6, 1) };
        var floors = new Dictionary<string, DateTime> { ["300750"] = new(2017, 1, 1) };

        var w = YearGapCalculator.For("600000", earliest, YearStart, YearEnd, FullCalendar(), floors);
        Assert.Equal(YearStart, w.Start);
        Assert.Equal(new DateTime(2010, 5, 31), w.End);
    }

    // ── TradingCalendar 自身 ────────────────────────────────────────────

    [Fact]
    public void CalendarCoversFromAndHasAnyIn()
    {
        var cal = new TradingCalendar([new DateTime(2016, 1, 4), new DateTime(2016, 1, 5), new DateTime(2016, 1, 8)]);
        Assert.Equal(new DateTime(2016, 1, 4), cal.Earliest);
        Assert.Equal(new DateTime(2016, 1, 8), cal.Latest);

        Assert.False(cal.CoversFrom(new DateTime(2015, 12, 31)));   // 早于日历起点 → 覆盖不到
        Assert.True(cal.CoversFrom(new DateTime(2016, 1, 4)));
        Assert.True(cal.CoversFrom(new DateTime(2016, 6, 1)));

        Assert.True(cal.HasAnyIn(new DateTime(2016, 1, 1), new DateTime(2016, 1, 4)));   // 含边界
        Assert.True(cal.HasAnyIn(new DateTime(2016, 1, 6), new DateTime(2016, 1, 9)));   // 区间内落一个
        Assert.False(cal.HasAnyIn(new DateTime(2016, 1, 6), new DateTime(2016, 1, 7)));  // 正好跳过
        Assert.False(cal.HasAnyIn(new DateTime(2016, 1, 9), new DateTime(2016, 1, 1)));  // start>end
    }

    [Fact]
    public void EmptyCalendarCoversNothing()
    {
        var cal = new TradingCalendar([]);
        Assert.Equal(0, cal.Count);
        Assert.Null(cal.Earliest);
        Assert.False(cal.CoversFrom(new DateTime(2016, 1, 4)));
        Assert.False(cal.HasAnyIn(new DateTime(1990, 1, 1), new DateTime(2026, 1, 1)));
    }
}
