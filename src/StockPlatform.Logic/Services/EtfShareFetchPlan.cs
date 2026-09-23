using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>一次份额请求：[From, To] 这段，以及这段里本轮要补的交易日。</summary>
public sealed record EtfShareRequest(DateOnly From, DateOnly To, IReadOnlyList<DateOnly> Days);

/// <summary>
/// ETF 份额这一轮要发哪些请求（2026-09-23）。纯计算、无 IO。
///
/// 要补的日子＝交易日历里、源开始有数据以后、到今天为止，并且满足其一：
///   · 本地还没有这天的份额；
///   · 这天在最近 <see cref="RefreshRecentDays"/> 个自然日内——**有了也重抓**。深交所页面注明
///     「T 日晚间更新的 T 日规模仅供参考，以 T+1 日早间更新的为准」，而校正拿 T 日份额去算
///     T+1 日的换手率，晚间那个参考值不能留成定稿。上交所没有这句说明，照同一条规矩多问几天也不贵。
/// 已被确认"交易所那天就是没有"的日子不再问。
///
/// 按天的源一天一个请求；按月的源把同一个月的日子合并成一个请求（深交所 xlsx 半年以内不截断，
/// 整年会返回"没有找到"，按月最稳）。
/// </summary>
public static class EtfShareFetchPlan
{
    /// <summary>最近几个自然日的份额每轮都重抓（理由见类注释）。</summary>
    public const int RefreshRecentDays = 3;

    public static List<EtfShareRequest> Build(
        IEnumerable<DateOnly> calendar, IReadOnlySet<DateOnly> have, IReadOnlySet<DateOnly> confirmedEmpty,
        DateOnly firstDay, DateOnly today, EtfShareBatch batch)
    {
        var refreshFrom = today.AddDays(-RefreshRecentDays);
        var days = calendar
            .Where(d => d >= firstDay && d <= today && !confirmedEmpty.Contains(d)
                        && (!have.Contains(d) || d > refreshFrom))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        return batch == EtfShareBatch.Day
            ? days.Select(d => new EtfShareRequest(d, d, [d])).ToList()
            : days.GroupBy(d => (d.Year, d.Month))
                  .Select(g => new EtfShareRequest(g.First(), g.Last(), g.ToList()))
                  .ToList();
    }

    /// <summary>
    /// 一次请求回来之后，哪些日子可以记成"交易所确实没有"：请求覆盖了、一行都没返回、
    /// 而且已经过了 <see cref="RefreshRecentDays"/> 天（近几天的空可能只是还没发布）。
    /// </summary>
    public static List<DateOnly> ConfirmableEmpty(
        EtfShareRequest request, IReadOnlyCollection<EtfShareRow> rows, DateOnly today)
    {
        var got = rows.Select(r => r.TradeDate).ToHashSet();
        var refreshFrom = today.AddDays(-RefreshRecentDays);
        return request.Days.Where(d => !got.Contains(d) && d < refreshFrom).ToList();
    }
}
