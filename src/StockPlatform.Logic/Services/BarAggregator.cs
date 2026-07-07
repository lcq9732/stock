using System.Globalization;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// Builds week/month bars by aggregating day bars locally, instead of fetching them from a
/// remote endpoint (see doc/data-platform-design.md section 3.3) — works offline and doesn't
/// depend on the data source supporting those periods directly.
/// </summary>
public static class BarAggregator
{
    public static List<Bar> ToWeekly(IReadOnlyList<Bar> dayBars) =>
        Aggregate(dayBars, b => ISOWeek.GetYear(b.PeriodStart) * 100 + ISOWeek.GetWeekOfYear(b.PeriodStart), Granularity.Week);

    public static List<Bar> ToMonthly(IReadOnlyList<Bar> dayBars) =>
        Aggregate(dayBars, b => b.PeriodStart.Year * 100 + b.PeriodStart.Month, Granularity.Month);

    private static List<Bar> Aggregate(IReadOnlyList<Bar> dayBars, Func<Bar, int> groupKey, string granularity)
    {
        var ordered = dayBars.OrderBy(b => b.PeriodStart).ToList();
        var result = new List<Bar>();
        double? prevClose = null;

        foreach (var group in ordered.GroupBy(groupKey))
        {
            var bars = group.OrderBy(b => b.PeriodStart).ToList();
            var close = bars[^1].Close;
            var aggregated = new Bar
            {
                Code = bars[0].Code,
                Granularity = granularity,
                PeriodStart = bars[0].PeriodStart,
                Open = bars[0].Open,
                Close = close,
                High = bars.Max(b => b.High),
                Low = bars.Min(b => b.Low),
                Volume = bars.Sum(b => b.Volume),
                Amount = bars.Sum(b => b.Amount),
                Turnover = bars.Sum(b => b.Turnover),
                PctChange = prevClose is > 0 ? (close - prevClose.Value) / prevClose.Value * 100 : 0,
            };
            result.Add(aggregated);
            prevClose = close;
        }

        return result;
    }
}
