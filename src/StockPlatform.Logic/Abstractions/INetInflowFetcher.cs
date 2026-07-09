using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Fetches per-stock daily 资金净流入 history from a remote data source (EastMoney or
/// Sina — see EastMoneyNetInflowFetcher/SinaNetInflowFetcher, StockPlatform.Data.Remote).</summary>
public interface INetInflowFetcher
{
    Task<List<NetInflow>> FetchAsync(string code, DateTime? start, DateTime? end, CancellationToken ct = default);

    /// <summary>Fires for out-of-band status worth surfacing to the UI — same rationale as
    /// IBarDataFetcher.OnStatus.</summary>
    event Action<string>? OnStatus;
}
