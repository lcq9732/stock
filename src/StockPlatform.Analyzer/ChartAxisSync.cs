using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

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
    // 每根K线固定占这么多像素宽，不随缩放变化——CandleStickSeries.CandleWidth 是以"数据坐标"
    // （每根K线之间的1个索引单位）为单位的固定值，缩小看更多天时会挤成一条线，放大看更少天时又
    // 显得K线之间空隙过大；固定像素宽后，每次缩放都按当前"每根K线实际能分到多少像素"反过来换算
    // 出这次该用的数据坐标宽度，视觉上的K线粗细/间隙就不会随缩放变化。
    internal const double CandleWidthPx = 11;

    // Y轴自适应可见范围时留的边距比例——纯留白，不是"数据不准"，太小会让最高/最低点贴着面板
    // 上下边缘（看起来被裁切），太大又会把K线挤扁回原来的问题。
    private const double YRangePadding = 0.08;

    public static Action<double> Wire(
        IReadOnlyList<PlotModel> models,
        IReadOnlyList<LinearAxis> dayAxes,
        IReadOnlyList<LinearAxis> monthAxes,
        double visibleStart, double visibleEnd,
        double initialPlotWidth, double pxPerDayLabel, double pxPerMonthLabel, int tradingDaysPerMonth,
        IReadOnlyList<CandleStickSeries>? candleSeries = null,
        IReadOnlyList<(LinearAxis YAxis, Func<int, int, (double Min, double Max)?> RangeFn)>? yAxisRanges = null)
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
            if (candleSeries is { Count: > 0 } && max > min)
            {
                var pxPerBar = plotPixelWidth / (max - min);
                // Clamp防止极端缩放下宽度失控——放得很大时不让相邻K线叠在一起(≤0.9)，缩得很小
                // 时不让宽度变成0导致K线整个消失(≥0.1)。
                var width = pxPerBar <= 0 ? 0.5 : Math.Clamp(CandleWidthPx / pxPerBar, 0.1, 0.9);
                foreach (var cs in candleSeries) cs.CandleWidth = width;
            }
            if (yAxisRanges is { Count: > 0 })
            {
                // Y轴改成"只看当前可见的这一段K线"重新算范围，而不是OxyPlot默认的"整条系列(可能是
                // 三年历史)算一次范围就不再变"——不这么改的话，缩放/平移K线图只会移动X轴，Y轴还是
                // 那个大范围没变，可见的这几十根K线的实际价格波动只占面板高度一小段，看起来很扁。
                int startIdx = (int)Math.Floor(min);
                int endIdx = (int)Math.Ceiling(max);
                foreach (var (yAxis, rangeFn) in yAxisRanges)
                {
                    var range = rangeFn(startIdx, endIdx);
                    if (range == null) continue;
                    var (rMin, rMax) = range.Value;
                    var span = rMax - rMin;
                    var pad = span > 0 ? span * YRangePadding : Math.Max(Math.Abs(rMax), 1) * YRangePadding;
                    yAxis.Minimum = rMin - pad;
                    yAxis.Maximum = rMax + pad;
                }
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
