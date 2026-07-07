namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// A named, swappable data source (see doc/data-platform-design.md — multi-source design to
/// spread load across vendors, speed up the first full backfill via parallelism, and provide
/// failover if one vendor rate-limits/blocks this machine's IP). Bundles the stock-LIST provider
/// alongside the bar-data fetcher so switching sources routes both steps away from a blocked
/// vendor, not just bar fetching — see doc/data-platform-design.md section 3.4.
/// </summary>
public record NamedBarSource(string Name, IBarDataFetcher Fetcher, IStockListProvider StockListProvider);
