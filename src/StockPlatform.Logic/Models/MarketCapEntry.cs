namespace StockPlatform.Logic.Models;

/// <summary>One stock's circulating market cap (流通市值，元), from whichever IMarketCapFetcher
/// implementation supplied it.</summary>
public record MarketCapEntry(string Code, double CirculatingMarketCap);
