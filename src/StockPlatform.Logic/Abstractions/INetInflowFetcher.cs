using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Fetches per-stock daily 资金净流入 history from a remote data source (EastMoney or
/// Sina — see EastMoneyNetInflowFetcher/SinaNetInflowFetcher, StockPlatform.Data.Remote).</summary>
public interface INetInflowFetcher
{
    /// <summary>这份数据**最早存在**的那一天——早于它的日子源上根本没有，请求也是白发。
    /// 区间回补的起点会被抬到这一天（见 FetchOrchestrator.FetchNetInflowRangeAsync）。</summary>
    DateOnly EarliestAvailable { get; }

    Task<List<NetInflow>> FetchAsync(string code, DateTime? start, DateTime? end, CancellationToken ct = default);

    /// <summary>Fires for out-of-band status worth surfacing to the UI — same rationale as
    /// IBarDataFetcher.OnStatus.</summary>
    event Action<string>? OnStatus;
}
