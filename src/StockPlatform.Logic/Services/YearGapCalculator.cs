namespace StockPlatform.Logic.Services;

/// <summary>
/// 往年回补时算"这只标的在目标区间里还缺哪一段"。纯计算、无 IO——2026-09-06 从
/// <c>FetchOrchestrator.YearGapFor</c> 抽出来，抽的目的就是让它能被单测覆盖：
/// 原来它是 private static，那个日历误判的 bug 藏了多久都没人测得到。
/// </summary>
public static class YearGapCalculator
{
    /// <summary>
    /// 返回这只标的要抓的区间。<b><c>Start &gt; End</c> 表示不用抓</b>（调用方按空区间跳过、不发请求）。
    ///
    /// 四种情形：
    /// <list type="bullet">
    /// <item>本地完全没有这只 → 整个目标区间都要抓；</item>
    /// <item>本地最早日已经早于（或等于）区间起点 → 这段本来就齐了，跳过；</item>
    /// <item>本地最早日落在区间内 → 只补它前面那段缺口 <c>[yearStart, 最早日-1]</c>；</item>
    /// <item>本地最早日晚于区间末尾 → 整个区间都缺，全抓。</item>
    /// </list>
    /// 以上四种都先被 <paramref name="noDataBefore"/> 抬过起点：上一轮已经探明"这天之前数据源没有"
    /// 的那段不再重复请求，水位盖住整个缺口时直接跳过。
    /// </summary>
    /// <param name="calendar">
    /// 本地已知的交易日历，可为 null（null 时不做"缺口里有没有交易日"的优化判断，一律放行去抓）。
    /// </param>
    /// <param name="noDataBefore">
    /// 已探明的水位（<c>BarProbeFloor</c> 表，见 <see cref="ProbeFloorPlanner"/>）：这只票在这一天
    /// 之前，数据源**确认**没有数据（上一轮真发过请求、成功、返回 0 行）。起点抬到它，抬过缺口末尾
    /// 就整只跳过。这是 2026-09-07 加的——在此之前，"那些年还没上市"的票每轮都要重新试一遍，
    /// 一次区间回补光在这上面就烧掉一万六千个请求、四个半小时，写入为零。
    /// </param>
    public static (DateTime Start, DateTime End) For(
        string code, IReadOnlyDictionary<string, DateTime> earliestByCode,
        DateTime yearStart, DateTime yearEnd, TradingCalendar? calendar = null,
        IReadOnlyDictionary<string, DateTime>? noDataBefore = null)
    {
        // 已探明"这天之前没有" → 起点抬上来。抬过整个区间就没什么可抓的了。
        var start = yearStart;
        if (noDataBefore != null && noDataBefore.TryGetValue(code, out var floor) && floor.Date > start.Date)
            start = floor.Date;
        if (start > yearEnd.Date) return (yearEnd.AddDays(1), yearEnd);             // 空区间=跳过

        if (!earliestByCode.TryGetValue(code, out var earliest)) return (start, yearEnd);
        if (earliest.Date <= start.Date) return (yearEnd.AddDays(1), yearEnd);      // 空区间=跳过
        if (earliest.Date <= yearEnd.Date)
        {
            var gapEnd = earliest.AddDays(-1);
            if (start > gapEnd.Date) return (yearEnd.AddDays(1), yearEnd);          // 水位已盖住整个缺口

            // 缺口里一个交易日都没有 → 再请求也只会拿回空数据，直接跳过。典型情形：区间起点写的是
            // 2016-01-01（自然年首日），而 A 股 2016 年第一个交易日是 01-04，中间只有元旦假期——
            // 不判这一下的话，几千只"其实已经补齐"的股票每只都会白发一次请求（2026-07-30 实测：
            // 一次区间重跑光在这上面就烧掉 19 分钟、1150 个请求，还没轮到后面的阶段）。
            //
            // ⚠ 但这个跳过**只在日历覆盖得到缺口起点时才算数**（CoversFrom）。日历是从已有K线归纳的，
            // 覆盖不到的区间它一律"查不到"，那只说明日历不知道、不代表那段真没开过市。少了这个前提，
            // 就是 2026-09-06 那个 bug：日历（上证指数 day）只有 2016 年之后，却拿它断言 1990~2015
            // 没有交易日，把 2,360 只最该补历史的老股全部静默跳过。见 <see cref="TradingCalendar"/>。
            if (calendar != null && calendar.CoversFrom(start) && !calendar.HasAnyIn(start, gapEnd))
                return (yearEnd.AddDays(1), yearEnd);

            return (start, gapEnd); // 只补前面的缺口
        }
        return (start, yearEnd);
    }
}
