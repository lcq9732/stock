using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// "阶梯低点法" 条件详情图所需的一切——3个上下堆叠的面板：主图(K线+MA5/10/20 + 把
/// <see cref="RisingLowsDetector"/> 找到的锚点画上去：阶段顶/金叉/反弹高点P/回踩V + 三个依次抬高的
/// 低点连线) + MACD(DIF/DEA/柱+零轴，标出金叉) + 成交量(量柱+5日均量，标出回踩缩量与今日放量)。
/// 锚点用的是引擎判定时同一份 <see cref="RisingLowsDetector.Anchors"/>，图和规则不会各算各的。
/// </summary>
public class RisingLowsChartResult
{
    public PlotModel Main { get; init; } = new();
    public PlotModel Macd { get; init; } = new();
    public PlotModel Volume { get; init; } = new();

    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis MacdDateAxis { get; init; } = null!;
    public LinearAxis VolumeDateAxis { get; init; } = null!;

    public LineAnnotation MainCrosshair { get; init; } = null!;
    public LineAnnotation MacdCrosshair { get; init; } = null!;
    public LineAnnotation VolumeCrosshair { get; init; } = null!;

    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();

    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Ma20 { get; init; } = Array.Empty<double>();
    public double[] Dif { get; init; } = Array.Empty<double>();
    public double[] Dea { get; init; } = Array.Empty<double>();
    public double[] MacdHist { get; init; } = Array.Empty<double>();
    public double[] Volumes { get; init; } = Array.Empty<double>();
    public double[] VolMa5 { get; init; } = Array.Empty<double>();
}

public static class RisingLowsChartBuilder
{
    public static RisingLowsChartResult Build(List<Bar> bars)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int last = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        var macdHist = new double[bars.Count];
        for (int i = 0; i < bars.Count; i++)
            macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;
        var volMa5 = TechnicalIndicators.SMA(volumes, 5);

