using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>
/// Everything <see cref="DetailWindow"/> needs beyond the two <see cref="PlotModel"/>s
/// themselves: the crosshair annotations it moves on hover, and the raw series values so it can
/// build the Chinese hover-info text without recomputing indicators itself.
/// </summary>
public class ChartResult
{
    public PlotModel Main { get; init; } = new();
    public PlotModel Macd { get; init; } = new();
    /// <summary>The interactive (day-number) tier — this is what mouse pan/zoom/hover actually
    /// operates on. The year-month tier underneath is a read-only mirror, see BuildDateAxes.</summary>
    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis MacdDateAxis { get; init; } = null!;
    public LineAnnotation MainCrosshair { get; init; } = null!;
    public LineAnnotation MacdCrosshair { get; init; } = null!;
    /// <summary>Call whenever the plot's actual rendered pixel width becomes known/changes
    /// (see DetailWindow's PlotView.SizeChanged) so day/month label density can be recomputed
    /// from real available space instead of the rough guess used before first layout.</summary>
    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();
    public double[] BollMid { get; init; } = Array.Empty<double>();
    public double[] Dif { get; init; } = Array.Empty<double>();
    public double[] Dea { get; init; } = Array.Empty<double>();
    public double[] MacdHist { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Builds the OxyPlot models for the double-click detail window (see
/// doc/analysis-app-design.md section 4.2): a candlestick main chart with BOLL bands and the
/// breakout reference line, plus a MACD sub-chart (DIF/DEA lines + MACD histogram) with a
/// zero-axis line. The two charts' time axes and hover crosshair are kept in sync by
/// <see cref="DetailWindow"/> using the pieces returned here.
///
/// X axis is a plain <see cref="LinearAxis"/> indexed by bar position (0, 1, 2, ...) with a
/// custom <see cref="Axis.LabelFormatter"/> that maps a position back to that bar's date — NOT a
/// DateTimeAxis, and NOT a CategoryAxis either:
/// - DateTimeAxis is a continuous calendar axis, so it reserves visual space for every
///   weekend/holiday even though no bar exists there, leaving gaps in the candlesticks.
/// - CategoryAxis avoids that gap (categories are just bar 0, 1, 2, ... — no weekend slot to
///   begin with), but tries to render a label for every single category regardless of how many
///   are visible — with ~700+ trading days that's all the dates crushed together, illegible.
/// Two axis TIERS are stacked at the bottom (OxyPlot's Axis.PositionTier — see BuildDateAxes):
/// tier 0 (closest to the plot) shows just the day-of-month number, which is short enough to
/// densely label most/every visible trading day; tier 1 below it shows "yyyy-MM" sparsely, so you
/// always know which month you're looking at without repeating it at every tick.
/// </summary>
public static class ChartBuilder
{
    // How many of the most recent bars are visible by default — enough to actually read
    // individual candles/lines instead of 3 years squeezed into one tiny window. The user can
    // still pan/zoom (mouse drag / wheel — explicitly wired in DetailWindow, not left to
    // defaults) to see older data; this is only the *initial* view.
    internal const int DefaultVisibleBars = 100;

    // Label density is derived from *actual rendered pixel width* (see UpdatePlotWidth), not a
    // fixed label-count target — a fixed count left huge unlabeled gaps on a maximized window
    // (plenty of room per bar) while still crowding a small one. These are just the assumed
    // pixel footprint per label used to turn "available width" into "how many labels fit".
    // Day numbers are 1-2 digits ("6", "23"); the month row prints the longer "yyyy-MM". Shared
    // with GoldenCrossChartBuilder so both chart styles feel visually consistent.
    internal const double PxPerDayLabel = 26;
    internal const double PxPerMonthLabel = 70;

    // Before the real PlotView width is known (Build() runs before the window has laid out),
    // assume a modest width — UpdatePlotWidth corrects this within one layout pass of the
    // window opening, so this only affects the very first frame.
    internal const double InitialPlotWidthGuess = 1200;

    // ~21 trading days per calendar month on average — used as a floor so the month/year row
    // never gets denser than "about one label per month" even when heavily zoomed in.
    internal const int TradingDaysPerMonth = 21;

    // Fixed left/right plot margins, applied to EVERY panel of a multi-panel chart (main+MACD,
    // or main+MACD+KDJ+RSI+成交量, etc). Without this, each panel's left margin is auto-sized by
    // OxyPlot based on that panel's OWN Y-axis label width — the main K线 panel hides its Y-axis
    // entirely (margin ~0) while MACD/KDJ/RSI/成交量 show theirs (each a different width, since
    // "-999.99" vs "100" vs a raw volume number all render at different widths) — so the panels'
    // plot areas started at different X pixel offsets and never lined up vertically. Forcing the
    // same fixed margins on every panel makes every panel's plot area start/end at identical X
    // pixels, regardless of what that panel's own axis would have auto-sized to.
    internal const double FixedLeftMargin = 60;
    internal const double FixedRightMargin = 20;

    /// <summary>
    /// Builds a Y轴自适应 range function for <see cref="ChartAxisSync.Wire"/> — given the current
    /// visible bar-index window, returns the min/max across all the given value arrays within that
    /// window (skipping NaN). Combine every series a panel actually renders (e.g. High+Low for
    /// candles, or Dif+Dea+MacdHist for a MACD panel) so the Y range covers everything visible, not
    /// just one of several overlapping series.
    /// </summary>
    /// <summary>
    /// Visually hides an axis (no line/ticks/labels rendered) while keeping
    /// <see cref="Axis.IsAxisVisible"/> literally true. Setting IsAxisVisible=false looks
    /// identical but silently breaks mouse pan/zoom: OxyPlot's PlotCommands.PanAt/ZoomWheel
    /// manipulators refuse to grab any axis with IsAxisVisible=false — even with IsPanEnabled/
    /// IsZoomEnabled=true — confirmed by directly calling PlotController.HandleMouseDown against a
    /// minimal model with/without IsAxisVisible: true → handled=true, false → handled=false, no
    /// other property differed. This was why every chart's drag/zoom silently did nothing.
    /// </summary>
    internal static void HideAxisVisually(Axis axis)
    {
        axis.IsAxisVisible = true;
        axis.LabelFormatter = _ => "";
        axis.MajorTickSize = 0;
        axis.MinorTickSize = 0;
        axis.AxislineStyle = LineStyle.None;
        axis.MajorGridlineStyle = LineStyle.None;
        axis.MinorGridlineStyle = LineStyle.None;
        axis.TicklineColor = OxyColors.Transparent;
        axis.TextColor = OxyColors.Transparent;
    }

    internal static Func<int, int, (double Min, double Max)?> YRangeFn(params IReadOnlyList<double>[] arrays)
    {
        return (startIdx, endIdx) =>
        {
            double min = double.MaxValue, max = double.MinValue;
            bool any = false;
            foreach (var arr in arrays)
            {
                int s = Math.Max(0, startIdx);
                int e = Math.Min(arr.Count - 1, endIdx);
                for (int i = s; i <= e; i++)
                {
                    var v = arr[i];
                    if (double.IsNaN(v)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    any = true;
                }
            }
            return any ? (min, max) : ((double, double)?)null;
        };
    }

    public static ChartResult Build(List<Bar> bars, int lookback)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var (boll, upper, lower) = TechnicalIndicators.BOLL(closes, lookback);
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        // Chinese convention: MACD柱 = (DIF - DEA) * 2, red when >=0 (上涨动能), green when <0
        // (下跌动能) — opposite of the US red/green convention, but standard for A股 charting.
        var macdHist = new double[bars.Count];
        for (int i = 0; i < bars.Count; i++)
            macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;

        double priorHigh = double.MinValue;
        int last = bars.Count - 1;
        for (int t = last - lookback; t < last; t++) priorHigh = Math.Max(priorHigh, closes[t]);

        // Default zoom: today (the last bar) at the right edge, ~100 most recent bars visible.
        // A small right-side margin keeps the last candle from sitting flush against the edge.
        int visibleCount = Math.Min(DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (initialDayStep, initialMonthStep) = ChartAxisSync.ComputeSteps(visibleCount, InitialPlotWidthGuess, PxPerDayLabel, PxPerMonthLabel, TradingDaysPerMonth);

        var (mainDayAxis, mainMonthAxis) = BuildDateAxes(bars, "MainDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);
        var (macdDayAxis, macdMonthAxis) = BuildDateAxes(bars, "MacdDay", visibleStart, visibleEnd, initialDayStep, initialMonthStep);

        // 主K线图不画横纵坐标（典型股票APP的K线图也不画，价格靠最高/最低价标注和悬浮信息表头，
        // 日期靠悬浮信息表头）——IsAxisVisible=false 只关渲染，拖动/缩放（IsPanEnabled/
        // IsZoomEnabled）不受影响，其它面板（MACD/KDJ/RSI/成交量）保留坐标轴，不在这次改动范围内。
        HideAxisVisually(mainDayAxis);
        HideAxisVisually(mainMonthAxis);

        var main = new PlotModel { PlotMargins = new OxyThickness(FixedLeftMargin, double.NaN, FixedRightMargin, double.NaN) };
        main.Axes.Add(mainDayAxis);
        main.Axes.Add(mainMonthAxis);
        var mainYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        HideAxisVisually(mainYAxis);
        main.Axes.Add(mainYAxis);
        // 图例不再画在图表里面（以前用 OxyPlot 自带的 Legend，跟悬浮信息叠在一起会互相挡），改成
        // DetailWindow.xaml 表头那一行自己的 WPF 图例，跟这里的颜色手动对应，见该文件。

        var candles = new CandleStickSeries
        {
            Title = "K线",
            XAxisKey = mainDayAxis.Key,
            // 国内看盘习惯：涨红跌绿——不设置的话 OxyPlot 默认是美股习惯（涨绿跌红），正好反过来。
            IncreasingColor = OxyColors.Red,
            DecreasingColor = OxyColors.Green,
            // 默认宽度是相邻两根K线间距的0.8倍，看起来偏胖；调窄一些更接近常见炒股软件的样子。
            CandleWidth = 0.5,
            TrackerFormatString = "日期: {2}\n开盘: {3:F2}\n最高: {4:F2}\n最低: {5:F2}\n收盘: {6:F2}",
        };
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            candles.Items.Add(new HighLowItem(i, b.High, b.Low, b.Open, b.Close));
        }
        main.Series.Add(candles);

        AddLine(main, mainDayAxis.Key, upper, "BOLL上轨", OxyColors.LightGray);
        AddLine(main, mainDayAxis.Key, boll, "BOLL中轨", OxyColors.Orange);
        AddLine(main, mainDayAxis.Key, lower, "BOLL下轨", OxyColors.LightGray);

        main.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = mainDayAxis.Key,
            Y = priorHigh,
            // 只画锚定区间本身（前lookback根K线 到 今天），不铺满整条横轴——不然看起来像是在说
            // "所有时间"都以这个价位为参照，而实际只跟这一小段对比区间有关。
            MinimumX = last - lookback,
            MaximumX = last,
            Color = OxyColors.Red,
            LineStyle = LineStyle.Dash,
            Text = $"前{lookback}根最高={priorHigh:F2}",
        });

        var mainCrosshair = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = mainDayAxis.Key,
            X = last,
            Color = OxyColors.DarkSlateGray,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        main.Annotations.Add(mainCrosshair);
        AddHighLowAnnotations(main, mainDayAxis.Key, bars, (int)Math.Round(visibleStart), last);

        // 副图（MACD/KDJ/RSI/成交量等）现在也跟主K线图一样不画横纵坐标——数值靠表头悬浮信息，
        // 不靠坐标轴刻度；IsAxisVisible=false 只关渲染，不影响 IsPanEnabled/IsZoomEnabled。
        HideAxisVisually(macdDayAxis);
        HideAxisVisually(macdMonthAxis);
        var macd = new PlotModel { PlotMargins = new OxyThickness(FixedLeftMargin, double.NaN, FixedRightMargin, double.NaN) };
        macd.Axes.Add(macdDayAxis);
        macd.Axes.Add(macdMonthAxis);
        var macdYAxis = new LinearAxis { Position = AxisPosition.Left, IsPanEnabled = true, IsZoomEnabled = true };
        HideAxisVisually(macdYAxis);
        macd.Axes.Add(macdYAxis);

        // MACD柱 first so the DIF/DEA lines draw on top of it, not hidden underneath.
        AddHistogram(macd, macdDayAxis.Key, macdHist);
        AddLine(macd, macdDayAxis.Key, dif, "DIF（快线）", OxyColors.Blue);
        AddLine(macd, macdDayAxis.Key, dea, "DEA（慢线）", OxyColors.Orange);
        macd.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = macdDayAxis.Key,
            Y = 0,
            Color = OxyColors.Black,
            LineStyle = LineStyle.Solid,
            Text = "零轴",
        });

        var macdCrosshair = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = macdDayAxis.Key,
            X = last,
            Color = OxyColors.DarkSlateGray,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        macd.Annotations.Add(macdCrosshair);

        // K线图和MACD图共用同一根轴的含义（同一批bar在同一个位置），拖动/缩放其中一个的横轴时，
        // 另一个必须跟着变，否则两张图的日期对不上——这就是"MACD图的横轴应该是错"的根源，由
        // ChartAxisSync统一处理（金叉法的5个面板用的是同一套逻辑，见GoldenCrossChartBuilder）。
        var updateWidth = ChartAxisSync.Wire(
            new[] { main, macd },
            new[] { mainDayAxis, macdDayAxis },
            new[] { mainMonthAxis, macdMonthAxis },
            visibleStart, visibleEnd, InitialPlotWidthGuess, PxPerDayLabel, PxPerMonthLabel, TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: new[]
            {
                (mainYAxis, YRangeFn(highs, lows, upper, lower)),
                (macdYAxis, YRangeFn(dif, dea, macdHist)),
            });

        return new ChartResult
        {
            Main = main,
            Macd = macd,
            MainDateAxis = mainDayAxis,
            MacdDateAxis = macdDayAxis,
            MainCrosshair = mainCrosshair,
            MacdCrosshair = macdCrosshair,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            BollMid = boll,
            Dif = dif,
            Dea = dea,
            MacdHist = macdHist,
        };
    }

