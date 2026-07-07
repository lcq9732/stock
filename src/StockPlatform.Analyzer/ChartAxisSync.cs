using OxyPlot;
using OxyPlot.Axes;

namespace StockPlatform.Analyzer;

/// <summary>
/// Keeps N charts' day/month axis pairs (see ChartBuilder.BuildDateAxes) zoomed/panned together
/// and their day-label density synced to actual rendered pixel width. Shared by ChartBuilder (2
/// panels: 主图+MACD) and GoldenCrossChartBuilder (5 panels) so the pan/zoom/label-density wiring
/// — and its two hard-won gotchas — only has one implementation:
/// - the month-tier axis must NOT have IsPanEnabled/IsZoomEnabled=false, because Axis.Zoom()
///   checks IsZoomEnabled even for programmatic calls, silently blocking this class's own Zoom()
///   on that axis (see BuildDateAxes);
/// - the visible range must be tracked in a local variable, not read back from
///   axis.ActualMinimum/ActualMaximum — those are only populated by OxyPlot after a render pass,
///   and UpdatePlotWidth (returned below) can run before the PlotView has ever rendered.
/// </summary>
internal static class ChartAxisSync
{
    public static (double Day, double Month) ComputeSteps(
        double visibleBars, double plotPixelWidth, double pxPerDayLabel, double pxPerMonthLabel, int tradingDaysPerMonth)
    {
        var maxDayLabels = Math.Max(1, plotPixelWidth / pxPerDayLabel);
        var maxMonthLabels = Math.Max(1, plotPixelWidth / pxPerMonthLabel);
        var dayStep = Math.Max(1, Math.Round(visibleBars / maxDayLabels));
        var monthStep = Math.Max(tradingDaysPerMonth, Math.Round(visibleBars / maxMonthLabels));
        return (dayStep, monthStep);
    }

    /// <summary>Wires AxisChanged on every day axis so panning/zooming any one of them re-syncs
    /// all the others (day+month tiers, across all panels) to the same range, and returns an
    /// UpdatePlotWidth callback the caller should invoke from PlotView.SizeChanged.</summary>
    public static Action<double> Wire(
        IReadOnlyList<PlotModel> models,
        IReadOnlyList<LinearAxis> dayAxes,
        IReadOnlyList<LinearAxis> monthAxes,
        double visibleStart, double visibleEnd,
        double initialPlotWidth, double pxPerDayLabel, double pxPerMonthLabel, int tradingDaysPerMonth)
    {
        double plotPixelWidth = initialPlotWidth;
        double currentMin = visibleStart, currentMax = visibleEnd;
        var syncing = false;

        void SyncTo(double min, double max)
        {
            currentMin = min;
            currentMax = max;
            var (dayStep, monthStep) = ComputeSteps(max - min, plotPixelWidth, pxPerDayLabel, pxPerMonthLabel, tradingDaysPerMonth);
            foreach (var axis in dayAxes)
            {
                axis.Zoom(min, max);
                axis.MajorStep = axis.MinorStep = dayStep;
            }
            foreach (var axis in monthAxes)
            {
                axis.Zoom(min, max);
                axis.MajorStep = axis.MinorStep = monthStep;
            }
            foreach (var m in models) m.InvalidatePlot(false);
        }

        // AxisChanged在OxyPlot里标了obsolete，但目前（2.1.2版本）官方还没有给出替代方案（社区讨论也
        // 是继续用这个），能用、没被真正移除，先用它。
#pragma warning disable CS0618
        foreach (var dayAxis in dayAxes)
        {
            dayAxis.AxisChanged += (_, _) =>
            {
                if (syncing) return;
                syncing = true;
                SyncTo(dayAxis.ActualMinimum, dayAxis.ActualMaximum);
                syncing = false;
            };
        }
#pragma warning restore CS0618

        void UpdateWidth(double pixelWidth)
        {
            if (syncing) return;
            syncing = true;
            plotPixelWidth = pixelWidth;
            SyncTo(currentMin, currentMax);
            syncing = false;
        }

        return UpdateWidth;
    }
}
