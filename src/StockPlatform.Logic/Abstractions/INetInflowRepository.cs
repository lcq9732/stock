using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Local SQLite storage for the NetInflow table (主力净流入日线历史，见
/// doc/data-platform-design.md) — same watermark-driven incremental-fetch shape as IBarRepository,
/// but fully independent since it comes from a different EastMoney endpoint on its own schedule.</summary>
public interface INetInflowRepository
{
    void EnsureSchema();
    void InsertOrIgnore(IEnumerable<NetInflow> rows);
    DateTime? GetLatestPeriodStart(string code);
    List<NetInflow> Query(string code, DateTime? start = null, DateTime? end = null);
}
