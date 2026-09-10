namespace StockPlatform.Logic.Models;

/// <summary>体检发现的一条问题。<see cref="AuditFindingKind"/> 决定它怎么被处置。</summary>
/// <param name="Kind">见 <see cref="AuditFindingKind"/>。</param>
/// <param name="Scope">
/// 这条属于哪个"面"（标的类型|口径，如 <c>stock|day_raw</c>）。**落账的单位就是面**——
/// 一批 = 一个面的全部发现，落账时只替换这个面的旧记录、别的面原样保留（理由见
/// doc/full-audit-task-migration-design.md §1）。<c>Kind=note</c> 的行不需要它。
/// </param>
/// <param name="Granularity">
/// 这一条属于哪个口径。**不能一律从 <paramref name="Scope"/> 推**：空洞体检的一批只涉及一个口径
/// （面＝类型×口径），但值体检的一批**横跨四个日线口径**（那几条判据是全表一次扫出来的，
/// 按口径分四次扫是四倍的钱）。
/// </param>
/// <param name="Note">Kind=note 时的那行人读文本（板块指数/day_adj/覆盖形状/日频表这些只报数的面）。</param>
public sealed record AuditFinding(
    string Kind,
    string? Scope = null,
    string? Code = null,
    string? Granularity = null,
    DateTime From = default,
    DateTime To = default,
    int Days = 0,
    string? Note = null);

/// <summary>
/// 体检发现的种类。除 <see cref="Note"/> 之外都会进 <see cref="Manifest.MissingBars"/> 交给
/// 【重新拉取失败】纠正，而且**复查时必须按种类分派回对应判据**——值错的行一直都在，
/// 拿"行在不在"去复查会一律判成"已补齐"划掉（见 doc/bar-value-audit-design.md §5）。
/// </summary>
public static class AuditFindingKind
{
    /// <summary>缺行（交易日历里有、这只票没有）。这是 2026-09-09 之前唯一的一种。</summary>
    public const string Gap = "gap";

    /// <summary>盘中固化：<c>fetched_at</c> 早于该交易日 16:00，值是半天快照。</summary>
    public const string Intraday = "intraday";

    /// <summary>关键列为 NULL（批量导入绕过写入路径造成）。</summary>
    public const string NullValue = "null_value";

    /// <summary>同日多口径的 volume/amount/turnover 对不上。</summary>
    public const string Inconsistent = "inconsistent";

    /// <summary>OHLC 不自洽（high &lt; max(open,close) 之类）。</summary>
    public const string Ohlc = "ohlc";

    /// <summary>
    /// <c>amount / (volume × close)</c> 不在 ≈100（手）或 ≈1（科创板按股）附近——量或额本身不对。
    /// **只报数、不进待补名单**：603999 那种是数据源自己给错的，重抓大概率拿回同样的值，
    /// 要修得先查清成因（见 memory project_bar_volume_unit_bug）。
    /// </summary>
    public const string Ratio = "ratio";

    /// <summary>只报数、不进待补名单的那些（本地合成/本地重算的面、覆盖形状、日频表）。</summary>
    public const string Note = "note";
}