    /// <summary>
    /// Builds the two stacked bottom axes for one chart — tier 0 shows the day number (e.g.
    /// "6"), tier 1 underneath shows "yyyy-MM" with its own gridlines/ticks hidden so it reads as
    /// a plain label row, not a second interactive axis. Both index into the same <paramref
    /// name="bars"/> list, so they always describe the same bar regardless of the exact tick math
    /// (each is independently rounded to the nearest bar index).
    /// </summary>
    internal static (LinearAxis Day, LinearAxis Month) BuildDateAxes(
        List<Bar> bars, string keyPrefix, double visibleStart, double visibleEnd, double dayStep, double monthStep)
    {
        string DayLabel(double x)
        {
            int idx = (int)Math.Round(x);
            return idx >= 0 && idx < bars.Count ? bars[idx].PeriodStart.Day.ToString() : "";
        }
        string MonthLabel(double x)
        {
            int idx = (int)Math.Round(x);
            return idx >= 0 && idx < bars.Count ? bars[idx].PeriodStart.ToString("yyyy-MM") : "";
        }

        var day = new LinearAxis
        {
            Key = keyPrefix,
            Position = AxisPosition.Bottom,
            PositionTier = 0,
            LabelFormatter = DayLabel,
            Minimum = visibleStart,
            Maximum = visibleEnd,
            MajorStep = dayStep,
            MinorStep = dayStep,
            IsPanEnabled = true,
            IsZoomEnabled = true,
        };
        var month = new LinearAxis
        {
            Key = keyPrefix + "Month",
            Position = AxisPosition.Bottom,
            PositionTier = 1,
            LabelFormatter = MonthLabel,
            Minimum = visibleStart,
            Maximum = visibleEnd,
            MajorStep = monthStep,
            MinorStep = monthStep,
            // Pure label row — no ticks/gridlines of its own, and no independent mouse
            // interaction of its own (the day axis is what the user actually drags/scrolls; this
            // one is always driven programmatically by that axis's AxisChanged handler, see
            // Build above). IsPanEnabled/IsZoomEnabled stay at their true default here — despite
            // the name, Axis.Zoom() checks IsZoomEnabled even for *programmatic* calls, so
            // setting these false would silently block the sync handler's own Zoom() calls on
            // this axis (found the hard way: this axis's ActualMinimum/Maximum just never moved).
            MajorTickSize = 0,
            MinorTickSize = 0,
            AxislineStyle = LineStyle.None,
            MajorGridlineStyle = LineStyle.None,
            MinorGridlineStyle = LineStyle.None,
            TicklineColor = OxyColors.Transparent,
        };
        return (day, month);
    }

