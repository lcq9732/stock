using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// "峰哥法"(2026-09-07 新规则:一根K线贯穿MA5/MA10/MA20 + 三线粘合 + 低位)条件详情图所需的一切
/// ——2个面板: 主图(K线+MA5/10/20，命中那根K线打竖线，并把命中日的"三线最高/最低"画成两条水平线，
/// 一眼能看出这根K线的最低价确实在三线之下、最高价在三线之上) + 成交量(量柱+5日均量线，只作参考:
/// 放量实测反而更差，没做成条件，见 FoundationAnalysisEngine 类注释)。
/// 命中日的判定跟引擎同一套判据(Low &lt; min三线 且 High &gt; max三线, 取最近一根)，所以图文一致。
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
    public static FoundationChartResult Build(List<Bar> bars, int lookbackDays)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int last = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var volMa5 = TechnicalIndicators.SMA(volumes, 5);

        // 命中日(跟 FoundationAnalysisEngine 同口径:最近一根 Low<三线最低 且 High>三线最高 的K线)。
        // 引擎的窗口起点是 PositionWindow-1(60日位置窗口要凑得齐)，这里照抄，免得图上标出一根
        // 引擎其实没算过的K线。
        int windowStart = Math.Max(FoundationAnalysisEngine.PositionWindow - 1, last - lookbackDays + 1);
        int l = -1;
        double hitLoMa = double.NaN, hitHiMa = double.NaN;
        for (int t = last; t >= windowStart; t--)
        {
            if (double.IsNaN(ma5[t]) || double.IsNaN(ma10[t]) || double.IsNaN(ma20[t])) continue;
            double loMa = Math.Min(ma5[t], Math.Min(ma10[t], ma20[t]));
            double hiMa = Math.Max(ma5[t], Math.Max(ma10[t], ma20[t]));
            if (bars[t].Low < loMa && bars[t].High > hiMa)
            {
                l = t;
                hitLoMa = loMa;
                hitHiMa = hiMa;
                break;
            }
        }

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
        var main = ChartTheme.Track(new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) });
        main.Axes.Add(mainDay);
        main.Axes.Add(mainMonth);
        var mainYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(mainYAxis);
        main.Axes.Add(mainYAxis);

        var candles = new CandleStickSeries
        {
            Title = "K线",
            XAxisKey = mainDay.Key,
            IncreasingColor = ChartTheme.Up,
            DecreasingColor = ChartTheme.Down,
            CandleWidth = 0.5,
            TrackerFormatString = "日期: {2}\n开盘: {3:F2}\n最高: {4:F2}\n最低: {5:F2}\n收盘: {6:F2}",
        };
        for (int i = 0; i < bars.Count; i++)
            candles.Items.Add(new HighLowItem(i, bars[i].High, bars[i].Low, bars[i].Open, bars[i].Close));
        main.Series.Add(candles);
        ChartBuilder.AddLine(main, mainDay.Key, ma5, "MA5", OxyColors.Blue);
        ChartBuilder.AddLine(main, mainDay.Key, ma10, "MA10", OxyColors.Orange);
        ChartBuilder.AddLine(main, mainDay.Key, ma20, "MA20", OxyColors.Purple);

        // 命中那根K线：竖线标出；再把命中日的"三线最高/最低"画成两条水平线——K线的最低价必须在
        // 下面那条之下、最高价在上面那条之上，这就是规则1本身。
        if (l != -1)
        {
            main.Annotations.Add(NewMarkerLine(mainDay.Key, l, "贯穿三线"));
            var marks = new ScatterSeries { Title = "命中K线", MarkerType = MarkerType.Triangle, MarkerFill = OxyColors.Red, MarkerSize = 6, XAxisKey = mainDay.Key };
            marks.Points.Add(new ScatterPoint(l, bars[l].High));
            main.Series.Add(marks);
            main.Annotations.Add(NewLevelLine(mainDay.Key, hitHiMa, $"命中日三线最高 {hitHiMa:F2}"));
            main.Annotations.Add(NewLevelLine(mainDay.Key, hitLoMa, $"命中日三线最低 {hitLoMa:F2}"));
        }

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        // 成交量面板。
        ChartBuilder.HideAxisVisually(volDay);
        ChartBuilder.HideAxisVisually(volMonth);
        var volumeModel = ChartTheme.Track(new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) });
        volumeModel.Axes.Add(volDay);
        volumeModel.Axes.Add(volMonth);
        var volumeYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(volumeYAxis);
        volumeModel.Axes.Add(volumeYAxis);
        AddVolumeBars(volumeModel, volDay.Key, bars, volumes);
        ChartBuilder.AddLine(volumeModel, volDay.Key, volMa5, "5日均量", OxyColors.Orange);

        // 命中日在量图上也标一条竖线。量能不参与判定（放量实测反而更差），只是让用户顺便看一眼。
        if (l != -1)
            volumeModel.Annotations.Add(NewMarkerLine(volDay.Key, l, "命中日"));

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

    /// <summary>一条水平参考线（命中日的三线最高/最低）——横线用 YAxis，所以不设 XAxisKey 会
    /// 落在默认Y轴上，这里显式带上 X 轴 key 是为了跟主图那套自定义日期轴对齐。</summary>
    private static LineAnnotation NewLevelLine(string xAxisKey, double y, string text) => new()
    {
        Type = LineAnnotationType.Horizontal,
        XAxisKey = xAxisKey,
        Y = y,
        Color = OxyColors.DarkOrange,
        LineStyle = LineStyle.Dash,
        StrokeThickness = 1,
        Text = text,
        TextColor = OxyColors.DarkOrange,
        TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
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
        var up = new StemSeries { Title = "成交量(涨)", Color = ChartTheme.Up, StrokeThickness = 3, XAxisKey = xAxisKey };
        var down = new StemSeries { Title = "成交量(跌)", Color = ChartTheme.Down, StrokeThickness = 3, XAxisKey = xAxisKey };
        for (int i = 0; i < bars.Count; i++)
            (bars[i].Close >= bars[i].Open ? up : down).Points.Add(new DataPoint(i, volumes[i]));
        model.Series.Add(up);
        model.Series.Add(down);
    }
}
