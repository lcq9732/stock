namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只票的财务抓取水位线（<c>FinancialFetchState</c> 表一行）。
///
/// <see cref="TargetDate"/> 是 2026-09-19 补的，补的是一个空转了好几天的洞：判据只有
/// "本地报告期够不够新"，而**没有任何一列记得住"我已经为这个目标期问过了、数据源就是没有"**。
/// 于是停牌/退市整理期那批票——公司早就不出新报告期了，法定截止日却一天天往前走——
/// 每轮都被判成"落后"、每轮抓完 <see cref="ReportDate"/> 原地不动、下轮再来。
/// 实测 13 只票每 20 分钟重抓一次、每轮重写 29263 行，连着跑了四天。
/// </summary>
/// <param name="ReportDate">库里这只票财报的最新报告期（抓到什么就是什么，不是"应该有什么"）。</param>
/// <param name="KeysVersion">抓这份数据时的 <see cref="FinancialKeys.Version"/>。科目集扩充后要重抓。</param>
/// <param name="TargetDate">
/// 上一轮抓取时**冲着哪个报告期去的**。跟 <see cref="ReportDate"/> 一比就知道上次是"抓到了"
/// 还是"问了但数据源没有"：后者在目标期前进之前不必反复去问。没有记录时为 null（老数据）。
/// </param>
/// <param name="FetchedAt">最后一次抓取的时刻。配合 <see cref="TargetDate"/> 做冷却。</param>
public readonly record struct FinancialFetchState(
    DateTime ReportDate, int KeysVersion, DateTime? TargetDate, DateTime? FetchedAt);
