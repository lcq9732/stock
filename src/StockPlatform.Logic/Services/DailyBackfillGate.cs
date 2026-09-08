namespace StockPlatform.Logic.Services;

/// <summary>某一天为什么不用发请求。<see cref="None"/>＝该抓。</summary>
public enum DailySkipReason
{
    /// <summary>该抓。</summary>
    None,
    /// <summary>周六周日。</summary>
    Weekend,
    /// <summary>交易日历里不是交易日（节假日）。</summary>
    NotTradingDay,
    /// <summary>抓过、确认这个源这天就是没有数据。</summary>
    ConfirmedNoData,
    /// <summary>本地已经有这天的数据了。</summary>
    AlreadyHave,
}

/// <summary>
/// 逐日回补时「这一天要不要发请求」的判据（2026-09-08）。
///
/// 单独成类是为了**能测**：这套判据错一处就是静默漏数据——把"日历不知道"当成"不是交易日"会
/// 跳过整段该抓的日子（2026-09-06 那次漏了 2,360 只老股就是这么来的），而日志上只会显示
/// "跳过 N 天"，跟正常收敛长得一模一样。它埋在 FetchOrchestrator 的私有方法里就一行都测不到。
///
/// 四道闸的顺序是有讲究的：先周末（最便宜）、再日历、再"确认没有"、最后才是"本地已有"——
/// 因为最后一道有例外（<paramref name="forceRefetch"/>），放前面会让例外覆盖不到前三道。
/// </summary>
public static class DailyBackfillGate
{
    /// <param name="day">要判的那天。</param>
    /// <param name="calendar">交易日历；null＝没有日历，退回"只跳周末"的老行为。</param>
    /// <param name="confirmedNoData">已确认这个源没有数据的日子。</param>
    /// <param name="have">本地已经有数据的日子。</param>
    /// <param name="forceRefetch">
    /// 本地已有也要重抓的日子（通常是最近几个交易日）。数据源盘后**陆续**发布，早抓到的可能只是
    /// 一半，"有行就跳过"会把残缺状态永久固化。重抓幂等，代价就是每轮多几个请求。
    /// </param>
    public static DailySkipReason Evaluate(
        DateOnly day,
        TradingCalendar? calendar,
        IReadOnlySet<DateOnly> confirmedNoData,
        IReadOnlySet<DateOnly> have,
        IReadOnlySet<DateOnly> forceRefetch)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return DailySkipReason.Weekend;

        // ⚠ 必须先问 CoversFrom：日历覆盖不到的区间，"不在日历里"的含义是**日历不知道**，
        //    不是"不是交易日"。混为一谈就会静默跳过整段该抓的日子。
        var asDate = day.ToDateTime(TimeOnly.MinValue);
        if (calendar != null && calendar.CoversFrom(asDate) && !calendar.Contains(asDate))
            return DailySkipReason.NotTradingDay;

        if (confirmedNoData.Contains(day)) return DailySkipReason.ConfirmedNoData;
        if (have.Contains(day) && !forceRefetch.Contains(day)) return DailySkipReason.AlreadyHave;
        return DailySkipReason.None;
    }
}
