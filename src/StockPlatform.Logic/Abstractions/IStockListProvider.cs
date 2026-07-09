namespace StockPlatform.Logic.Abstractions;

/// <summary><paramref name="CirculatingMarketCap"/> (流通市值，元) is populated only by providers
/// whose underlying list endpoint happens to carry it for free (e.g. SinaStockListProvider) —
/// null otherwise (e.g. EastMoneyStockListProvider's list endpoint doesn't have it). See
/// SinaListMarketCapFetcher, which relies on this field instead of a separate per-stock call.</summary>
public record StockListEntry(string Code, string Name, double? CirculatingMarketCap = null);

/// <summary>Fetches the full list of tradable A-share stock codes, so the Fetcher program
/// doesn't require the user to type codes in one by one — see doc/data-platform-design.md.</summary>
public interface IStockListProvider
{
    Task<List<StockListEntry>> GetAllStocksAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
