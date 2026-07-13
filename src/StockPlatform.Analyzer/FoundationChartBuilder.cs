using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// "峰哥法"(2026-07-10 新规则:近N天涨停 + 涨停后持续放量)条件详情图所需的一切——2个面板:
/// 主图(K线+MA5/10/20，并在窗口内每个涨停日打红色三角、最近一次涨停日 L 单独标出) + 成交量
/// (量柱+5日均量线，标出涨停日 L 和涨停前5日均量基准线)。图上标的东西正好对应两条规则:哪天涨停、
/// 涨停后量能有没有持续放大。
/// </summary>
public class FoundationChartResult
{
    public PlotModel Main { get; init; } = new();
    public PlotModel Volume { get; init; } = new();
    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis VolumeDateAxis { get; init; } = null!;
    public LineAnnotation MainCrosshair { get; init; } = null!;
    public LineAnnotation VolumeCrosshair { get; init; } = null!;
    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();
    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Ma20 { get; init; } = Array.Empty<double>();
    public double[] Volumes { get; init; } = Array.Empty<double>();
    public double[] VolMa5 { get; init; } = Array.Empty<double>();
}

public static class FoundationChartBuilder
{
    public static FoundationChartResult Build(List<Bar> bars, string code, string name, int lookbackDays)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int last = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var volMa5 = TechnicalIndicators.SMA(volumes, 5);

        // 窗口内的涨停日(跟引擎同口径:收盘涨停 + LimitUpClassifier)，及最近一次 L。
        var limitUpDays = new List<int>();
        int windowStart = Math.Max(1, last - lookbackDays + 1);
        for (int t = windowStart; t <= last; t++)
        {
            double pct = closes[t - 1] > 0 ? (closes[t] - closes[t - 1]) / closes[t - 1] * 100 : 0;
            if (LimitUpClassifier.IsLimitUp(code, name, pct)) limitUpDays.Add(t);
        }
        int l = limitUpDays.Count > 0 ? limitUpDays[^1] : -1;

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "FndMainDay", visibleStart, visibleEnd, dayStep, monthStep);
        var (volDay, volMonth) = ChartBuilder.BuildDateAxes(bars, "FndVolDay", visibleStart, visibleEnd, dayStep, monthStep);

        // 主图。
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
            candles.Items.Add(new HighLowItem(i, bars[i].High, bars[i].Low, bars[i].Open, bars[i].Close));
        main.Series.Add(candles);
        ChartBuilder.AddLine(main, mainDay.Key, ma5, "MA5", OxyColors.Blue);
        ChartBuilder.AddLine(main, mainDay.Key, ma10, "MA10", OxyColors.Orange);
        ChartBuilder.AddLine(main, mainDay.Key, ma20, "MA20", OxyColors.Purple);

        // 窗口内所有涨停日：红三角标在当天最高价上方一点。
        if (limitUpDays.Count > 0)
        {
            var marks = new ScatterSeries { Title = "涨停日", MarkerType = MarkerType.Triangle, MarkerFill = OxyColors.Red, MarkerSize = 6, XAxisKey = mainDay.Key };
            foreach (var t in limitUpDays) marks.Points.Add(new ScatterPoint(t, bars[t].High));
            main.Series.Add(marks);
            // 最近一次涨停 L：一条竖线 + 文字，主图/量图都标。
            main.Annotations.Add(NewMarkerLine(mainDay.Key, l, "最近涨停"));
        }

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        // 成交量面板。
        ChartBuilder.HideAxisVisually(volDay);
        ChartBuilder.HideAxisVisually(volMonth);
        var volumeModel = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        volumeModel.Axes.Add(volDay);
        volumeModel.Axes.Add(volMonth);
        var volumeYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(volumeYAxis);
        volumeModel.Axes.Add(volumeYAxis);
        AddVolumeBars(volumeModel, volDay.Key, bars, volumes);
        ChartBuilder.AddLine(volumeModel, volDay.Key, volMa5, "5日均量", OxyColors.Orange);

        if (l != -1)
        {
            volumeModel.Annotations.Add(NewMarkerLine(volDay.Key, l, "最近涨停"));
            // 涨停前5日均量基准线（C2 的对比基准）。
            int b0 = Math.Max(0, l - 5);
            int cnt = l - b0;
            if (cnt > 0)
            {
                double baseline = volumes.Skip(b0).Take(cnt).Average();
                volumeModel.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Horizontal,
                    XAxisKey = volDay.Key,
                    Y = baseline,
                    Color = OxyColors.RoyalBlue,
                    LineStyle = LineStyle.Dash,
                    Text = "涨停前5日均量",
                    TextColor = OxyColors.RoyalBlue,
                });
            }
        }

        var volumeCrosshair = NewCrosshair(volDay.Key, last);
        volumeModel.Annotations.Add(volumeCrosshair);

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, volumeModel },
            new[] { mainDay, volDay },
            new[] { mainMonth, volMonth },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: new[]
            {
                (mainYAxis, ChartBuilder.YRangeFn(bars.Select(b => b.High).ToList(), bars.Select(b => b.Low).ToList(), ma5, ma10, ma20)),
                (volumeYAxis, ChartBuilder.YRangeFn(volumes, volMa5)),
            });

        return new FoundationChartResult
        {
            Main = main,
            Volume = volumeModel,
            MainDateAxis = mainDay,
            VolumeDateAxis = volDay,
            MainCrosshair = mainCrosshair,
            VolumeCrosshair = volumeCrosshair,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Volumes = volumes.ToArray(),
            VolMa5 = volMa5,
        };
    }

    private static LineAnnotation NewMarkerLine(string xAxisKey, int x, string text) => new()
    {
        Type = LineAnnotationType.Vertical,
        XAxisKey = xAxisKey,
        X = x,
        Color = OxyColors.Red,
        LineStyle = LineStyle.Dot,
        StrokeThickness = 1,
        Text = text,
        TextColor = OxyColors.Red,
    };

    private static LineAnnotation NewCrosshair(string xAxisKey, int last) => new()
    {
        Type = LineAnnotationType.Vertical,
        XAxisKey = xAxisKey,
        X = last,
        Color = OxyColors.DarkSlateGray,
        LineStyle = LineStyle.Dash,
        StrokeThickness = 1,
    };

    private static void AddVolumeBars(PlotModel model, string xAxisKey, List<Bar> bars, List<double> volumes)
    {
        var up = new StemSeries { Title = "成交量(涨)", Color = OxyColors.Red, StrokeThickness = 3, XAxisKey = xAxisKey };
        var down = new StemSeries { Title = "成交量(跌)", Color = OxyColors.Green, StrokeThickness = 3, XAxisKey = xAxisKey };
        for (int i = 0; i < bars.Count; i++)
            (bars[i].Close >= bars[i].Open ? up : down).Points.Add(new DataPoint(i, volumes[i]));
        model.Series.Add(up);
        model.Series.Add(down);
    }
}
