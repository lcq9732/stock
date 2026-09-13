using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>抓回来之后怎么落库。</summary>
public enum ValueFixWrite
{
    /// <summary>只覆盖 volume/amount/turnover 三列（<c>UpdateVolumeAmountTurnover</c>），**绝不动 OHLC**。</summary>
    ThreeColumns,

    /// <summary>整段交给 <c>InsertOrRefreshUnconfirmed</c> 裁决（只覆盖未确认的行）。</summary>
    WholeSegment,
}

/// <summary>一次抓取动作。</summary>
/// <param name="IsBaselineRefetch">
/// 这次抓的是**基准口径 day**、为了修另一个口径报出来的不一致（见 <see cref="ValueIssueFixPlan"/>）。
/// 它不是名单里的某一段，所以不计进度。
/// </param>
public sealed record ValueFixFetch(
    string Code, string Granularity, DateTime From, DateTime To,
    ValueFixWrite Write, bool IsBaselineRefetch = false);

/// <summary>一批值问题段的执行计划：哪些抓不了、要发哪些请求。</summary>
public sealed record ValueFixPlan(
    IReadOnlySet<MissingBarRange> Skipped, IReadOnlyList<ValueFixFetch> Fetches);

/// <summary>
/// 【重新拉取失败股票】里"修体检报出的值问题"那一步的**计划**（2026-09-11 从
/// <c>FetchOrchestrator.FillValueIssuesAsync</c> 抽出来）。纯计算、无 IO——抽出来的理由跟
/// <see cref="ValueIssueRecheck"/> 一样：orchestrator 有三十多个构造参数、没法单测，
/// 而这里错一点就是"每轮都发请求、每轮都修不掉"，且不报任何错。
///
/// ════ 三件事 ════
/// ① **抓不了的先挑出来**（<see cref="ValueFixPlan.Skipped"/>，<c>Tries</c> 一动不动，
///    免得空跑两轮被误判成"数据源确实没有"）：后复权/不复权只有腾讯给；
///    <see cref="Granularity.DayAdj"/> 是本地重算的产物，抓不来，留着等【重算回测序列】。
///
/// ② **按 (票, 口径) 分组，一组只抓一次**。同一 (票, 口径) 常有几条不同 Reason 的记录：
///    2026-09-01 那批盘中行既是半天快照（intraday）、量额自然也跟 day 对不上（inconsistent），
///    两条判据都命中。逐段抓的话 9398 段里有 4020 段是白发的请求（2026-09-09 实测）。
///    落库方式看这一组**是不是全是** inconsistent：全是就只覆盖三列，混着别的 Reason
///    （盘中固化连 OHLC 都错）就整段重抓——那条路顺带也把量额修好，不会漏。
///
/// ③ ⚠ **inconsistent 要连基准 <see cref="Granularity.Day"/> 一起重抓**（2026-09-11 补，
///    doc/bar-value-audit-design.md §16 缺陷一）。V3 拿 <c>day</c> 当基准逐个比别的口径，
///    所以**报出来的永远是非基准口径**，而错值恰恰多数在基准那一边——09-11 手工修的 101 行
///    turnover 错值分布是 <c>day</c> 62 行、<c>day_hfq</c> 39 行、<c>day_raw</c> **0 行**，
///    100% 不在只抓非基准时重抓的那一边。只刷一边的话两边永远各说各话，实测 <c>Tries</c>
///    从 2 一路爬到 5 也对不上。同一只票的两个口径各触发一次基准重抓时按 (票, 区间) 去重。
/// </summary>
public static class ValueIssueFixPlan
{
    public static ValueFixPlan Build(IEnumerable<MissingBarRange> batch, bool supportsHfq)
    {
        var skipped = new HashSet<MissingBarRange>();
        var fetchable = new List<MissingBarRange>();

        foreach (var r in batch)
        {
            // 后复权/不复权只有腾讯给。数据源不支持这个口径就原样留着。
            if (r.Granularity != Granularity.Day && r.Granularity != Granularity.DayAdj && !supportsHfq)
                skipped.Add(r);
            // day_adj 是本地重算的产物，抓不来——留着，等【重算回测序列】跑。
            else if (r.Granularity == Granularity.DayAdj)
                skipped.Add(r);
            else
                fetchable.Add(r);
        }

        var fetches = new List<ValueFixFetch>();
        var baselineDone = new HashSet<(string Code, DateTime From, DateTime To)>();

        foreach (var g in fetchable.GroupBy(r => (r.Code, r.Granularity)))
        {
            var from = g.Min(x => x.From);
            var to = g.Max(x => x.To);
            bool onlyInconsistent = g.All(x => x.EffectiveReason == AuditFindingKind.Inconsistent);

            fetches.Add(new ValueFixFetch(g.Key.Code, g.Key.Granularity, from, to,
                onlyInconsistent ? ValueFixWrite.ThreeColumns : ValueFixWrite.WholeSegment));

            // ③ 基准也重抓——四份都来自同一时刻才可能一致
            if (onlyInconsistent && g.Key.Granularity != Granularity.Day
                && baselineDone.Add((g.Key.Code, from, to)))
            {
                fetches.Add(new ValueFixFetch(g.Key.Code, Granularity.Day, from, to,
                    ValueFixWrite.ThreeColumns, IsBaselineRefetch: true));
            }
        }

        return new ValueFixPlan(skipped, fetches);
    }
}
