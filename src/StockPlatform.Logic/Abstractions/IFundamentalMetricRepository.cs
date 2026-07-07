using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Local SQLite storage for the FundamentalMetric key-value table.</summary>
public interface IFundamentalMetricRepository
{
    void EnsureSchema();
    void InsertOrIgnore(IEnumerable<FundamentalMetric> metrics);
    List<FundamentalMetric> Query(string code, string metricKey, DateTime? start = null, DateTime? end = null);
}
