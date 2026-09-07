namespace StockPlatform.Logic.Services;

/// <summary>
/// 把一个日期区间按**自然年**切片。纯计算、无 IO——跟 <see cref="YearGapCalculator"/> 同样的理由
/// 放在这里：它是个容易写错边界（跨年那两端）的小函数，抽出来才测得到。
/// </summary>
public static class CalendarYearSlicer
{
    /// <summary>
    /// 把 [start, end] 切成每年一片；单年区间原样返回一片，<c>start &gt; end</c> 返回空列表。
    /// 首尾两片保留原始的起止日（不会被扩成整年），所以切片的并集严格等于原区间。
    ///
    /// ════ 用途：绕开搜索源的单次翻页上限 ════
    /// 巨潮全文检索是"一个关键词 + 一个日期窗口"翻页返回的，而 CninfoAnnouncementSearchProvider
    /// 对单次搜索有 30 页硬上限（那个上限本身是对的：防一个宽关键词无限翻页）。日常增量只扫最近
    /// 几十天，30 页绰绰有余；但区间回补一填就是二十几年，翻满 30 页就停、**剩下的全部静默丢掉**
    /// ——日志上看是"搜索完成"，完全不像少了东西。按年切之后每年各自享有 30 页额度。
    ///
    /// 切自然年而不是等长窗口，是为了让"哪一年抓过了"在日志里一眼能对上。
    /// </summary>
    public static List<(DateOnly Start, DateOnly End)> Split(DateOnly start, DateOnly end)
    {
        var slices = new List<(DateOnly, DateOnly)>();
        if (start > end) return slices;
        for (int y = start.Year; y <= end.Year; y++)
        {
            var s = y == start.Year ? start : new DateOnly(y, 1, 1);
            var e = y == end.Year ? end : new DateOnly(y, 12, 31);
            slices.Add((s, e));
        }
        return slices;
    }
}
