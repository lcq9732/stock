using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// Everything MidCapPullbackDetailWindow needs — 3 panels, one per bar granularity the 10
/// conditions actually check (day/week/month), each with its OWN independent bar-index axis and
/// pan/zoom (they can't share one axis like the other charts' panels do — a week bar and a day bar
/// at the same index number aren't the same calendar day, so there's nothing meaningful to keep in
/// sync between them; see doc/analysis-app-design.md section 3.2.4's chart notes).
/// </summary>
public class MidCapPullbackChartResult
{
    public PlotModel Main { get; init; } = new();       // 日线K线 + MA15 + 涨停标记（规则5/8/9/10）
    public PlotModel WeekMacd { get; init; } = new();   // 周线MACD（规则7）
    public PlotModel MonthMacd { get; init; } = new();  // 月线MACD（规则6）

    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis WeekMacdDateAxis { get; init; } = null!;
    public LinearAxis MonthMacdDateAxis { get; init; } = null!;

    public LineAnnotation MainCrosshair { get; init; } = null!;
    public LineAnnotation WeekMacdCrosshair { get; init; } = null!;
    public LineAnnotation MonthMacdCrosshair { get; init; } = null!;

    /// <summary>Each panel corrects its own label density independently — they're not one synced
    /// group like ChartBuilder's/GoldenCrossChartBuilder's panels (different granularities have
    /// nothing to zoom in sync with each other).</summary>
    public Action<double> UpdateMainWidth { get; init; } = _ => { };
    public Action<double> UpdateWeekWidth { get; init; } = _ => { };
    public Action<double> UpdateMonthWidth { get; init; } = _ => { };

    public List<Bar> DayBars { get; init; } = new();
    public List<Bar> WeekBars { get; init; } = new();
    public List<Bar> MonthBars { get; init; } = new();

    public double[] Ma15 { get; init; } = Array.Empty<double>();
    public double[] WeekDif { get; init; } = Array.Empty<double>();
    public double[] WeekDea { get; init; } = Array.Empty<double>();
    public double[] WeekMacdHist { get; init; } = Array.Empty<double>();
    public double[] MonthDif { get; init; } = Array.Empty<double>();
    public double[] MonthDea { get; init; } = Array.Empty<double>();
    public double[] MonthMacdHist { get; init; } = Array.Empty<double>();
}

public static class MidCapPullbackChartBuilder
{
    public static MidCapPullbackChartResult Build(List<Bar> dayBars, List<Bar> weekBars, List<Bar> monthBars, string code, string name)
    {
        var dayCloses = dayBars.Select(b => b.Close).ToList();
        int lastDay = dayBars.Count - 1;
        var ma15 = TechnicalIndicators.SMA(dayCloses, 15);

        var weekCloses = weekBars.Select(b => b.Close).ToList();
        var (weekDif, weekDea) = TechnicalIndicators.MACD(weekCloses);
        var weekMacdHist = new double[weekBars.Count];
        for (int i = 0; i < weekBars.Count; i++)
            weekMacdHist[i] = double.IsNaN(weekDif[i]) || double.IsNaN(weekDea[i]) ? double.NaN : (weekDif[i] - weekDea[i]) * 2;

        var monthCloses = monthBars.Select(b => b.Close).ToList();
        var (monthDif, monthDea) = TechnicalIndicators.MACD(monthCloses);
        var monthMacdHist = new double[monthBars.Count];
        for (int i = 0; i < monthBars.Count; i++)
            monthMacdHist[i] = double.IsNaN(monthDif[i]) || double.IsNaN(monthDea[i]) ? double.NaN : (monthDif[i] - monthDea[i]) * 2;

        var (mainPlot, mainDayAxis, mainCrosshair, updateMainWidth) = BuildMainPanel(dayBars, dayCloses, ma15, code, name);
        var (weekPlot, weekDayAxis, weekCrosshair, updateWeekWidth) = BuildMacdPanel(weekBars, weekDif, weekDea, weekMacdHist, "WkMacd");
        var (monthPlot, monthDayAxis, monthCrosshair, updateMonthWidth) = BuildMacdPanel(monthBars, monthDif, monthDea, monthMacdHist, "MoMacd");

        return new MidCapPullbackChartResult
        {
            Main = mainPlot,
            WeekMacd = weekPlot,
            MonthMacd = monthPlot,
            MainDateAxis = mainDayAxis,
            WeekMacdDateAxis = weekDayAxis,
            MonthMacdDateAxis = monthDayAxis,
            MainCrosshair = mainCrosshair,
            WeekMacdCrosshair = weekCrosshair,
            MonthMacdCrosshair = monthCrosshair,
            UpdateMainWidth = updateMainWidth,
            UpdateWeekWidth = updateWeekWidth,
            UpdateMonthWidth = updateMonthWidth,
            DayBars = dayBars,
            WeekBars = weekBars,
            MonthBars = monthBars,
            Ma15 = ma15,
            WeekDif = weekDif,
            WeekDea = weekDea,
            WeekMacdHist = weekMacdHist,
            MonthDif = monthDif,
            MonthDea = monthDea,
            MonthMacdHist = monthMacdHist,
        };
    }

