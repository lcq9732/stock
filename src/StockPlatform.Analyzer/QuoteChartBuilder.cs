using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>行情详情两个副图可选的指标类型——跟"条件详情"那几个窗口不一样，这里的副图内容不是
/// 固定的，用户在界面上手工切换，所以每种指标都要能独立算出一份够用的 PlotModel + 悬浮信息文字，
/// 不依赖是哪个方法选中了这只股票。</summary>
public enum QuoteSubIndicator { Volume, Macd, Kdj, Rsi }

/// <summary>
/// Everything QuoteDetailWindow needs——主图(K线+MA5/MA10/MA20，固定) + 两个可独立切换指标类型的
/// 副图（默认成交量+MACD，用户可各自切换成KDJ/RSI/另一个）。三个面板共用同一套日期轴，横向
/// 拖动/缩放联动，切换副图指标或者切换K线粒度（日K/周K/月K）都会用新的 bars 整体重新 Build 一次
/// ——不做增量更新，简单、不容易漏状态。
/// </summary>
public class QuoteChartResult
{
    public PlotModel Main { get; init; } = new();
    public LinearAxis MainDateAxis { get; init; } = null!;
    public LineAnnotation MainCrosshair { get; init; } = null!;

    public PlotModel Sub1 { get; init; } = new();
    public LinearAxis Sub1DateAxis { get; init; } = null!;
    public LineAnnotation Sub1Crosshair { get; init; } = null!;
    /// <summary>给定bar下标，返回这个副图当前指标该显示的悬浮文字——窗口不需要知道副图具体是
    /// 哪种指标，只管调用这个函数。</summary>
    public Func<int, string> Sub1FormatInfo { get; init; } = _ => "";

    public PlotModel Sub2 { get; init; } = new();
    public LinearAxis Sub2DateAxis { get; init; } = null!;
    public LineAnnotation Sub2Crosshair { get; init; } = null!;
    public Func<int, string> Sub2FormatInfo { get; init; } = _ => "";

    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();
    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Ma20 { get; init; } = Array.Empty<double>();
    public double[] Ma30 { get; init; } = Array.Empty<double>();
}

public static class QuoteChartBuilder
{
    // 暴露成公开常量，方便 QuoteDetailWindow.xaml.cs 给"MA5:158.49↑"这类文字上色时跟图上的线对上，
    // 不用在两个文件里各写一遍颜色。
    public static readonly OxyColor Ma5Color = OxyColors.Blue;
    public static readonly OxyColor Ma10Color = OxyColors.Orange;
    public static readonly OxyColor Ma20Color = OxyColors.Purple;
    public static readonly OxyColor Ma30Color = OxyColor.FromRgb(139, 0, 0); // DarkRed

    public static string IndicatorLabel(QuoteSubIndicator kind) => kind switch
    {
        QuoteSubIndicator.Volume => "成交量",
        QuoteSubIndicator.Macd => "MACD",
        QuoteSubIndicator.Kdj => "KDJ",
        QuoteSubIndicator.Rsi => "RSI",
        _ => kind.ToString(),
    };

    /// <summary>每种指标的图例——(文字, 颜色)，跟 BuildSub 里画的线/柱颜色手动对应，供窗口表头
    /// 动态生成图例用（这两个副图内容会变，没法像其它窗口那样把图例写死在XAML里）。</summary>
    public static (string Label, OxyColor Color)[] LegendFor(QuoteSubIndicator kind) => kind switch
    {
        QuoteSubIndicator.Volume => new[] { ("成交量(涨)", OxyColors.Red), ("成交量(跌)", OxyColors.Green) },
        QuoteSubIndicator.Macd => new[] { ("MACD柱(正)", OxyColors.Red), ("MACD柱(负)", OxyColors.Green), ("DIF（快线）", OxyColors.Blue), ("DEA（慢线）", OxyColors.Orange) },
        QuoteSubIndicator.Kdj => new[] { ("K", OxyColors.Blue), ("D", OxyColors.Orange) },
        QuoteSubIndicator.Rsi => new[] { ("RSI", OxyColors.Purple) },
        _ => Array.Empty<(string, OxyColor)>(),
    };