    internal static void AddLine(PlotModel model, string xAxisKey, double[] values, string title, OxyColor color)
    {
        var series = new LineSeries { Title = title, Color = color, XAxisKey = xAxisKey, TrackerFormatString = $"{title}\n日期: {{2}}\n数值: {{4:F3}}" };
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(values[i])) continue;
            series.Points.Add(new DataPoint(i, values[i]));
        }
        model.Series.Add(series);
    }

    /// <summary>
    /// MACD柱状图——用两条 StemSeries（正值红色、负值绿色，符合国内看盘习惯）各画各的，
    /// 没有对应符号的日期就不加点，天然形成红绿交替的柱状效果，不需要自定义按点着色的Series类型。
    /// </summary>
    internal static void AddHistogram(PlotModel model, string xAxisKey, double[] values)
    {
        var positive = new StemSeries { Title = "MACD柱(正)", Color = OxyColors.Red, StrokeThickness = 3, XAxisKey = xAxisKey };
        var negative = new StemSeries { Title = "MACD柱(负)", Color = OxyColors.Green, StrokeThickness = 3, XAxisKey = xAxisKey };
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(values[i])) continue;
            var point = new DataPoint(i, values[i]);
            (values[i] >= 0 ? positive : negative).Points.Add(point);
        }
        model.Series.Add(positive);
        model.Series.Add(negative);
    }

    /// <summary>
    /// 最高/最低价标注——典型股票APP的K线图都会把当前可见范围里的最高价、最低价直接标在图上
    /// （一条虚线 + 具体数字），不用自己去找哪根K线最高/最低。范围按初始默认可见的那一段算
    /// （visibleStartIdx 到 visibleEndIdx），跟其它参考线/压力线一样是画一次不随后续缩放动态
    /// 重算——用户缩放/平移之后这两条线可能不再是"当前视野"里的最高/最低，这跟压力线等其它
    /// 固定参考线的处理方式是一致的。
    /// </summary>
    internal static void AddHighLowAnnotations(PlotModel model, string xAxisKey, List<Bar> bars, int visibleStartIdx, int visibleEndIdx)
    {
        visibleStartIdx = Math.Max(0, visibleStartIdx);
        visibleEndIdx = Math.Min(bars.Count - 1, visibleEndIdx);
        if (visibleStartIdx > visibleEndIdx) return;

        int highIdx = visibleStartIdx, lowIdx = visibleStartIdx;
        for (int t = visibleStartIdx; t <= visibleEndIdx; t++)
        {
            if (bars[t].High > bars[highIdx].High) highIdx = t;
            if (bars[t].Low < bars[lowIdx].Low) lowIdx = t;
        }

        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = xAxisKey,
            Y = bars[highIdx].High,
            MinimumX = visibleStartIdx,
            MaximumX = highIdx,
            Color = OxyColors.Gray,
            LineStyle = LineStyle.Dash,
            FontSize = 10,
            Text = bars[highIdx].High.ToString("F2"),
        });
        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = xAxisKey,
            Y = bars[lowIdx].Low,
            MinimumX = visibleStartIdx,
            MaximumX = lowIdx,
            Color = OxyColors.Gray,
            LineStyle = LineStyle.Dash,
            FontSize = 10,
            Text = bars[lowIdx].Low.ToString("F2"),
        });
    }
}
