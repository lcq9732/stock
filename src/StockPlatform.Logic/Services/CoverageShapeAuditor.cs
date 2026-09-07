namespace StockPlatform.Logic.Services;

/// <summary>
/// **覆盖形状体检**（2026-09-06 新增，全库数据体检的第四块）——查的不是"中间缺了哪几天"，
/// 而是"这条线整体的形状对不对"：起点是不是比该有的晚了一大截、尾巴是不是停在几天前。
///
/// ════ 为什么 FindGaps 查不出这两种形状 ════
/// SqliteMissingBarRepository.FindGaps 只在每只票**自己**的 [最早, 最晚] 区间内找洞。
/// 于是两类失效天生在它的视野之外：
///   ① **起点晚**：某个口径只抓到最近两年、别的口径有十年——区间内一个洞都没有，干干净净；
///      2026-09-06 实测有 37 只次新股的 day_raw 比 day 少约 20 根，体检一个字没报。
///   ② **尾巴停**：最近几天整体没抓到，`最晚` 会跟着往前退，缺口同样落在区间外。
/// 两者都是"看起来很干净"的假阴性，比报错难发现得多。
///
/// ════ 判据为什么要用交易日数、不能用自然日 ════
/// 春节前后能差出十天自然日、一个交易日都没有。全部换算成交易日历上的下标差。
///
/// 纯函数、不碰数据库，方便直接拿真实的日期序列验证。
/// </summary>
public static class CoverageShapeAuditor
{
    /// <summary>起点比基准晚这么多个交易日才算问题。次新股实测差 20 根，正常抖动 1~2 根。</summary>
    public const int DefaultLateStartThreshold = 5;

    /// <summary>尾巴落后这么多个交易日才算问题。日历本身已经按 cutoff 截断（让开最近两天），
    /// 所以这里给 0 就够——传进来的日历最后一天就是"该有的最新那天"。</summary>
    public const int DefaultLateTailThreshold = 0;

    /// <summary>基准口径的历史短于这么多个交易日的标的一律跳过：新上市的票三个口径都只有
    /// 一两根K线（2026-09-04 上市的 920289 就是），拿它比什么都是噪声。</summary>
    public const int MinBaseTradingDays = 5;

    /// <param name="Code">标的代码。</param>
    /// <param name="TradingDays">差了几个交易日。</param>
    /// <param name="Expected">基准口径的那一天（起点比较用最早日，尾巴比较用日历最新日）。</param>
    /// <param name="Actual">被查口径的那一天。</param>
    public sealed record ShapeGap(string Code, int TradingDays, DateTime Expected, DateTime Actual);

    /// <summary>
    /// 找出"起点比基准晚太多"的标的。基准是前复权（day）——它是界面和绝大多数分析用的口径，
    /// 也是抓得最全的一套。
    ///
    /// 被查口径**一根都没有**的标的不在这里报：那是另一类问题（这一项从没排进计划、或者一直
    /// 在失败），全库体检里已经单独数了，混进来只会让两边的数字都说不清。
    /// </summary>
    /// <param name="calendar">交易日历，升序、date-only。</param>
    /// <param name="baseEarliest">基准口径每只票的最早日。</param>
    /// <param name="otherEarliest">被查口径每只票的最早日。</param>
    public static List<ShapeGap> FindLateStarts(
        IReadOnlyList<DateTime> calendar,
        IReadOnlyDictionary<string, DateTime> baseEarliest,
        IReadOnlyDictionary<string, DateTime> otherEarliest,
        int threshold = DefaultLateStartThreshold,
        int minBaseTradingDays = MinBaseTradingDays)
    {
        var result = new List<ShapeGap>();
        if (calendar.Count == 0) return result;

        var index = BuildIndex(calendar);
        foreach (var (code, baseDay) in baseEarliest)
        {
            if (!otherEarliest.TryGetValue(code, out var otherDay)) continue;   // 一根都没有：别处报
            int baseAt = IndexOfOnOrAfter(index, calendar, baseDay);
            int otherAt = IndexOfOnOrAfter(index, calendar, otherDay);
            if (baseAt < 0 || otherAt < 0) continue;                            // 落在日历之外，判不了
            if (calendar.Count - baseAt < minBaseTradingDays) continue;         // 新票，历史太短

            int late = otherAt - baseAt;
            if (late > threshold) result.Add(new ShapeGap(code, late, baseDay, otherDay));
        }
        return result;
    }

    /// <summary>
    /// 找出"尾巴停在几天前"的标的：某个口径的最新一根比交易日历的最后一天落后太多。
    ///
    /// ⚠ 只查**本地已经有这个口径**的标的。一根都没有的不在这里报，理由同 <see cref="FindLateStarts"/>。
    /// </summary>
    /// <param name="calendar">交易日历，升序、date-only，**已按体检的 cutoff 截断**。</param>
    /// <param name="latestByCode">被查口径每只票的最新日。</param>
    /// <param name="earliestByCode">被查口径每只票的最早日——用来跳过"最近才上市"的票
    /// （它们的最新日当然落后于日历最新日之前的那些天，那不叫落后）。传 null 就不做这道过滤。</param>
    public static List<ShapeGap> FindLateTails(
        IReadOnlyList<DateTime> calendar,
        IReadOnlyDictionary<string, DateTime> latestByCode,
        IReadOnlyDictionary<string, DateTime>? earliestByCode = null,
        int threshold = DefaultLateTailThreshold,
        int minBaseTradingDays = MinBaseTradingDays)
    {
        var result = new List<ShapeGap>();
        if (calendar.Count == 0) return result;

        var index = BuildIndex(calendar);
        var last = calendar[^1];
        int lastAt = calendar.Count - 1;

        foreach (var (code, latest) in latestByCode)
        {
            if (earliestByCode != null && earliestByCode.TryGetValue(code, out var earliest))
            {
                int firstAt = IndexOfOnOrAfter(index, calendar, earliest);
                if (firstAt >= 0 && calendar.Count - firstAt < minBaseTradingDays) continue;
            }

            int at = IndexOfOnOrBefore(index, calendar, latest);
            if (at < 0) continue;
            int behind = lastAt - at;
            if (behind > threshold) result.Add(new ShapeGap(code, behind, last, latest));
        }
        return result;
    }

    private static Dictionary<DateTime, int> BuildIndex(IReadOnlyList<DateTime> calendar)
    {
        var index = new Dictionary<DateTime, int>(calendar.Count);
        for (int i = 0; i < calendar.Count; i++) index[calendar[i].Date] = i;
        return index;
    }

    /// <summary>这一天在日历上的位置；不是交易日（停牌期间的日期不会出现这种情况，但退市/脏数据
    /// 会）就取它**之后**最近的那个交易日。全都在日历之后返回 -1。</summary>
    private static int IndexOfOnOrAfter(
        Dictionary<DateTime, int> index, IReadOnlyList<DateTime> calendar, DateTime day)
    {
        var d = day.Date;
        if (index.TryGetValue(d, out var at)) return at;
        for (int i = 0; i < calendar.Count; i++)
            if (calendar[i].Date >= d) return i;
        return -1;
    }

    /// <summary>同上，取它**之前**最近的那个交易日。全都在日历之前返回 -1。</summary>
    private static int IndexOfOnOrBefore(
        Dictionary<DateTime, int> index, IReadOnlyList<DateTime> calendar, DateTime day)
    {
        var d = day.Date;
        if (index.TryGetValue(d, out var at)) return at;
        for (int i = calendar.Count - 1; i >= 0; i--)
            if (calendar[i].Date <= d) return i;
        return -1;
    }
}