    private static (PlotModel, LinearAxis, LineAnnotation, Action<double>) BuildMainPanel(
        List<Bar> bars, List<double> closes, double[] ma15, string code, string name)
    {
        int last = bars.Count - 1;
        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);
        var (dayAxis, monthAxis) = ChartBuilder.BuildDateAxes(bars, "McMainDay", visibleStart, visibleEnd, dayStep, monthStep);

        // 日线主图不画横纵坐标，周线/月线MACD面板保留坐标轴。
        ChartBuilder.HideAxisVisually(dayAxis);
        ChartBuilder.HideAxisVisually(monthAxis);

        var main = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        main.Axes.Add(dayAxis);
        main.Axes.Add(monthAxis);
        var mainYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(mainYAxis);
        main.Axes.Add(mainYAxis);

        var candles = new CandleStickSeries
        {
            Title = "K线",
            XAxisKey = dayAxis.Key,
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
        ChartBuilder.AddLine(main, dayAxis.Key, ma15, "MA15", OxyColors.Orange);

        // 规则5——最近15个交易日里按收盘涨停统计到的那几天，在最高价上方标个小红点，
        // 直观看到"这15天里涨停了几次"，不用去数Basis文字里的数字。
        const int limitUpWindowDays = 15;
        int windowStart = Math.Max(1, last - limitUpWindowDays + 1);
        var limitUpMarkers = new ScatterSeries
        {
            Title = "涨停",
            MarkerType = MarkerType.Triangle,
            MarkerFill = OxyColors.Red,
            MarkerSize = 6,
            XAxisKey = dayAxis.Key,
        };
        for (int t = windowStart; t <= last; t++)
        {
            double pct = (closes[t] - closes[t - 1]) / closes[t - 1] * 100;
            if (LimitUpClassifier.IsLimitUp(code, name, pct))
                limitUpMarkers.Points.Add(new ScatterPoint(t, bars[t].High * 1.02));
        }
        main.Series.Add(limitUpMarkers);

        var crosshair = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = dayAxis.Key,
            X = last,
            Color = OxyColors.DarkSlateGray,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        main.Annotations.Add(crosshair);
        ChartBuilder.AddHighLowAnnotations(main, dayAxis.Key, bars, (int)Math.Round(visibleStart), last);

        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var updateWidth = ChartAxisSync.Wire(
            new[] { main }, new[] { dayAxis }, new[] { monthAxis },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: new[] { (mainYAxis, ChartBuilder.YRangeFn(highs, lows, ma15)) });

        return (main, dayAxis, crosshair, updateWidth);
    }

    private static (PlotModel, LinearAxis, LineAnnotation, Action<double>) BuildMacdPanel(
        List<Bar> bars, double[] dif, double[] dea, double[] macdHist, string keyPrefix)
    {
        int last = bars.Count - 1;
        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);
        var (dayAxis, monthAxis) = ChartBuilder.BuildDateAxes(bars, keyPrefix, visibleStart, visibleEnd, dayStep, monthStep);
        ChartBuilder.HideAxisVisually(dayAxis);
        ChartBuilder.HideAxisVisually(monthAxis);

        var model = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        model.Axes.Add(dayAxis);
        model.Axes.Add(monthAxis);
        var macdYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(macdYAxis);
        model.Axes.Add(macdYAxis);
        ChartBuilder.AddHistogram(model, dayAxis.Key, macdHist);
        ChartBuilder.AddLine(model, dayAxis.Key, dif, "DIF（快线）", OxyColors.Blue);
        ChartBuilder.AddLine(model, dayAxis.Key, dea, "DEA（慢线）", OxyColors.Orange);
        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = dayAxis.Key,
            Y = 0,
            Color = OxyColors.Black,
            LineStyle = LineStyle.Solid,
            Text = "零轴",
        });

        var crosshair = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = dayAxis.Key,
            X = last,
            Color = OxyColors.DarkSlateGray,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        model.Annotations.Add(crosshair);

        var updateWidth = ChartAxisSync.Wire(
            new[] { model }, new[] { dayAxis }, new[] { monthAxis },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            yAxisRanges: new[] { (macdYAxis, ChartBuilder.YRangeFn(dif, dea, macdHist)) });

        return (model, dayAxis, crosshair, updateWidth);
    }

}
