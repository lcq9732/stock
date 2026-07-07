using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Local SQLite storage for the Bar table (see doc/data-platform-design.md section 4).</summary>
public interface IBarRepository
{
    void EnsureSchema();
    void InsertOrIgnore(IEnumerable<Bar> bars);
    DateTime? GetLatestPeriodStart(string code, string granularity);
    /// <summary>Latest period_start across ALL codes for a granularity — used by the Analyzer to
    /// show "本地数据最新到 X" without needing a separate sync-state file (see
    /// doc/analysis-app-design.md — the Analyzer reads the local database directly).</summary>
    DateTime? GetOverallLatestPeriodStart(string granularity);
    List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null);
    List<string> GetAllCodes();
}
