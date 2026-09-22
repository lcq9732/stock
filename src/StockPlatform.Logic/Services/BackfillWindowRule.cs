using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 把一个任务自己算出来的「整段」窗口，**收窄**到调用方指定的年份区间（2026-09-22）。
///
/// ════ 为什么是"收窄"而不是"设定" ════
/// <see cref="FetchMode"/> 的 <c>FirstBackfill</c> 语义是"不看水位线、只补缺的"，
/// 而「整段」有多长**由各任务自己的数据源决定**：K线从 A股开市首日、
/// 分档资金流的接口只给最近 120 个交易日、不复权那一路从前复权的最早那天起。
///
/// 【拉取区间数据】说"补 2016~2022"，意思是把那个"整段"**收小**到这几年，
/// 而不是"不管数据源有没有、一律从 2016 抓到 2022"。所以这里只做交集、永不放宽：
/// 要是有人填了 1990~2030，分档资金流仍然只补它那 120 天，不会白发二十年的空请求。
///
/// 这也是为什么没有为"区间"新开一个 FetchMode——它是同一种行为的窗口更小，
/// 不是另一种行为。模式会两两组合，多一个就多一片要想清楚的格子。
///
/// ⚠ 结果可能是**空窗口**（Start &gt; End），那是正常的"这只标的这几年没什么可补"，
/// 调用方照常按 start &gt; end 跳过即可，不要当成错误。
/// </summary>
public static class BackfillWindowRule
{
    /// <summary>
    /// 交集。<paramref name="yearStart"/>/<paramref name="yearEnd"/> 为 null 表示那一头不收窄。
    /// </summary>
    /// <param name="start">任务自己算出的整段起点。</param>
    /// <param name="end">任务自己算出的整段终点。</param>
    /// <param name="today">今天——结束年是今年（或更晚）时收到今天为止，见下。</param>
    public static (DateTime Start, DateTime End) Narrow(
        DateTime start, DateTime end, int? yearStart, int? yearEnd, DateTime today)
    {
        if (yearStart is { } ys)
        {
            var from = new DateTime(ys, 1, 1);
            if (from > start) start = from;
        }

        if (yearEnd is { } ye)
        {
            // 结束年是今年或更晚 → 收到**今天**，不是 12-31：之后的日期还没发生，
            // 请求它们只会拿回空数据，而"成功返回空"会被探测水位记成
            // "数据源没有更早数据"，白白污染那张表。
            var to = ye >= today.Year ? today.Date : new DateTime(ye, 12, 31);
            if (to < end) end = to;
        }

        return (start, end);
    }
}
