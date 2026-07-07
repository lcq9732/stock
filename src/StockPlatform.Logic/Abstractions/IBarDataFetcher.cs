using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Fetches raw bars for one stock/granularity from a remote data source (e.g. EastMoney).</summary>
public interface IBarDataFetcher
{
    Task<(string Name, List<Bar> Bars)> FetchAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default);

    /// <summary>
    /// Fires for out-of-band status worth surfacing to the UI even though it isn't tied to any
    /// single stock — e.g. "rate limiter is intentionally pausing for N minutes". Without this,
    /// a long defensive pause is indistinguishable in the log from the program having hung.
    /// </summary>
    event Action<string>? OnStatus;
}
