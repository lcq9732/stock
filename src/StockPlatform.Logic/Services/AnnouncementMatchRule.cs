namespace StockPlatform.Logic.Services;

/// <summary>
/// 巨潮公告 ↔ 东财正文的**日期对位规则**（2026-10-22）。**纯计算，不碰 IO。**
///
/// ════ 为什么要它 ════
/// 两边没有共用的公告编号，只能在东财"该股最近 100 条公告"里按标题 + 日期找。原来的规则是
/// "日期相差 ±3 天、标题对得上就取**第一条**"，而东财列表新的在前——于是取到的是窗口里**最新**那篇。
/// 一只票几天内连发同名公告时就配错：600636 退市国化 06-16～06-19 每天一份「股份回购进展公告」，
/// 四天全配到 06-18 那篇；605020 永和股份 10 月好几份同理。库里回购 19 行、中标订单 4 行就是这么错的。
///
/// ════ 规则从哪来 ════
/// 东财 art_code 前缀带它自己的发布日（AN + yyyyMMdd）。拿 2614 行回购公告比巨潮公告日：
/// 东财**早一天** 1950 行、**同一天** 650 行，**没有一行正常的更晚**（晚 1~3 天的全是配错的）。
/// 所以只认"早一天或同一天"，两种都有时优先早一天（更常见）。
/// </summary>
public static class AnnouncementMatchRule
{
    /// <summary>
    /// 东财这条的日期对巨潮公告日来说能不能认、认的话排第几（越小越优先）。
    /// 早一天 → 0，同一天 → 1，其余 → null（不认）。
    /// </summary>
    public static int? DatePreference(DateOnly eastMoneyDate, DateOnly cninfoDate)
        => (eastMoneyDate.DayNumber - cninfoDate.DayNumber) switch
        {
            -1 => 0,
            0 => 1,
            _ => null,
        };

    /// <summary>东财 art_code 里带的发布日（"AN202608061827713711" → 2026-08-06）；认不出返回 null。</summary>
    public static DateOnly? ArtCodeDate(string? artCode)
    {
        if (artCode is not { Length: >= 10 } || !artCode.StartsWith("AN", StringComparison.Ordinal)) return null;
        return DateOnly.TryParseExact(artCode.Substring(2, 8), "yyyyMMdd", out var d) ? d : null;
    }

    /// <summary>
    /// 已入库的行里哪些**配错了正文**（返回下标）——给一次性清理用。
    ///
    /// 两类：① 东财日期比巨潮公告日**晚**（正常的从来不会）；
    /// ② 同一只票的几行**共用同一个 art_code**——只有日期对位最正常的那一行（按 <see cref="DatePreference"/>）
    ///    是真主，其余都是配错的；一行都不正常就全算错。
    /// 认不出日期的 art_code 不判（不是东财那种编号，无从比较）。
    /// </summary>
    public static IReadOnlySet<int> FindMismatched(IReadOnlyList<(string Code, DateOnly AnnounceDate, string ArtCode)> rows)
    {
        var wrong = new HashSet<int>();
        foreach (var group in rows.Select((r, i) => (r, i))
                     .Where(x => !string.IsNullOrEmpty(x.r.ArtCode))
                     .GroupBy(x => (x.r.Code, x.r.ArtCode)))
        {
            if (ArtCodeDate(group.Key.ArtCode) is not { } artDate) continue;
            var ranked = group
                .Select(x => (x.i, Pref: DatePreference(artDate, x.r.AnnounceDate)))
                .ToList();
            var owner = ranked.Where(x => x.Pref != null).OrderBy(x => x.Pref).Select(x => (int?)x.i).FirstOrDefault();
            foreach (var x in ranked)
                if (x.i != owner && (ranked.Count > 1 || x.Pref == null))
                    wrong.Add(x.i);
        }
        return wrong;
    }
}
