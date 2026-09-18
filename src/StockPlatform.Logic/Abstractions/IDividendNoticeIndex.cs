namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 「最近哪些票出了分红公告」的索引（2026-09-18）——只回答**谁要抓**，不提供任何值。
///
/// ════ 为什么要它 ════
/// 分红的值源是新浪，那一页没有时间参数（URL 只有 stockid，一次返回整页全历史、无分页），
/// 所以"只拉某天之后的"省不了任何东西：这一项的成本 100% 在"抓哪些票"。
/// 全市场刷一轮是 5902 个请求，限流下要跑几小时，而其中绝大多数票这期间根本没出新方案。
///
/// 先问一句"最近谁出了新公告"，只抓这些 + 到期兜底的，每轮就从 5902 降到一两百。
///
/// ⚠ **只当索引，值仍取新浪**——索引源当值源是不合格的（退市股全空、配股比例只在文本里），
/// 而且实测有约 1% 的漏检（是索引源自己缺记录，扩大窗口救不了）。所以：
///   · 索引命中 ⇒ 必抓（哪怕昨天刚抓过，因为进度可能变了）；
///   · 索引**不命中不等于不用抓** ⇒ 仍要有周期性全量兜底（见 DividendTask.StaleDays）；
///   · 索引拿不到 ⇒ 退回纯水位线，**绝不能**当成"本轮没有要抓的"。
///
/// 见 doc/dividend-task-design.md §15-19。
/// </summary>
public interface IDividendNoticeIndex
{
    /// <summary>数据源名字，写日志用。</summary>
    string SourceName { get; }

    /// <summary>抓取进度/限流播报。</summary>
    event Action<string>? OnStatus;

    /// <summary>
    /// 最近 <paramref name="lookbackDays"/> 天内有分红公告（含预案、进度更新）的股票：
    /// **代码 → 这段时间里它最新那条公告的日期**。代码是 6 位纯数字，不带市场前缀。
    ///
    /// ⚠ 为什么要带日期、不能只给一份代码名单（2026-09-18 当天改的）：
    /// 回看窗口是固定的 45 天，只给名单的话"命中就抓"会让**同一只票在 45 天里天天被重抓**
    /// ——稳态每天 900 多个请求，而真正"自上次抓取后又出了新公告"的只有几十只。
    /// 有了日期，判据才能收敛成"上次抓取早于这条公告才抓"。
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetRecentAsync(
        int lookbackDays, CancellationToken ct = default);
}