        var anchors = RisingLowsDetector.Find(bars);

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "RlMainDay", visibleStart, visibleEnd, dayStep, monthStep);
        var (macdDay, macdMonth) = ChartBuilder.BuildDateAxes(bars, "RlMacdDay", visibleStart, visibleEnd, dayStep, monthStep);
        var (volDay, volMonth) = ChartBuilder.BuildDateAxes(bars, "RlVolDay", visibleStart, visibleEnd, dayStep, monthStep);

        // 主图：K线 + MA5/10/20 + 锚点。
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

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        // MACD 面板。
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
        macd.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = macdDay.Key, Y = 0, Color = OxyColors.Black, LineStyle = LineStyle.Solid, Text = "零轴" });
        var macdCrosshair = NewCrosshair(macdDay.Key, last);
        macd.Annotations.Add(macdCrosshair);

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
        var volumeCrosshair = NewCrosshair(volDay.Key, last);
        volumeModel.Annotations.Add(volumeCrosshair);

        // 把锚点画到三张图上（阶段顶/金叉/P/回踩V + 三个抬高的低点）。
        if (anchors is { } a && a.CrossFound)
            OverlayAnchors(main, macd, volumeModel, mainDay.Key, macdDay.Key, volDay.Key, bars, dif, dea, a);

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, macd, volumeModel },
            new[] { mainDay, macdDay, volDay },
            new[] { mainMonth, macdMonth, volMonth },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: new[]
            {
                (mainYAxis, ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20)),
                (macdYAxis, ChartBuilder.YRangeFn(dif, dea, macdHist)),
                (volumeYAxis, ChartBuilder.YRangeFn(volumes, volMa5)),
            });

        return new RisingLowsChartResult
        {
            Main = main,
            Macd = macd,
            Volume = volumeModel,
            MainDateAxis = mainDay,
            MacdDateAxis = macdDay,
            VolumeDateAxis = volDay,
            MainCrosshair = mainCrosshair,
            MacdCrosshair = macdCrosshair,
            VolumeCrosshair = volumeCrosshair,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Dif = dif,
            Dea = dea,
            MacdHist = macdHist,
            Volumes = volumes.ToArray(),
            VolMa5 = volMa5,
        };
    }

    private static void OverlayAnchors(
        PlotModel main, PlotModel macd, PlotModel vol, string mainKey, string macdKey, string volKey,
        List<Bar> bars, double[] dif, double[] dea, RisingLowsDetector.Anchors a)
    {
        // 三个依次抬高的低点：起点低(前50日低) → 金叉低 → 回踩V低，连成一条向上的虚线。
        var risingLows = new LineSeries { Title = "抬高的低点", Color = OxyColor.FromRgb(0x1D, 0x9E, 0x75), StrokeThickness = 1.5, LineStyle = LineStyle.Dash, XAxisKey = mainKey };
        risingLows.Points.Add(new DataPoint(a.Prior50LowIdx, bars[a.Prior50LowIdx].Low));
        risingLows.Points.Add(new DataPoint(a.NearLowIdx, bars[a.NearLowIdx].Low));
        risingLows.Points.Add(new DataPoint(a.VIdx, bars[a.VIdx].Low));
        main.Series.Add(risingLows);

        var lowMarks = new ScatterSeries { Title = "抬高的低点", MarkerType = MarkerType.Circle, MarkerFill = OxyColor.FromRgb(0x1D, 0x9E, 0x75), MarkerSize = 5, XAxisKey = mainKey };
        foreach (var idx in new[] { a.Prior50LowIdx, a.NearLowIdx, a.VIdx })
            lowMarks.Points.Add(new ScatterPoint(idx, bars[idx].Low));
        main.Series.Add(lowMarks);

        var stageHigh = new ScatterSeries { Title = "阶段顶", MarkerType = MarkerType.Triangle, MarkerFill = OxyColors.Red, MarkerSize = 6, XAxisKey = mainKey };
        stageHigh.Points.Add(new ScatterPoint(a.StageHighIdx, bars[a.StageHighIdx].High));
        main.Series.Add(stageHigh);

        var pMark = new ScatterSeries { Title = "反弹高点P", MarkerType = MarkerType.Diamond, MarkerFill = OxyColors.DarkOrange, MarkerSize = 6, XAxisKey = mainKey };
        pMark.Points.Add(new ScatterPoint(a.PIdx, bars[a.PIdx].High));
        main.Series.Add(pMark);

        AddText(main, mainKey, a.StageHighIdx, bars[a.StageHighIdx].High, "阶段顶", OxyColors.Red, 12);
        AddText(main, mainKey, a.PIdx, bars[a.PIdx].High, "P", OxyColors.DarkOrange, -14);
        AddText(main, mainKey, a.NearLowIdx, bars[a.NearLowIdx].Low, "金叉低", OxyColor.FromRgb(0x0F, 0x6E, 0x56), -16);
        AddText(main, mainKey, a.VIdx, bars[a.VIdx].Low, "回踩V", OxyColor.FromRgb(0x0F, 0x6E, 0x56), -16);

        // 金叉位：主图 + MACD 图各画一条竖线标出。
        foreach (var (model, key) in new[] { (main, mainKey), (macd, macdKey) })
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                XAxisKey = key,
                X = a.CrossIdx,
                Color = OxyColors.RoyalBlue,
                LineStyle = LineStyle.Dot,
                StrokeThickness = 1,
                Text = "金叉",
                TextColor = OxyColors.RoyalBlue,
            });
        var crossMark = new ScatterSeries { Title = "金叉", MarkerType = MarkerType.Cross, MarkerStroke = OxyColors.RoyalBlue, MarkerStrokeThickness = 2, MarkerSize = 5, XAxisKey = macdKey };
        crossMark.Points.Add(new ScatterPoint(a.CrossIdx, dif[a.CrossIdx]));
        macd.Series.Add(crossMark);

        // 成交量：标出回踩V日(缩量)与今天(放量)。
        var volMarks = new ScatterSeries { Title = "回踩缩量/今日放量", MarkerType = MarkerType.Circle, MarkerStroke = OxyColors.RoyalBlue, MarkerStrokeThickness = 2, MarkerSize = 5, XAxisKey = volKey };
        volMarks.Points.Add(new ScatterPoint(a.VIdx, bars[a.VIdx].Volume));
        volMarks.Points.Add(new ScatterPoint(a.Today, bars[a.Today].Volume));
        vol.Series.Add(volMarks);
    }

    private static void AddText(PlotModel model, string xAxisKey, int idx, double y, string text, OxyColor color, double dyPixels)
    {
        model.Annotations.Add(new TextAnnotation
        {
            XAxisKey = xAxisKey,
            TextPosition = new DataPoint(idx, y),
            Text = text,
            TextColor = color,
            FontSize = 11,
            StrokeThickness = 0,
            Offset = new ScreenVector(0, dyPixels),
        });
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
