using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 一只证券哪些交易日**全天停牌**（当天不该有日K）——纯函数（2026-09-30，见 doc/suspension-design.md）。
///
/// ════ 判据（2026-09-30 拿官网记录跟本地 day_raw 逐日比对定的）════
/// 上交所（股票、基金同一套）：
///   · LXTP 连续停牌，或 stopTime=WH（临时停牌·全天）：[开始日, 结束日] **两头都含**。
///     2010~2025 每年 1/7 月抽样，判出的 8.7 万天**没有一天有日K**；
///   · 其余 stopTime（915、13、AM、PM……）是盘中停一段，不算（当天多半有日K，个别没有的只是照旧进待办）。
/// 深交所（2007~2026 全量 28 万天，对不上的 2 天）：
///   · 全天停的日子 ＝ [生效起点, 复牌日)。停牌时刻是"开市"（或 09:30 以前）当天就算，
///     盘中某个时刻才停的当天有交易、从下一个交易日起算；**复牌日当天有交易**。
///     「1小时」「半天」「盘中临时停牌」这样自然算出 0 天，不用按期限文字特判。
///   · 「取消停牌/今起复牌/复牌/取消特停/今起恢复交易」是**复牌记录**，日期可能写在停牌时间那一列、
///     也可能写在复牌时间那一列（老数据两种都有），它们本身不算停牌日，只给下面的配对当终点。
///   · 没写复牌时刻的停牌（长期停牌、特停），终点取三者最早：之后第一条复牌记录、之后第一条别的停牌事件
///     （2008-03-12 特停、次日来一条「1小时」＝特停只停了一天）、**本地停牌后第一根日K**
///     （官网有开了头没收尾的记录：000656 在 2017-07-19 记了停牌，下一条复牌记录在 2023 年，
///     中间六年天天有成交）。三者都没有＝还在停，算到 <c>asOf</c>。
///   · 同一天可能叠着两条（2025-01-23 盘中停到次日开市、01-24 又有一条「1天」），**按天取并集**。
///
/// ⚠ 用途只有一个：**解释"为什么这天没有日K"**（体检不再把停牌日记成待办）。它从来不用来删K线、
///   也不用来判"这天必须没有"——官网漏记一条的后果只是那天照旧进待办，不会伤数据。
///   正因为只用来解释缺口，才敢拿"停牌后第一根日K"当终点：那之后的日子本来就不归这条记录解释。
/// </summary>
public static class SuspensionRule
{
    /// <summary>深交所盘中时刻早于这个就当作开市起停（集合竞价前停的，当天一笔都成交不了）。</summary>
    private static readonly TimeOnly MarketOpen = new(9, 30);

    /// <param name="rows">**同一只证券**的全部记录（深交所要跨月配对，只给一段会配不上）。</param>
    /// <param name="calendar">交易日历，升序。</param>
    /// <param name="asOf">还在停牌的，算到这天为止（含）。</param>
    /// <param name="barDays">这只证券本地有日K的日子，升序；null＝不拿日K截断（只在没有K线可查时用）。</param>
    public static HashSet<DateOnly> FullDays(
        IEnumerable<SuspensionRow> rows, IReadOnlyList<DateOnly> calendar, DateOnly asOf,
        IReadOnlyList<DateOnly>? barDays = null)
    {
        var list = rows as IReadOnlyCollection<SuspensionRow> ?? rows.ToList();
        var days = new HashSet<DateOnly>();

        var szse = list.Where(r => r.Source == SuspensionSource.Szse).ToList();
        var terminators = szse.Where(IsResume)
            .Select(r => r.EndDay ?? r.StartDay).OfType<DateOnly>()
            .OrderBy(d => d).ToList();
        var szseStarts = szse.Where(r => !IsResume(r) && r.StartDay is not null)
            .Select(r => r.StartDay!.Value).OrderBy(d => d).ToList();

        foreach (var r in list)
        {
            if (r.StartDay is not { } start) continue;

            if (r.Source == SuspensionSource.Szse)
            {
                if (IsResume(r)) continue;
                var from = AtOrBeforeOpen(r.StartTime) ? start : start.AddDays(1);
                DateOnly resume;               // 复牌日（不含）
                if (r.EndDay is { } end) resume = end;
                else
                {
                    DateOnly? best = null;
                    Min(ref best, FirstAtOrAfter(terminators, start));
                    Min(ref best, FirstAfter(szseStarts, start));
                    if (barDays != null) Min(ref best, FirstAtOrAfter(barDays, from));
                    resume = best ?? asOf.AddDays(1);
                }
                AddRange(days, calendar, from, resume.AddDays(-1));
            }
            else if (r.Kind == "LXTP" || r.StopTime == "WH")
            {
                var last = r.EndDay ?? (barDays != null && FirstAtOrAfter(barDays, start) is { } b
                    ? b.AddDays(-1) : asOf);
                AddRange(days, calendar, start, last);
            }
        }
        return days;
    }

