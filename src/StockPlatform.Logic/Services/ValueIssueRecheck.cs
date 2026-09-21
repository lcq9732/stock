using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 值问题补过一轮之后**还在不在**的判定（2026-09-09）。纯计算、无 IO——抽出来的理由跟
/// <see cref="YearGapCalculator"/>、<see cref="IncrementalWindowCalculator"/> 一样：
/// 这类判据出错是静默的，而它所在的那段编排（现在是 <c>BarFetchTaskBase.FillValueAsync</c>）没法单测
/// （orchestrator 有三十多个构造参数）。
///
/// ════ 为什么不能沿用缺行那套判定 ════
/// 缺行的判定是"这一天的行还在不在"（<c>FindGaps</c>）。值错的行**一直都在**——拿那套去判，
/// 会把每一段都算成"已补齐"划掉，哪怕值根本没被覆盖（比如重抓又落在盘中）。所以这里吃的是
/// **对应判据的复查结果**：判据不再命中，才算修好。
/// </summary>
public static class ValueIssueRecheck
{
    /// <summary>
    /// 一段值问题补过之后的结论。
    ///
    /// <para><paramref name="liveDays"/> 是复查时"这个 (代码, 口径, 原因) 仍然命中判据"的那些交易日
    /// （由 <c>SqliteBarValueAuditor</c> 只查这批 code 得出）。</para>
    ///
    /// <returns>
    /// <c>null</c>＝修好了，可以从待补名单划掉；否则是收窄后的新段（只留仍然命中的那几天）
    /// 且 <see cref="MissingBarRange.Tries"/> 加一。
    ///
    /// ⚠ **到了 Tries 上限也照样返回段、不该转进「确认没有」白名单**——那份名单是给停牌用的
    /// （数据源就是没有那一天），而值错是"数据在但不对"，进白名单等于给错值发永久豁免。
    /// 收敛靠"真修好"，不靠计数到顶。
    /// </returns>
    /// </summary>
    public static MissingBarRange? Survives(MissingBarRange range, IReadOnlyList<DateTime>? liveDays)
    {
        if (liveDays is null || liveDays.Count == 0) return null;

        var left = liveDays
            .Select(d => d.Date)
            .Where(d => d >= range.From.Date && d <= range.To.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        if (left.Count == 0) return null;

        return new MissingBarRange
        {
            Code = range.Code,
            Granularity = range.Granularity,
            Reason = range.EffectiveReason,
            From = left[0],
            To = left[^1],
            Days = left.Count,
            Tries = range.Tries + 1,
        };
    }
}
