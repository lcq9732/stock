using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 逐日回补的四道跳过闸（2026-09-08）。
///
/// 为什么值得单独测：这套判据错一处就是**静默漏数据**——日志上只会显示"跳过 N 天"，
/// 跟正常收敛长得一模一样，没人会发现。尤其是"日历不知道 ≠ 不是交易日"那条，
/// 2026-09-06 踩过一次，代价是 2,360 只老股一个请求都没发。
/// </summary>
public class DailyBackfillGateTests
{
    private static readonly HashSet<DateOnly> Empty = new();

    private static TradingCalendar Calendar(params string[] days)
        => new(days.Select(DateTime.Parse));

    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public void 周末不抓()
    {
        Assert.Equal(DailySkipReason.Weekend,
            DailyBackfillGate.Evaluate(D("2026-09-05"), null, Empty, Empty, Empty));   // 周六
    }

    [Fact]
    public void 日历里不是交易日就不抓()
    {
        var cal = Calendar("2026-09-30", "2026-10-09");   // 国庆假期不在日历里
        Assert.Equal(DailySkipReason.NotTradingDay,
            DailyBackfillGate.Evaluate(D("2026-10-01"), cal, Empty, Empty, Empty));
    }

    /// <summary>
    /// **最要紧的一条**：日历覆盖不到的区间，"不在日历里"的含义是日历不知道，不是没有交易日。
    /// 判错就会把整段该抓的历史静默跳过（2026-09-06 那次漏抓 2,360 只老股）。
    /// </summary>
    [Fact]
    public void 日历覆盖不到的日子照抓()
    {
        var cal = Calendar("2016-01-04", "2016-01-05");   // 日历最早只到 2016
        Assert.Equal(DailySkipReason.None,
            DailyBackfillGate.Evaluate(D("2005-06-15"), cal, Empty, Empty, Empty));
    }

    [Fact]
    public void 没有日历时退回只跳周末()
    {
        Assert.Equal(DailySkipReason.None,
            DailyBackfillGate.Evaluate(D("2026-10-01"), null, Empty, Empty, Empty));
    }

    [Fact]
    public void 确认没有数据的日子不抓()
    {
        var confirmed = new HashSet<DateOnly> { D("2003-01-06") };
        Assert.Equal(DailySkipReason.ConfirmedNoData,
            DailyBackfillGate.Evaluate(D("2003-01-06"), null, confirmed, Empty, Empty));
    }

    [Fact]
    public void 本地已有的日子不抓()
    {
        var have = new HashSet<DateOnly> { D("2026-09-04") };
        Assert.Equal(DailySkipReason.AlreadyHave,
            DailyBackfillGate.Evaluate(D("2026-09-04"), null, Empty, have, Empty));
    }

    /// <summary>
    /// 本地已有、但在"无条件重抓"名单里 → 照抓。龙虎榜盘后陆续公布，早抓到的那 20 只
    /// 会让这天变成"已有"，晚上发布的另外 40 只就永远补不上了。
    /// </summary>
    [Fact]
    public void 最近几个交易日已有也要重抓()
    {
        var have = new HashSet<DateOnly> { D("2026-09-04") };
        var force = new HashSet<DateOnly> { D("2026-09-04") };
        Assert.Equal(DailySkipReason.None,
            DailyBackfillGate.Evaluate(D("2026-09-04"), null, Empty, have, force));
    }

    /// <summary>
    /// 重抓名单**不能**盖过前三道闸：周末、非交易日、确认没有的日子，即使落进名单也不该发请求。
    /// （闸的顺序保证了这一点，改顺序会悄悄破坏它。）
    /// </summary>
    [Fact]
    public void 重抓名单盖不过前三道闸()
    {
        var force = new HashSet<DateOnly> { D("2026-09-05"), D("2026-10-01"), D("2003-01-06") };
        var cal = Calendar("2026-09-30", "2026-10-09");
        var confirmed = new HashSet<DateOnly> { D("2003-01-06") };

        Assert.Equal(DailySkipReason.Weekend,
            DailyBackfillGate.Evaluate(D("2026-09-05"), cal, Empty, Empty, force));
        Assert.Equal(DailySkipReason.NotTradingDay,
            DailyBackfillGate.Evaluate(D("2026-10-01"), cal, Empty, Empty, force));
        Assert.Equal(DailySkipReason.ConfirmedNoData,
            DailyBackfillGate.Evaluate(D("2003-01-06"), null, confirmed, Empty, force));
    }

    /// <summary>"最后 N 个交易日"要按交易日数、不是自然日数——跨周末/长假时差别很大。</summary>
    [Fact]
    public void 最后几个交易日按交易日数()
    {
        var cal = Calendar("2026-09-01", "2026-09-02", "2026-09-03", "2026-09-04", "2026-09-07", "2026-09-08");
        var last3 = cal.LastTradingDays(DateTime.Parse("2026-09-08"), 3);
        Assert.Equal([DateTime.Parse("2026-09-04"), DateTime.Parse("2026-09-07"), DateTime.Parse("2026-09-08")], last3);
    }

    /// <summary>asOf 落在非交易日（周日）时，要取它之前的最后几个交易日，不能返回空。</summary>
    [Fact]
    public void 最后几个交易日_起点是休息日也要取到()
    {
        var cal = Calendar("2026-09-03", "2026-09-04", "2026-09-07");
        var last2 = cal.LastTradingDays(DateTime.Parse("2026-09-06"), 2);   // 周日
        Assert.Equal([DateTime.Parse("2026-09-03"), DateTime.Parse("2026-09-04")], last2);
    }
}
