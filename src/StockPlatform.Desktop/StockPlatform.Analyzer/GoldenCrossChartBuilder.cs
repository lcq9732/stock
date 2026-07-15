using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// Everything GoldenCrossDetailWindow needs beyond the five <see cref="PlotModel"/>s themselves —
/// mirrors ChartBuilder.ChartResult's shape but with 5 panels instead of 2, one per group of
/// indicators the "金叉法" 7 conditions actually use (see GoldenCrossAnalysisEngine).
/// </summary>
public class GoldenCrossChartResult
{
    public PlotModel Main { get; init; } = new();
    public PlotModel Macd { get; init; } = new();
    public PlotModel Kdj { get; init; } = new();
    public PlotModel Rsi { get; init; } = new();
    public PlotModel Volume { get; init; } = new();

    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis MacdDateAxis { get; init; } = null!;
    public LinearAxis KdjDateAxis { get; init; } = null!;
    public LinearAxis RsiDateAxis { get; init; } = null!;
    public LinearAxis VolumeDateAxis { get; init; } = null!;

    public LineAnnotation MainCrosshair { get; init; } = null!;
    public LineAnnotation MacdCrosshair { get; init; } = null!;
    public LineAnnotation KdjCrosshair { get; init; } = null!;
    public LineAnnotation RsiCrosshair { get; init; } = null!;
    public LineAnnotation VolumeCrosshair { get; init; } = null!;

    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();

    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Dif { get; init; } = Array.Empty<double>();
    public double[] Dea { get; init; } = Array.Empty<double>();
    public double[] MacdHist { get; init; } = Array.Empty<double>();
    public double[] K { get; init; } = Array.Empty<double>();
    public double[] D { get; init; } = Array.Empty<double>();
    public double[] RsiValues { get; init; } = Array.Empty<double>();
    public double[] Volumes { get; init; } = Array.Empty<double>();
    public double[] VolMa5 { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Builds the OxyPlot models for "金叉法" result detail windows — 5 panels, one per group of
/// indicators its 7 conditions check (see GoldenCrossAnalysisEngine): 主图(K线+MA5+MA10+20日最高价
/// 压力线，对应条件1/2/7)、MACD(条件3)、KDJ(条件4)、RSI(条件5)、成交量(条件6). This is a
/// deliberately separate builder from ChartBuilder (峰哥法, BOLL+MACD+breakout line) rather than
/// one parametrized builder — the two methods' indicator sets don't overlap enough to share a
/// single code path without a pile of conditionals, but they DO share the tricky axis/pan/zoom/
/// label-density plumbing via ChartAxisSync and the BuildDateAxes/AddLine/AddHistogram helpers.
/// </summary>
public static class GoldenCrossChartBuilder
{
    // 跟 GoldenCrossAnalysisEngine 里"股价突破最近20日平台或压力位"用的是同一个窗口和同一个价格
    // 口径（最高价，不是收盘价）——这张图存在的意义就是让图上的数字和条件文字对得上。
    private const int ResistanceLookback = 20;

    public static GoldenCrossChartResult Build(List<Bar> bars)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int last = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        var macdHist = new double[bars.Count];
        for (int i = 0; i < bars.Count; i++)
            macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;
        var (k, d, _) = TechnicalIndicators.KDJ(closes, highs, lows);
        var rsi = TechnicalIndicators.RSI(closes);
        var volMa5 = TechnicalIndicators.SMA(volumes, 5);

        double resistance = double.MinValue;
        for (int t = last - ResistanceLookback; t < last; t++) resistance = Math.Max(resistance, highs[t]);

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (initialDayStep, initialMonthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "GcMainDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);
        var (macdDay, macdMonth) = ChartBuilder.BuildDateAxes(bars, "GcMacdDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);
        var (kdjDay, kdjMonth) = ChartBuilder.BuildDateAxes(bars, "GcKdjDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);
        var (rsiDay, rsiMonth) = ChartBuilder.BuildDateAxes(bars, "GcRsiDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);
        var (volDay, volMonth) = ChartBuilder.BuildDateAxes(bars, "GcVolDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);

        // 主图：K线 + MA5 + MA10（条件1“MA5上穿MA10”、条件2“MA10拐头向上”）+ 20日最高价压力线
        // （条件7，注意用的是最高价不是收盘价——跟峰哥法那条基于收盘价的参考线口径不一样）。
        // 不画横纵坐标（同 ChartBuilder 的做法），其它4个面板保留坐标轴。
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

        main.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = mainDay.Key,
            Y = resistance,
            MinimumX = last - ResistanceLookback,
            MaximumX = last,
            Color = OxyColors.Red,
            LineStyle = LineStyle.Dash,
            Text = $"前{ResistanceLookback}日最高价={resistance:F2}",
        });

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        // MACD副图（条件3“MACD金叉”）——跟峰哥法那张结构完全一样，直接复用AddLine/AddHistogram。
        // 4个副图现在都不画横纵坐标（跟主图一样），数值靠表头悬浮信息；IsAxisVisible只关渲染，
        // 不影响IsPanEnabled/IsZoomEnabled，拖动缩放不受影响。
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
        var macdCrosshair = NewCrosshair(macdDay.Key, last);
        macd.Annotations.Add(macdCrosshair);

        // KDJ副图（条件4“KDJ在20~50区域金叉”）——20/50两条参考线标出条件要求的区间。
        ChartBuilder.HideAxisVisually(kdjDay);
        ChartBuilder.HideAxisVisually(kdjMonth);
        var kdj = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        kdj.Axes.Add(kdjDay);
        kdj.Axes.Add(kdjMonth);
        var kdjYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true, Minimum = 0, Maximum = 100 };
        ChartBuilder.HideAxisVisually(kdjYAxis);
        kdj.Axes.Add(kdjYAxis);
        ChartBuilder.AddLine(kdj, kdjDay.Key, k, "K", OxyColors.Blue);
        ChartBuilder.AddLine(kdj, kdjDay.Key, d, "D", OxyColors.Orange);
        kdj.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = kdjDay.Key, Y = 20, Color = OxyColors.Gray, LineStyle = LineStyle.Dot, Text = "20" });
        kdj.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = kdjDay.Key, Y = 50, Color = OxyColors.Gray, LineStyle = LineStyle.Dot, Text = "50" });
        var kdjCrosshair = NewCrosshair(kdjDay.Key, last);
        kdj.Annotations.Add(kdjCrosshair);