    /// <summary>深交所的复牌记录（「取消停牌」「今起复牌」「复牌」「取消特停」「今起恢复交易」）。</summary>
    public static bool IsResume(SuspensionRow r) =>
        r.Source == SuspensionSource.Szse
        && (r.Kind.StartsWith("取消", StringComparison.Ordinal)
            || r.Kind.Contains("复牌", StringComparison.Ordinal)
            || r.Kind.Contains("恢复", StringComparison.Ordinal));

    private static bool AtOrBeforeOpen(string time) =>
        time == SuspensionRow.AtOpen
        || (TimeOnly.TryParse(time, out var t) && t < MarketOpen);

    private static void Min(ref DateOnly? best, DateOnly? candidate)
    {
        if (candidate is { } c && (best is null || c < best)) best = c;
    }

    private static DateOnly? FirstAtOrAfter(IReadOnlyList<DateOnly> sorted, DateOnly d)
    {
        int i = LowerBound(sorted, d);
        return i < sorted.Count ? sorted[i] : null;
    }

    private static DateOnly? FirstAfter(IReadOnlyList<DateOnly> sorted, DateOnly d)
    {
        int i = LowerBound(sorted, d.AddDays(1));
        return i < sorted.Count ? sorted[i] : null;
    }

    private static int LowerBound(IReadOnlyList<DateOnly> sorted, DateOnly d)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < d) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>把 [from, to] 里的交易日加进去（两头都含）。</summary>
    private static void AddRange(HashSet<DateOnly> days, IReadOnlyList<DateOnly> calendar, DateOnly from, DateOnly to)
    {
        if (to < from) return;
        for (int i = LowerBound(calendar, from); i < calendar.Count && calendar[i] <= to; i++) days.Add(calendar[i]);
    }
}

/// <summary>
/// 停复牌这一轮要问哪些月份（2026-09-30）。纯计算。
///
/// 没问过的月份都问；**最近 <see cref="RefreshRecentMonths"/> 个月每轮都重问**——
/// 还在停牌的记录复牌后才补上结束日（上交所）、复牌记录记在复牌那个月（深交所），
/// 只问一次就定案的话，这些票会一直被当成"还在停牌"。
/// 全量模式（首次整段回补）把从 <c>firstMonth</c> 起的每个月都重问一遍。
/// </summary>
public static class SuspensionFetchPlan
{
    /// <summary>当月 + 上个月每轮都重问（理由见类注释）。</summary>
    public const int RefreshRecentMonths = 2;

    public static List<DateOnly> Build(DateOnly firstMonth, IReadOnlySet<DateOnly> fetched, DateOnly today, bool all)
    {
        var first = new DateOnly(firstMonth.Year, firstMonth.Month, 1);
        var last = new DateOnly(today.Year, today.Month, 1);
        var refreshFrom = last.AddMonths(-(RefreshRecentMonths - 1));
        var months = new List<DateOnly>();
        for (var m = first; m <= last; m = m.AddMonths(1))
            if (all || !fetched.Contains(m) || m >= refreshFrom)
                months.Add(m);
        return months;
    }
}
