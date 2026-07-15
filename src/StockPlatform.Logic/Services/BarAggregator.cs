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
                // 涨跌幅不再存储（消费端用收盘价现算，见 2026-07-14 变更记录）。
                // 周/月线是每次抓取都从头重新聚合、整体覆盖写入的（不参与"收盘后就不再抓"的水位线
                // 判断，见FetchOrchestrator），FetchedAt 对它们其实是inert的，写DateTime.Now只是为了
                // 跟Bar模型的其它用法保持一致，不留一个没意义的默认值。
                FetchedAt = DateTime.Now,
            };
            result.Add(aggregated);
        }

        return result;
    }
}
