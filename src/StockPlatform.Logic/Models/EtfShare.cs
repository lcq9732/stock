namespace StockPlatform.Logic.Models;

/// <summary>
/// 上交所官方公布的一只 ETF 在某个交易日的总份额（EtfShare 表，2026-09-23）。
/// </summary>
/// <param name="Code">6 位裸码（上交所返回的 SEC_CODE，如 510150）。Bar 里同一只 ETF 存成 sh510150。</param>
/// <param name="TradeDate">统计日（上交所的 STAT_DATE）。</param>
/// <param name="SharesWan">总份额，单位**万份**（上交所 TOT_VOL 原样）。</param>
public sealed record EtfShareRow(string Code, DateOnly TradeDate, double SharesWan);