    public static QuoteChartResult Build(List<Bar> bars, QuoteSubIndicator sub1Kind, QuoteSubIndicator sub2Kind)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var ma30 = TechnicalIndicators.SMA(closes, 30);
        int last = bars.Count - 1;

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "QuoteMainDay", visibleStart, visibleEnd, dayStep, monthStep);

        // 主K线图不画横纵坐标（价格靠最高/最低价标注和表头，日期靠表头），两个副图保留坐标轴。
        ChartBuilder.HideAxisVisually(mainDay);
        ChartBuilder.HideAxisVisually(mainMonth);

        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();

        var main = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        main.Axes.Add(mainDay);
        main.Axes.Add(mainMonth);
        var mainYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        ChartBuilder.HideAxisVisually(mainYAxis);
        main.Axes.Add(mainYAxis);

        // 国内看盘习惯：涨红跌绿。
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
        ChartBuilder.AddLine(main, mainDay.Key, ma5, "MA5", Ma5Color);
        ChartBuilder.AddLine(main, mainDay.Key, ma10, "MA10", Ma10Color);
        ChartBuilder.AddLine(main, mainDay.Key, ma20, "MA20", Ma20Color);
        ChartBuilder.AddLine(main, mainDay.Key, ma30, "MA30", Ma30Color);

        var mainCrosshair = NewCrosshair(mainDay.Key, last);
        main.Annotations.Add(mainCrosshair);
        ChartBuilder.AddHighLowAnnotations(main, mainDay.Key, bars, (int)Math.Round(visibleStart), last);

        var sub1 = BuildSub(bars, sub1Kind, "QuoteSub1Day", visibleStart, visibleEnd, dayStep, monthStep, last);
        var sub2 = BuildSub(bars, sub2Kind, "QuoteSub2Day", visibleStart, visibleEnd, dayStep, monthStep, last);

        // KDJ/RSI(如果被选中)不参与Y轴自适应——它们的Y轴是固定0~100语义区间，见BuildSub。
        var yRanges = new List<(LinearAxis, Func<int, int, (double, double)?>)> { (mainYAxis, ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20, ma30)) };
        if (sub1.RangeFn != null) yRanges.Add((sub1.YAxis, sub1.RangeFn));
        if (sub2.RangeFn != null) yRanges.Add((sub2.YAxis, sub2.RangeFn));

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, sub1.Model, sub2.Model },
            new[] { mainDay, sub1.DateAxis, sub2.DateAxis },
            new[] { mainMonth, sub1.MonthAxis, sub2.MonthAxis },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: yRanges);

        return new QuoteChartResult
        {
            Main = main,
            MainDateAxis = mainDay,
            MainCrosshair = mainCrosshair,
            Sub1 = sub1.Model,
            Sub1DateAxis = sub1.DateAxis,
            Sub1Crosshair = sub1.Crosshair,
            Sub1FormatInfo = sub1.FormatInfo,
            Sub2 = sub2.Model,
            Sub2DateAxis = sub2.DateAxis,
            Sub2Crosshair = sub2.Crosshair,
            Sub2FormatInfo = sub2.FormatInfo,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Ma30 = ma30,
        };
    }

    private record SubBuildResult(
        PlotModel Model, LinearAxis DateAxis, LinearAxis MonthAxis, LineAnnotation Crosshair, Func<int, string> FormatInfo,
        LinearAxis YAxis, Func<int, int, (double, double)?>? RangeFn);

    private static SubBuildResult BuildSub(
        List<Bar> bars, QuoteSubIndicator kind, string keyPrefix,
        double visibleStart, double visibleEnd, double dayStep, double monthStep, int last)
    {
        var (dayAxis, monthAxis) = ChartBuilder.BuildDateAxes(bars, keyPrefix, visibleStart, visibleEnd, dayStep, monthStep);
        // 副图（成交量/MACD/KDJ/RSI）不画横纵坐标，跟主K线图一样——数值靠表头悬浮信息。
        ChartBuilder.HideAxisVisually(dayAxis);
        ChartBuilder.HideAxisVisually(monthAxis);
        var model = new PlotModel { PlotMargins = new OxyThickness(ChartBuilder.FixedLeftMargin, double.NaN, ChartBuilder.FixedRightMargin, double.NaN) };
        model.Axes.Add(dayAxis);
        model.Axes.Add(monthAxis);

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");
        Func<int, string> formatInfo;
        LinearAxis yAxis;
        // KDJ/RSI留null——它们的Y轴是固定0~100语义区间，不参与"按可见K线自适应"（见Build里的判断）。
        Func<int, int, (double, double)?>? rangeFn = null;

        switch (kind)
        {
            case QuoteSubIndicator.Volume:
            {
                yAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
                ChartBuilder.HideAxisVisually(yAxis);
                model.Axes.Add(yAxis);
                var up = new StemSeries { Title = "成交量(涨)", Color = OxyColors.Red, StrokeThickness = 3, XAxisKey = dayAxis.Key };
                var down = new StemSeries { Title = "成交量(跌)", Color = OxyColors.Green, StrokeThickness = 3, XAxisKey = dayAxis.Key };
                var volumes = bars.Select(b => b.Volume).ToList();
                for (int i = 0; i < bars.Count; i++)
                {
                    var point = new DataPoint(i, volumes[i]);
                    (bars[i].Close >= bars[i].Open ? up : down).Points.Add(point);
                }
                model.Series.Add(up);
                model.Series.Add(down);
                formatInfo = idx => $"成交量:{bars[idx].Volume:F0}";
                rangeFn = ChartBuilder.YRangeFn(volumes);
                break;
            }
            case QuoteSubIndicator.Macd:
            {
                var closes = bars.Select(b => b.Close).ToList();
                var (dif, dea) = TechnicalIndicators.MACD(closes);
                var macdHist = new double[bars.Count];
                for (int i = 0; i < bars.Count; i++)
                    macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;
                yAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
                ChartBuilder.HideAxisVisually(yAxis);
                model.Axes.Add(yAxis);
                ChartBuilder.AddHistogram(model, dayAxis.Key, macdHist);
                ChartBuilder.AddLine(model, dayAxis.Key, dif, "DIF（快线）", OxyColors.Blue);
                ChartBuilder.AddLine(model, dayAxis.Key, dea, "DEA（慢线）", OxyColors.Orange);
                model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = dayAxis.Key, Y = 0, Color = OxyColors.Black, LineStyle = LineStyle.Solid, Text = "零轴" });
                formatInfo = idx => $"DIF:{Fmt(dif[idx])}  DEA:{Fmt(dea[idx])}  MACD柱:{Fmt(macdHist[idx])}";
                rangeFn = ChartBuilder.YRangeFn(dif, dea, macdHist);
                break;
            }
            case QuoteSubIndicator.Kdj:
            {
                var closes = bars.Select(b => b.Close).ToList();
                var highs = bars.Select(b => b.High).ToList();
                var lows = bars.Select(b => b.Low).ToList();
                var (k, d, _) = TechnicalIndicators.KDJ(closes, highs, lows);
                yAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true, Minimum = 0, Maximum = 100 };
                ChartBuilder.HideAxisVisually(yAxis);
                model.Axes.Add(yAxis);
                ChartBuilder.AddLine(model, dayAxis.Key, k, "K", OxyColors.Blue);
                ChartBuilder.AddLine(model, dayAxis.Key, d, "D", OxyColors.Orange);
                formatInfo = idx => $"K:{Fmt(k[idx])}  D:{Fmt(d[idx])}";
                break;
            }
            default: // Rsi
            {
                var closes = bars.Select(b => b.Close).ToList();
                var rsi = TechnicalIndicators.RSI(closes);
                yAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true, Minimum = 0, Maximum = 100 };
                ChartBuilder.HideAxisVisually(yAxis);
                model.Axes.Add(yAxis);
                ChartBuilder.AddLine(model, dayAxis.Key, rsi, "RSI", OxyColors.Purple);
                formatInfo = idx => $"RSI:{Fmt(rsi[idx])}";
                break;
            }
        }

        var crosshair = NewCrosshair(dayAxis.Key, last);
        model.Annotations.Add(crosshair);
        return new SubBuildResult(model, dayAxis, monthAxis, crosshair, formatInfo, yAxis, rangeFn);
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
