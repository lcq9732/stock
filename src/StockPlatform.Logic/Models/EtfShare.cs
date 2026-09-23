namespace StockPlatform.Logic.Models;

/// <summary>
/// 交易所官方公布的一只 ETF 在某个交易日的总份额（EtfShare 表，2026-09-23；同日加了深市）。
/// </summary>
/// <param name="Market">"sh" / "sz"。Bar 里同一只 ETF 存成 Market + Code（sh510150 / sz159915）。</param>
/// <param name="Code">6 位裸码（交易所返回的代码，如 510150 / 159915）。</param>
/// <param name="TradeDate">统计日。</param>
/// <param name="SharesWan">总份额，单位**万份**。上交所原样给万份；深交所导出给的是份，provider 里换算。</param>
public sealed record EtfShareRow(string Market, string Code, DateOnly TradeDate, double SharesWan)
{
    /// <summary>Bar 里的代码（带市场前缀）。</summary>
    public string BarCode => Market + Code;
}

/// <summary>一个份额源一次请求覆盖多长——上交所一天一个请求，深交所一个月一个请求。</summary>
public enum EtfShareBatch
{
    Day,
    Month,
}