        // RSI副图（条件5“RSI从30附近向上突破50”）——50是突破线，35是"近30附近"的粗略参考线
        // （条件本身用的判定阈值是≤35，见GoldenCrossAnalysisEngine.rsiWasNear30）。
        ChartBuilder.HideAxisVisually(rsiDay);
        ChartBuilder.HideAxisVisually(rsiMonth);
        var rsiModel = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        rsiModel.Axes.Add(rsiDay);
        rsiModel.Axes.Add(rsiMonth);
        var rsiYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true, Minimum = 0, Maximum = 100 };
        ChartBuilder.HideAxisVisually(rsiYAxis);
        rsiModel.Axes.Add(rsiYAxis);
        ChartBuilder.AddLine(rsiModel, rsiDay.Key, rsi, "RSI", OxyColors.Purple);
        rsiModel.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = rsiDay.Key, Y = 50, Color = OxyColors.Black, LineStyle = LineStyle.Solid, Text = "50" });
        rsiModel.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = rsiDay.Key, Y = 35, Color = OxyColors.Gray, LineStyle = LineStyle.Dot, Text = "35" });
        var rsiCrosshair = NewCrosshair(rsiDay.Key, last);
        rsiModel.Annotations.Add(rsiCrosshair);

        // 成交量副图（条件6“成交量≥5日均量的1.5倍”）。
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

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, macd, kdj, rsiModel, volumeModel },
            new[] { mainDay, macdDay, kdjDay, rsiDay, volDay },
            new[] { mainMonth, macdMonth, kdjMonth, rsiMonth, volMonth },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            // KDJ/RSI不在这里——它们的Y轴是固定的0~100语义区间，不该跟着可见K线数据自适应缩放。
            yAxisRanges: new[]
            {
                (mainYAxis, ChartBuilder.YRangeFn(highs, lows)),
                (macdYAxis, ChartBuilder.YRangeFn(dif, dea, macdHist)),
                (volumeYAxis, ChartBuilder.YRangeFn(volumes, volMa5)),
            });

        return new GoldenCrossChartResult
        {
            Main = main,
            Macd = macd,
            Kdj = kdj,
            Rsi = rsiModel,
            Volume = volumeModel,
            MainDateAxis = mainDay,
            MacdDateAxis = macdDay,
            KdjDateAxis = kdjDay,
            RsiDateAxis = rsiDay,
            VolumeDateAxis = volDay,
            MainCrosshair = mainCrosshair,
            MacdCrosshair = macdCrosshair,
            KdjCrosshair = kdjCrosshair,
            RsiCrosshair = rsiCrosshair,
            VolumeCrosshair = volumeCrosshair,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Dif = dif,
            Dea = dea,
            MacdHist = macdHist,
            K = k,
            D = d,
            RsiValues = rsi,
            Volumes = volumes.ToArray(),
            VolMa5 = volMa5,
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

    /// <summary>涨的那天用红柱、跌的那天用绿柱（跟K线颜色约定一致），跟ChartBuilder.AddHistogram
    /// 用的是同一种"两条StemSeries各画各的"手法，只是分类依据换成了涨跌而不是正负。</summary>
    private static void AddVolumeBars(PlotModel model, string xAxisKey, List<Bar> bars, List<double> volumes)
    {
        var up = new StemSeries { Title = "成交量(涨)", Color = OxyColors.Red, StrokeThickness = 3, XAxisKey = xAxisKey };
        var down = new StemSeries { Title = "成交量(跌)", Color = OxyColors.Green, StrokeThickness = 3, XAxisKey = xAxisKey };
        for (int i = 0; i < bars.Count; i++)
        {
            var point = new DataPoint(i, volumes[i]);
            (bars[i].Close >= bars[i].Open ? up : down).Points.Add(point);
        }
        model.Series.Add(up);
        model.Series.Add(down);
    }
}
