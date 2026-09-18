namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只票的股东数据水位线（2026-09-18）——**不是独立的状态表**，是从 <c>ShareholderCount</c>
/// 现算出来的（每票 <c>max(report_date)</c> + <c>max(fetched_at)</c>）。
///
/// 为什么不像分红那样单开一张状态表：分红必须开，因为"没分红的票一行都不写"，
/// "没抓过"和"抓了没有"分不开。股东这边两张表"只有一张有数据"的票是 0 只，
/// 历史数据天然就是水位线，也就不需要播种。
///
/// 已知代价：那 13 只"一行都没有"的在市股（新股居多）不在字典里、每轮都会被抓一次。
/// 不值得为它们单开一张表。
/// </summary>
/// <param name="ReportDate">
/// 库里这只票**股东户数**的最新报告期。
///
/// ⚠ 用户数表、**不用十大股东表**：两表最新期不一致的有 7.9%（5557 只里 441 只），
/// 方向是十大股东更"新"——那些额外期次是不定期的股东名单变动公告（增发、协议转让之后），
/// 不是季度期。拿它去跟"应有季度期"比会因为偏高而把该抓的票判成已经很新 ⇒ 漏抓。
/// 见 doc/shareholder-task-design.md §1.2。
/// </param>
/// <param name="FetchedAt">最后一次抓取的时刻。给"多久整轮重刷一遍"的时间兜底用。</param>
public readonly record struct ShareholderFetchState(DateTime ReportDate, DateTime FetchedAt);
