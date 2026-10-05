namespace StockPlatform.Logic.Models;

/// <summary>停复牌记录来自哪一路官网接口（存进 Suspension.source，改名等于改存储格式）。</summary>
public static class SuspensionSource
{
    /// <summary>上交所「停复牌信息·股票」（<c>sqlId=GW_PL_JYTS_TFPXX</c>，只留 controlType=TR 的股票行）。</summary>
    public const string SseStock = "sse_stock";

    /// <summary>上交所「停复牌信息·基金」（<c>sqlId=SSE_PL_JYTS_TFPXX_JJ</c>，含 ETF）。</summary>
    public const string SseFund = "sse_fund";

    /// <summary>深交所「停复牌提示」（<c>CATALOGID=1798</c>，股票、基金在一张表里）。</summary>
    public const string Szse = "szse";
}

/// <summary>
/// 一条官网停复牌记录，**按接口原样的语义存**（2026-09-30，见 doc/suspension-design.md）。
///
/// 两所的记法不一样，不在抓取时硬拧成一种——深交所的长期停牌分成「停牌」「取消停牌」两条记，
/// 要跨月配对才拼得出区间，抓一个月的时候拼不了。"某天是不是全天停牌"由
/// <see cref="Services.SuspensionRule"/> 按来源分别判。
///
/// 字段取值（2026-09-30 沙箱实测）：
/// · 上交所：<see cref="StartDay"/>/<see cref="EndDay"/> 是停牌起止日（**两头都含**，EndDay 空＝还没复牌）；
///   <see cref="Kind"/> 是 LXTP（连续停牌）/ LSTP（临时停牌）；<see cref="StopTime"/> 是 WH（全天）/
///   915、13（盘中一段）。<see cref="StartTime"/>/<see cref="EndTime"/> 恒为空。
/// · 深交所：StartDay+StartTime 是停牌时刻（StartTime＝"open" 表示开市起停，否则是 HH:mm:ss 盘中时刻）；
///   EndDay+EndTime 是**复牌**时刻（复牌日当天有交易）；Kind 是「停牌期限」原文（1天、1小时、停牌、
///   取消停牌……）；「取消停牌」那条没有 StartDay、只有复牌时刻。StopTime 恒为空。
/// </summary>
/// <param name="Code">6 位裸码（Bar 里 ETF 带市场前缀、个股不带，对照时见 <see cref="BarCodeMatches"/>）。</param>
/// <param name="EndReason">复牌原因（上交所 endStopReason；深交所没有这一列）。</param>
/// <param name="ControlType">品种（上交所股票查询的 controlType：TR 股票 / GB 债券 / CB 可转债；别的来源空）。</param>
/// <param name="EndKind">上交所基金 endStopType（复牌时的停牌类型）。</param>
/// <param name="StartType">上交所基金 startType。</param>
/// <param name="EndType">上交所基金 endType。</param>
/// <param name="DateSource">上交所基金 dateSource。</param>
/// <param name="FullName">上交所基金 expandAbbr（扩位简称）。</param>
/// <remarks>
/// **接口返回的每一列都有对应字段**（2026-09-30 用户定：取到的全存）：上交所股票 9 列、上交所基金 13 列、
/// 深交所 6 列，一列不落。没有这一列的来源存空串。
/// </remarks>
public sealed record SuspensionRow(
    string Source, string Market, string Code, string Name,
    DateOnly? StartDay, string StartTime,
    DateOnly? EndDay, string EndTime,
    string Kind, string StopTime, string Reason,
    string EndReason = "", string ControlType = "",
    string EndKind = "", string StartType = "", string EndType = "",
    string DateSource = "", string FullName = "")
{
    /// <summary>深交所时刻字段里"开市"的记法。</summary>
    public const string AtOpen = "open";

    /// <summary>
    /// 同一条记录再抓一次时认得出是它（UPSERT 的键）。**不含结束日**：上交所仍在停牌的记录
    /// 复牌后会补上结束日，键要不变才能覆盖掉那条"没有结束日"的旧行——含了的话旧行留着，
    /// 这只票就永远被判成"还在停牌"。
    /// 深交所「取消停牌」没有开始时刻，同一只票可能有好几条，所以这种行用复牌时刻区分。
    /// </summary>
    public string Key => StartDay is null
        ? $"-|{Kind}|{EndDay:yyyy-MM-dd}|{EndTime}"
        : $"{StartDay:yyyy-MM-dd}|{StartTime}|{Kind}|{StopTime}";

    /// <summary>Bar 里的代码是不是这只（个股 6 位裸码；ETF 是市场前缀 + 6 位）。</summary>
    public bool BarCodeMatches(string barCode) =>
        barCode.Length == 8 ? barCode == Market + Code : barCode == Code;
}
