namespace StockPlatform.Logic.Models;

/// <summary>Common metric_key values. New keys can be added without a schema change.</summary>
public static class MetricKeys
{
    public const string Revenue = "revenue";
    public const string NetProfit = "net_profit";
    public const string Roe = "roe";
    public const string Eps = "eps";
    public const string Bvps = "bvps";
    public const string Pe = "pe";
    public const string Pb = "pb";
}

/// <summary>One row of the key-value FundamentalMetric table (see doc/data-platform-design.md section 4).</summary>
public class FundamentalMetric
{
    public string Code { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public DateTime AsOfDate { get; set; }
    public double Value { get; set; }
    public string Source { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}
