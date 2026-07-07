namespace StockPlatform.Logic.Models;

/// <summary>
/// Supported bar granularities. New periods can be added here without touching the
/// Bar table schema — see doc/data-platform-design.md section 4.
/// </summary>
public static class Granularity
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public const string Min1 = "min1";
    public const string Min5 = "min5";
    public const string Min15 = "min15";
    public const string Min30 = "min30";
    public const string Min60 = "min60";
}

/// <summary>One row of the multi-granularity Bar table (see doc/data-platform-design.md section 4).</summary>
public class Bar
{
    public string Code { get; set; } = "";
    public string Granularity { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public double Open { get; set; }
    public double Close { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Volume { get; set; }
    public double Amount { get; set; }
    public double PctChange { get; set; }
    public double Turnover { get; set; }
}
