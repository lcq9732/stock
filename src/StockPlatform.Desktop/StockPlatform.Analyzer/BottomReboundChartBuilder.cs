using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// Everything BottomReboundDetailWindow needs beyond the two <see cref="PlotModel"/>s
/// themselves — mirrors ChartBuilder.ChartResult's shape (2 panels), but with the indicators
/// "耀哥法"（原名触底回升法） actually checks: MA5/MA10/MA20 (rule 2) plus the bottom day it found (rule 3) on
/// the main chart, and MACD (rule 1) on the sub-chart.
/// </summary>
public class BottomReboundChartResult
{
    public PlotModel Main { get; init; } = new();
    public PlotModel Macd { get; init; } = new();
    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis MacdDateAxis { get; init; } = null!;
    public LineAnnotation MainCrosshair { get; init; } = null!;
    public LineAnnotation MacdCrosshair { get; init; } = null!;
    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();
    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Ma20 { get; init; } = Array.Empty<double>();
    public double[] Dif { get; init; } = Array.Empty<double>();
    public double[] Dea { get; init; } = Array.Empty<double>();
    public double[] MacdHist { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Builds the OxyPlot models for "耀哥法"（原名触底回升法） result detail windows — 主图(K线+MA5+MA10+MA20+底部
/// 标记，对应规则2/3)、MACD副图(对应规则1，含近零阈值参考线). Uses
/// BottomReboundPatternDetector.Find so the marked bottom day is always the exact same one the
/// rule itself used — see the golden-cross chart fix this mirrors (doc/analysis-app-design.md
/// section 3.2.2's history: showing different indicators/reference values than the rule actually
/// checked is confusing, not just cosmetically wrong).
/// </summary>
public static class BottomReboundChartBuilder
{
    public static BottomReboundChartResult Build(List<Bar> bars, double difNearZeroThreshold)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        int last = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        var macdHist = new double[bars.Count];
        for (int i = 0; i < bars.Count; i++)
            macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;

        var pattern = BottomReboundPatternDetector.Find(bars, last);

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (initialDayStep, initialMonthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "BrMainDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);
        var (macdDay, macdMonth) = ChartBuilder.BuildDateAxes(bars, "BrMacdDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);

        // 主图：K线 + MA5/MA10/MA20（规则2）+ 底部标记（规则3——最近20日里最低价所在的那一天，
        // 跟BottomReboundAnalysisEngine用的是同一个BottomReboundPatternDetector，不会对不上）。
        // 不画横纵坐标，MACD副图保留坐标轴。
        ChartBuilder.HideAxisVisually(mainDay);
        ChartBuilder.HideAxisVisually(mainMonth);

        var main = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        main.Axes.Add(mainDay);
        main.Axes.Add(mainMonth);
        var mainYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(mainYAxis);
        main.Axes.Add(mainYAxis);

        var candles = new CandleStickSeries
        {
            Title = "K线",
            XAxisKey = mainDay.Key,
            IncreasingColor = OxyColors.Red,
            DecreasingColor = OxyColors.Green,
            CandleWidth = 0.5,
            TrackerFormatString = "日期: {2}\n开盘: {3:F2}\n最高: {4:F2}\n最低: {5:F2}\n收盘: {6:F2}",
        };
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            candles.Items.Add(new HighLowItem(i, b.High, b.Low, b.Open, b.Close));
        }
        main.Series.Add(candles);
        ChartBuilder.AddLine(main, mainDay.Key, ma5, "MA5", OxyColors.Blue);
        ChartBuilder.AddLine(main, mainDay.Key, ma10, "MA10", OxyColors.Orange);
        ChartBuilder.AddLine(main, mainDay.Key, ma20, "MA20", OxyColors.Green);

        main.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = mainDay.Key,
            X = pattern.BottomIndex,
            Color = OxyColors.Red,
            LineStyle = LineStyle.Dash,
            Text = $"底部={bars[pattern.BottomIndex].Low:F2}",
        });
        if (pattern.HasUpStreak)
        {
            main.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                XAxisKey = mainDay.Key,
                X = pattern.BestStreakEnd,
                Color = OxyColors.Green,
                LineStyle = LineStyle.Dash,
                Text = $"连续上涨{pattern.BestStreakLen}天",
            });
        }

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        // MACD副图（规则1）——虚线标出近零阈值的下边界，直观看到DIF是否在允许范围内。
        ChartBuilder.HideAxisVisually(macdDay);
        ChartBuilder.HideAxisVisually(macdMonth);
        var macd = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        macd.Axes.Add(macdDay);
        macd.Axes.Add(macdMonth);
        var macdYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(macdYAxis);
        macd.Axes.Add(macdYAxis);
        ChartBuilder.AddHistogram(macd, macdDay.Key, macdHist);
        ChartBuilder.AddLine(macd, macdDay.Key, dif, "DIF（快线）", OxyColors.Blue);
        ChartBuilder.AddLine(macd, macdDay.Key, dea, "DEA（慢线）", OxyColors.Orange);
        macd.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = macdDay.Key,
            Y = 0,
            Color = OxyColors.Black,
            LineStyle = LineStyle.Solid,
            Text = "零轴",
        });
        if (difNearZeroThreshold > 0)
        {
            macd.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                XAxisKey = macdDay.Key,
                Y = -difNearZeroThreshold,
                Color = OxyColors.Gray,
                LineStyle = LineStyle.Dot,
                Text = $"DIF阈值=-{difNearZeroThreshold:F3}",
            });
        }
        var macdCrosshair = NewCrosshair(macdDay.Key, last);
        macd.Annotations.Add(macdCrosshair);

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, macd },
            new[] { mainDay, macdDay },
            new[] { mainMonth, macdMonth },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: new[]
            {
                (mainYAxis, ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20)),
                (macdYAxis, ChartBuilder.YRangeFn(dif, dea, macdHist)),
            });

        return new BottomReboundChartResult
        {
            Main = main,
            Macd = macd,
            MainDateAxis = mainDay,
            MacdDateAxis = macdDay,
            MainCrosshair = mainCrosshair,
            MacdCrosshair = macdCrosshair,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Dif = dif,
            Dea = dea,
            MacdHist = macdHist,
        };
    }

    private static LineAnnotation NewCrosshair(string xAxisKey, int last) => new()
    {
        Type = LineAnnotationType.Vertical,
        XAxisKey = xAxisKey,
        X = last,
        Color = OxyColors.DarkSlateGray,
        LineStyle = LineStyle.Dash,
        StrokeThickness = 1,
    };
}
