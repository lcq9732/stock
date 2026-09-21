using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 「哪些退市股缺最后几天的K线、各补哪一段」——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchOrchestrator.CatchUpDelistedTailsAsync</c> 抽出来。
///
/// ════ 它补的是一个真实存在的数据黑洞 ════
/// 一只股票退市后，日常抓取的本地清单里它还在（type 还是 stock），但数据源已经不再给新数据；
/// 而它**最后几个交易日**（退市整理期，往往正是跌得最惨、对回测最关键的那一段）
/// 如果当时没抓到，之后就永久缺失了。
///
/// ════ 三条筛选条件，缺一不可 ════
/// ① **有终止日**——上交所部分行（转板/吸收合并）没有这个字段，没有就算不出要补到哪天；
/// ② **没尝试过**（<c>tail_fetched_at IS NULL</c>）——这个标记不可少：停牌后才退市的股票
///    K线止于停牌日、**永远**早于终止日，没有标记就会每天徒劳重抓；
/// ③ **本地已有历史，且最后一根早于终止日**——本地一根都没有的不在这里补
///    （那是【拉取区间数据】的活，退市股历史补过就不会再变，每天重扫毫无意义）。
///
/// 正常情况下结果是 0 只，偶尔 1~2 只新退市的。
/// </summary>
public static class DelistedTailPlanner
{
    /// <summary>要补尾巴的一只退市股。</summary>
    /// <param name="Code">代码。</param>
    /// <param name="Name">终止上市时的简称。</param>
    /// <param name="DelistDate">终止上市日——补到这天为止。</param>
    /// <param name="DayStart">前复权从哪天续（本地最后一根的次日）。</param>
    public sealed record Target(string Code, string Name, DateTime DelistDate, DateTime DayStart);

    /// <summary>挑出要补尾巴的票（三条条件见类注释）。</summary>
    /// <param name="all">数据源给的全部终止上市公司。</param>
    /// <param name="tailPending">还没尝试过补尾巴的代码集合。</param>
    /// <param name="latestDay">每只票本地**前复权**最后一根的日期。</param>
    public static List<Target> SelectPending(
        IEnumerable<DelistedStockRow> all,
        IReadOnlySet<string> tailPending,
        IReadOnlyDictionary<string, DateTime> latestDay)
    {
        var list = new List<Target>();
        foreach (var r in all)
        {
            if (r.DelistDate is not { } delist) continue;
            if (!tailPending.Contains(r.Code)) continue;
            if (!latestDay.TryGetValue(r.Code, out var latest)) continue;
            if (latest.Date >= delist.Date) continue;
            list.Add(new Target(r.Code, r.Name, delist, latest.AddDays(1)));
        }
        return list;
    }

    /// <summary>
    /// 后复权/不复权那两条线各补哪一段。它们的水位线**跟前复权各自独立**，所以要分开算：
    /// 有历史就从它的次日续；**完全没有的**就从终止日往前回看几年，一次把这只退市股的
    /// 该口径历史抓够——回测吃的正是这两条线，缺了这几天，它的回测序列就断在这儿。
    /// </summary>
    public static (DateTime Start, DateTime End) WindowFor(
        DateTime delistDate, DateTime? latestOfGranularity, int lookbackYears)
        => (latestOfGranularity is { } l ? l.AddDays(1) : delistDate.AddYears(-lookbackYears), delistDate);
}
