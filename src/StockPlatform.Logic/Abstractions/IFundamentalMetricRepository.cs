using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Local SQLite storage for the FundamentalMetric key-value table.</summary>
public interface IFundamentalMetricRepository
{
    void EnsureSchema();
    /// <summary>Insert-or-replace on the (code, metric_key, as_of_date) primary key — the same
    /// code/metric/day fetched more than once keeps whichever call wrote last, not whichever
    /// wrote first (unlike raw Bar rows, a metric value fetched again for a day it's already
    /// stored under isn't a duplicate fact to ignore, it's a correction/refresh).</summary>
    void Upsert(IEnumerable<FundamentalMetric> metrics);
    List<FundamentalMetric> Query(string code, string metricKey, DateTime? start = null, DateTime? end = null);
}
