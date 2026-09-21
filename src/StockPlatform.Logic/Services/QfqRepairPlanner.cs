namespace StockPlatform.Logic.Services;

/// <summary>
/// 「抓取时发现基准漂移的这些票，哪几只真需要重取更早的历史」——纯判据，零 IO。
///
/// 2026-09-21 从 <c>FetchOrchestrator.RecordDriftedForRepair</c> 抽出来
/// （分层原则见 doc/solution-class-map.md §0.1）。
///
/// ════ 判据只有一条 ════
/// 漂移是在抓取时顺带发现的：窗口向前放宽了
/// <see cref="BarWritePlanner.DriftCheckLookbackDays"/> 天（不多花请求），拿回来的那一页
/// 已经**就地按新基准覆盖好了**。所以只有**历史比那一页更长**的票才有没修到的部分，
/// 才需要排进【重取前复权】的名单。
///
/// 反过来说：短历史的票（本地最早那根就在这一页里）已经整段修完，再排进名单纯属白跑一轮。
/// </summary>
public static class QfqRepairPlanner
{
    /// <summary>
    /// 挑出真需要重取的票。
    /// </summary>
    /// <param name="driftedCodes">本轮抓取中发现漂移的代码（可以有重复）。</param>
    /// <param name="earliestByCode">每只票本地最早那根日线的日期。</param>
    /// <param name="today">哪一天算"今天"（传进来才可单测）。</param>
    public static List<string> SelectForRepair(
        IEnumerable<string> driftedCodes,
        IReadOnlyDictionary<string, DateTime> earliestByCode,
        DateTime today)
    {
        var pageCovered = today.AddDays(-BarWritePlanner.DriftCheckLookbackDays).Date;
        return driftedCodes
            .Distinct(StringComparer.Ordinal)
            .Where(c => earliestByCode.TryGetValue(c, out var e) && e.Date < pageCovered)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }
}
