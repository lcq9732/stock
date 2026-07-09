using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// Reuses ChartBuilder's candlestick+BOLL+MACD base (see doc comment there) and overlays the two
/// trendlines + swing-point markers found by TriangleConvergenceDetector — the SAME Match object
/// TriangleConvergenceAnalysisEngine judged against, so the chart can never show different
/// trendlines than the ones the rule actually used.
/// </summary>
public static class TriangleConvergenceChartBuilder
{
    public static ChartResult Build(List<Bar> bars, int lookbackDays, TriangleConvergenceDetector.Match? match)
    {
        var chart = ChartBuilder.Build(bars, lookbackDays);
        if (match == null) return chart;

        int firstHigh = match.SwingHighIndices[0];
        int firstLow = match.SwingLowIndices[0];
        int today = bars.Count - 1;

        chart.Main.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.LinearEquation,
            XAxisKey = chart.MainDateAxis.Key,
            Slope = match.HighSlope,
            Intercept = match.HighIntercept,
            MinimumX = firstHigh,
            MaximumX = today,
            Color = OxyColors.Blue,
            LineStyle = LineStyle.Solid,
            Text = $"压力线(R²={match.HighR2:F2})",
        });
        chart.Main.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.LinearEquation,
            XAxisKey = chart.MainDateAxis.Key,
            Slope = match.LowSlope,
            Intercept = match.LowIntercept,
            MinimumX = firstLow,
            MaximumX = today,
            Color = OxyColors.Blue,
            LineStyle = LineStyle.Solid,
            Text = $"支撑线(R²={match.LowR2:F2})",
        });

        var highMarkers = new ScatterSeries { MarkerType = MarkerType.Triangle, MarkerFill = OxyColors.Red, MarkerSize = 5, XAxisKey = chart.MainDateAxis.Key, Title = "摆动高点" };
        foreach (var i in match.SwingHighIndices) highMarkers.Points.Add(new ScatterPoint(i, bars[i].High));
        chart.Main.Series.Add(highMarkers);

        var lowMarkers = new ScatterSeries { MarkerType = MarkerType.Triangle, MarkerFill = OxyColors.Green, MarkerSize = 5, XAxisKey = chart.MainDateAxis.Key, Title = "摆动低点" };
        foreach (var i in match.SwingLowIndices) lowMarkers.Points.Add(new ScatterPoint(i, bars[i].Low));
        chart.Main.Series.Add(lowMarkers);

        return chart;
    }
}
