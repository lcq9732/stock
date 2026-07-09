using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Supplies circulating market cap (流通市值) as a byproduct of Sina's stock-list scan
/// (<see cref="SinaStockListProvider"/>) instead of a separate per-stock HTTP call — the DEFAULT
/// market-cap source (2026-07-08), replacing <see cref="TencentMarketCapFetcher"/>.
///
/// Rationale: SinaStockListProvider already pages through the entire market (~55 requests) for
/// the stock-LIST step, and each row already carries `nmc`（流通市值）for free (verified against
/// TencentMarketCapFetcher's per-stock value for 3 diverse stocks before trusting it — see
/// doc/data-platform-design.md §3.5). Replacing ~5000 individual per-stock calls with ~55
/// paginated list requests is both faster AND consolidates onto a single already-relied-on vendor
/// (Sina), matching the user's "一个源就够就不要拼凑" preference (2026-07-08).
///
/// This means <see cref="StockPlatform.Data.Orchestration.FetchOrchestrator"/>'s "拉取当天" mode
/// now does a full market-wide list scan purely to refresh market cap, even though it otherwise
/// deliberately avoids re-scanning the market for its K线 stock set (see that class's remarks) —
/// the user explicitly confirmed the added time cost is acceptable ("慢点没关系", 2026-07-08) in
/// exchange for 拉取当天 also keeping market cap current, not just 拉取全部.
/// </summary>
public class SinaListMarketCapFetcher : IMarketCapFetcher
{
    private readonly SinaStockListProvider _listProvider;

    // SinaStockListProvider has its own internal retry/backoff (RetryDelays) and doesn't expose a
    // RateLimiter-driven OnStatus like the per-stock fetchers — nothing to forward here.
    public event Action<string>? OnStatus { add { } remove { } }

    public SinaListMarketCapFetcher(SinaStockListProvider? listProvider = null)
    {
        _listProvider = listProvider ?? new SinaStockListProvider();
    }

    public async Task<List<MarketCapEntry>> GetMarketCapsAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct = default)
    {
        var wanted = codes.ToHashSet();
        var allStocks = await _listProvider.GetAllStocksAsync(progress, ct);
        return allStocks
            .Where(s => wanted.Contains(s.Code) && s.CirculatingMarketCap is > 0)
            .Select(s => new MarketCapEntry(s.Code, s.CirculatingMarketCap!.Value))
            .ToList();
    }
}
