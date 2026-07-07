namespace StockPlatform.Logic.Abstractions;

public record StockListEntry(string Code, string Name);

/// <summary>Fetches the full list of tradable A-share stock codes, so the Fetcher program
/// doesn't require the user to type codes in one by one — see doc/data-platform-design.md.</summary>
public interface IStockListProvider
{
    Task<List<StockListEntry>> GetAllStocksAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
