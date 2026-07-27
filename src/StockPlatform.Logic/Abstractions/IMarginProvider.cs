using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取某交易日全市场融资融券明细（交易所官方源：上交所 JSON + 深交所 xlsx，合并返回）——
/// 最权威、非东财。按交易日拉，非交易日/无数据返回空。</summary>
public interface IMarginProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某交易日沪深两市全部标的的融资融券明细（含融资余额）。</summary>
    Task<List<MarginDetailRow>> GetDetailAsync(DateOnly date, CancellationToken ct = default);
}
