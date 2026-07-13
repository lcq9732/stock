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
    /// <summary>Earliest period_start across ALL codes for a granularity — paired with
    /// <see cref="GetOverallLatestPeriodStart"/> so the Fetcher UI can show "本地数据覆盖范围：X 至 Y"
    /// (see doc/data-platform-design.md).</summary>
    DateTime? GetOverallEarliestPeriodStart(string granularity);
    /// <summary>某个时点(含)之前的最新 period_start——给"按历史截止日期验证"用（见
    /// CutoffBarRepository）：用户输入的截止日可能是周末/节假日，需要据此定位真正的最后交易日。</summary>
    DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff);
    List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null);
    List<string> GetAllCodes();
}
