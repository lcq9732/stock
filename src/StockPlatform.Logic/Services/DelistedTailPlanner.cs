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
/// ③ **本地已有历史，且最后一根早于终止日**——本地一根都没有的走下面「整段缺失」那一类。
///
/// 正常情况下结果是 0 只，偶尔 1~2 只新退市的。
///
/// ════ 另一类：整段缺失（<see cref="SelectWholeHistory"/>，2026-10-16 加）════
/// 本地**一根前复权都没有**的退市股。原来说"那是【拉取区间数据】的活"，可整段回补是手动跑的，
/// 而【补全退市名单】从巨潮/档案补进来的票（没有终止日）入名单之后就再没人管：
/// 2026-10-16 实测 19 只一根K线都没有（688287 观典防务等），腾讯其实有它们的完整历史。
/// 这一类**不要求终止日**（没有就补到今天——数据源只给到它最后交易的那天），
/// 照样只认没尝试过的，每只只抓一次。
/// </summary>
public static class DelistedTailPlanner
{
    /// <summary>要补的一只退市股。</summary>
    /// <param name="Code">代码。</param>
    /// <param name="Name">终止上市时的简称。</param>
    /// <param name="End">补到这天为止：终止日；整段缺失又没有终止日的是今天。</param>
    /// <param name="DayStart">前复权从哪天起：补尾巴是本地最后一根的次日，整段缺失是 A股开市首日。</param>
    /// <param name="WholeHistory">整段缺失（本地一根前复权都没有）——三个口径都从头抓。</param>
    public sealed record Target(string Code, string Name, DateTime End, DateTime DayStart, bool WholeHistory = false);

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
    /// 挑出整段缺失的票：没尝试过、本地一根前复权都没有。
    ///
    /// 起点固定是 A股开市首日，不用上市日：腾讯从截止日往前翻页、翻到上市那天自己停，
    /// 起点早不多发请求；而上市日有缺的（退市股原来全空），拿它当起点反而可能漏掉开头。
    /// </summary>
    /// <param name="all">本地退市名单（含【补全退市名单】补进来、没有终止日的那些）。</param>
    /// <param name="tailPending">还没尝试过的代码集合。</param>
    /// <param name="latestDay">每只票本地前复权最后一根的日期——在这里面的就不是整段缺失。</param>
    /// <param name="today">没有终止日的补到这天。</param>
    public static List<Target> SelectWholeHistory(
        IEnumerable<DelistedStockRow> all,
        IReadOnlySet<string> tailPending,
        IReadOnlyDictionary<string, DateTime> latestDay,
        DateTime today)
    {
        var list = new List<Target>();
        foreach (var r in all)
        {
            if (!tailPending.Contains(r.Code)) continue;
            if (latestDay.ContainsKey(r.Code)) continue;
            list.Add(new Target(r.Code, r.Name, (r.DelistDate ?? today).Date,
                                IncrementalWindowCalculator.AShareMarketOpen, WholeHistory: true));
        }
        return list;
    }

    /// <summary>本地最后一根K线距今不超过这么久，就算"还在交易"。跟【补全退市名单】的门槛一致。</summary>
    public static readonly TimeSpan StillTradingWithin = TimeSpan.FromDays(30);

    /// <summary>
    /// 名单说退市、本地行情却说还在交易——**不能标退市**（2026-09-28 加）。
    ///
    /// 标错的代价是这只票被踢出日常轮询、K线从此停更，而且没有任何地方会报：
    /// <c>600801 华新建材</c>就是这样被上交所名单误标（只有 B股终止），日K停在 09-16，
    /// 靠资金流对账才发现。【补全退市名单】早有这道检查，两所官网这条路径以前没有。
    ///
    /// 只拦**没有终止日**的行：有明确终止日的是官方给的事实，刚退市的票最后一根K线本来就在
    /// 30 天内，拦了反而让它多进 30 天日常轮询。
    /// </summary>
    public static bool LooksStillTrading(DelistedStockRow row, DateTime? latestBar, DateTime today)
        => row.DelistDate is null
           && latestBar is { } l
           && today.Date - l.Date <= StillTradingWithin;

    /// <summary>
    /// 后复权/不复权那两条线各补哪一段。它们的水位线**跟前复权各自独立**，所以要分开算：
    /// 有历史就从它的次日续；**完全没有的**就从终止日往前回看几年，一次把这只退市股的
    /// 该口径历史抓够——回测吃的正是这两条线，缺了这几天，它的回测序列就断在这儿。
    /// </summary>
    public static (DateTime Start, DateTime End) WindowFor(
        DateTime delistDate, DateTime? latestOfGranularity, int lookbackYears)
        => (latestOfGranularity is { } l ? l.AddDays(1) : delistDate.AddYears(-lookbackYears), delistDate);

    /// <summary>
    /// 同上，按一只要补的票算。整段缺失的票该口径没有历史时从 A股开市首日起——
    /// 它前复权都是从头抓的，后复权/不复权只回看几年，回测序列就只剩最后几年。
    /// </summary>
    public static (DateTime Start, DateTime End) WindowFor(
        Target t, DateTime? latestOfGranularity, int lookbackYears)
        => t.WholeHistory && latestOfGranularity is null
            ? (IncrementalWindowCalculator.AShareMarketOpen, t.End)
            : WindowFor(t.End, latestOfGranularity, lookbackYears);
}
