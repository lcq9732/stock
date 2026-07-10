using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Fetches per-stock circulating market cap (流通市值) from a remote data source
/// (EastMoney or Tencent — see EastMoneyMarketCapFetcher/TencentMarketCapFetcher,
/// StockPlatform.Data.Remote).</summary>
public interface IMarketCapFetcher
{
    Task<MarketCapFetchResult> GetMarketCapsAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>Fires for out-of-band status worth surfacing to the UI — same rationale as
    /// IBarDataFetcher.OnStatus.</summary>
    event Action<string>? OnStatus;
}
