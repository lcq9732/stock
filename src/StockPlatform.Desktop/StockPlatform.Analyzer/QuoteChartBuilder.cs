using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>行情详情两个副图可选的指标类型——跟"条件详情"那几个窗口不一样，这里的副图内容不是
/// 固定的，用户在界面上手工切换，所以每种指标都要能独立算出一份够用的 PlotModel + 悬浮信息文字，
/// 不依赖是哪个方法选中了这只股票。
///
/// 枚举的顺序必须跟 QuoteDetailWindow.xaml 里两个下拉框 ComboBoxItem 的顺序**严格一一对应**——
/// 界面是靠 SelectedIndex 强转成这个枚举的（见 QuoteDetailWindow.RebuildChart），加/改项时两边
/// 必须同步。前4个（成交量/MACD/KDJ/RSI）位置不要动，构造函数按名字设默认值（成交量+MACD）。</summary>
public enum QuoteSubIndicator
{
    Volume, Macd, Kdj, Rsi,
    Amount, Turnover, Boll, Ema, Sar, Dmi, Bias, Cci, Wr, Mtm, Roc, Trix, Dma, Obv, Vr, Mfi, Emv, Psy, Arbr, Asi
}

/// <summary>
/// Everything QuoteDetailWindow needs——主图(K线+MA5/10/20/60，固定) + 两个可独立切换指标类型的
/// 副图（默认成交量+MACD）。三个面板共用同一套日期轴，横向拖动/缩放联动。
///
/// 样式仿通达信/常见券商软件的**深色主题**（黑底、右侧价格轴+横向网格线、现价横线标签、十字光标
/// 横竖两条线）。这套深色样式只用在"行情详情"，5个"条件详情"窗口仍走 ChartBuilder 的浅色样式，
/// 互不影响。
/// </summary>
public class QuoteChartResult
{
    public PlotModel Main { get; init; } = new();
    public LinearAxis MainDateAxis { get; init; } = null!;
    public LinearAxis MainYAxis { get; init; } = null!;
    public LineAnnotation MainCrosshair { get; init; } = null!;   // 竖线（时间）
    public LineAnnotation MainHairY { get; init; } = null!;       // 横线（价格）

    public PlotModel Sub1 { get; init; } = new();
    public LinearAxis Sub1DateAxis { get; init; } = null!;
    public LinearAxis Sub1YAxis { get; init; } = null!;
    public LineAnnotation Sub1Crosshair { get; init; } = null!;
    public LineAnnotation Sub1HairY { get; init; } = null!;
    /// <summary>给定bar下标，返回这个副图当前指标该显示的悬浮文字。</summary>
    public Func<int, string> Sub1FormatInfo { get; init; } = _ => "";

    public PlotModel Sub2 { get; init; } = new();
    public LinearAxis Sub2DateAxis { get; init; } = null!;
    public LinearAxis Sub2YAxis { get; init; } = null!;
    public LineAnnotation Sub2Crosshair { get; init; } = null!;
    public LineAnnotation Sub2HairY { get; init; } = null!;
    public Func<int, string> Sub2FormatInfo { get; init; } = _ => "";

    public Action<double> UpdatePlotWidth { get; init; } = _ => { };
    public List<Bar> Bars { get; init; } = new();
    public double[] Ma5 { get; init; } = Array.Empty<double>();
    public double[] Ma10 { get; init; } = Array.Empty<double>();
    public double[] Ma20 { get; init; } = Array.Empty<double>();
    public double[] Ma60 { get; init; } = Array.Empty<double>();
}

public static class QuoteChartBuilder
{
    /// <summary>横向光标线"隐藏"时放到的 Y 值——一个远离任何面板可见区间的大负值，比 NaN 安全
    /// （NaN 在个别渲染路径下可能出问题）。QuoteDetailWindow 复位非当前面板的横线时也用它。</summary>
    public const double QuoteChartHiddenY = -1e9;

    // ===== 深色主题配色（仿通达信）=====
    private static readonly OxyColor Bg = OxyColors.Black;
    private static readonly OxyColor GridColor = OxyColor.FromRgb(45, 45, 45);
    private static readonly OxyColor AxisTextColor = OxyColor.FromRgb(190, 190, 190);
    private static readonly OxyColor CrosshairColor = OxyColor.FromRgb(200, 200, 200);
    private static readonly OxyColor UpColor = OxyColors.Red;                    // 涨红
    private static readonly OxyColor DownColor = OxyColor.FromRgb(0, 210, 210);  // 跌青（通达信风格）

    // 均线颜色（通达信默认）：MA5 白、MA10 黄、MA20 品红、MA60 绿。公开给 QuoteDetailWindow 的表头
    // 数值上色用，图和文字对得上。
    public static readonly OxyColor Ma5Color = OxyColor.FromRgb(235, 235, 235);
    public static readonly OxyColor Ma10Color = OxyColor.FromRgb(255, 215, 0);
    public static readonly OxyColor Ma20Color = OxyColor.FromRgb(255, 0, 255);
    public static readonly OxyColor Ma60Color = OxyColor.FromRgb(0, 210, 0);

    public static string IndicatorLabel(QuoteSubIndicator kind) => kind switch
    {
        QuoteSubIndicator.Volume => "成交量",
        QuoteSubIndicator.Macd => "MACD",
        QuoteSubIndicator.Kdj => "KDJ",
        QuoteSubIndicator.Rsi => "RSI",
        QuoteSubIndicator.Amount => "成交额",
        QuoteSubIndicator.Turnover => "换手率",
        QuoteSubIndicator.Boll => "BOLL",
        QuoteSubIndicator.Ema => "EMA",
        QuoteSubIndicator.Sar => "SAR",
        QuoteSubIndicator.Dmi => "DMI",
        QuoteSubIndicator.Bias => "BIAS",
        QuoteSubIndicator.Cci => "CCI",
        QuoteSubIndicator.Wr => "WR",
        QuoteSubIndicator.Mtm => "MTM",
        QuoteSubIndicator.Roc => "ROC",
        QuoteSubIndicator.Trix => "TRIX",
        QuoteSubIndicator.Dma => "DMA",
        QuoteSubIndicator.Obv => "OBV",
        QuoteSubIndicator.Vr => "VR",
        QuoteSubIndicator.Mfi => "MFI",
        QuoteSubIndicator.Emv => "EMV",
        QuoteSubIndicator.Psy => "PSY",
        QuoteSubIndicator.Arbr => "ARBR",
        QuoteSubIndicator.Asi => "ASI",
        _ => kind.ToString(),
    };

    // 副图各条线颜色——为深色底做了提亮，集中定义方便 LegendFor 跟 BuildSub 对上。
    private static readonly OxyColor C1 = OxyColor.FromRgb(255, 255, 255); // 白
    private static readonly OxyColor C2 = OxyColor.FromRgb(255, 215, 0);   // 黄
    private static readonly OxyColor C3 = OxyColor.FromRgb(255, 80, 255);  // 品红
    private static readonly OxyColor C4 = OxyColor.FromRgb(0, 210, 150);   // 青绿
    private static readonly OxyColor C5 = OxyColor.FromRgb(255, 140, 0);   // 橙

    public static (string Label, OxyColor Color)[] LegendFor(QuoteSubIndicator kind) => kind switch
    {
        QuoteSubIndicator.Volume => new[] { ("成交量(涨)", UpColor), ("成交量(跌)", DownColor), ("MA5", C1), ("MA10", C2) },
        QuoteSubIndicator.Macd => new[] { ("MACD柱(正)", UpColor), ("MACD柱(负)", OxyColors.LimeGreen), ("DIF（快线）", C1), ("DEA（慢线）", C2) },
        QuoteSubIndicator.Kdj => new[] { ("K", C1), ("D", C2) },
        QuoteSubIndicator.Rsi => new[] { ("RSI", C3) },
        QuoteSubIndicator.Amount => new[] { ("成交额(涨)", UpColor), ("成交额(跌)", DownColor) },
        QuoteSubIndicator.Turnover => new[] { ("换手率(%)", C3) },
        QuoteSubIndicator.Boll => new[] { ("中轨", C2), ("上轨", C5), ("下轨", C4), ("收盘", C1) },
        QuoteSubIndicator.Ema => new[] { ("EMA12", C1), ("EMA26", C2), ("收盘", OxyColors.Gray) },
        QuoteSubIndicator.Sar => new[] { ("收盘", C1), ("SAR", UpColor) },
        QuoteSubIndicator.Dmi => new[] { ("+DI", UpColor), ("-DI", DownColor), ("ADX", C1), ("ADXR", C3) },
        QuoteSubIndicator.Bias => new[] { ("BIAS6", C1), ("BIAS12", C2), ("BIAS24", C3) },
        QuoteSubIndicator.Cci => new[] { ("CCI", C1) },
        QuoteSubIndicator.Wr => new[] { ("WR10", C1), ("WR6", C2) },
        QuoteSubIndicator.Mtm => new[] { ("MTM", C1), ("MTMMA", C2) },
        QuoteSubIndicator.Roc => new[] { ("ROC", C1), ("ROCMA", C2) },
        QuoteSubIndicator.Trix => new[] { ("TRIX", C1), ("TRIXMA", C2) },
        QuoteSubIndicator.Dma => new[] { ("DMA", C1), ("AMA", C2) },
        QuoteSubIndicator.Obv => new[] { ("OBV", C1), ("OBVMA", C2) },
        QuoteSubIndicator.Vr => new[] { ("VR", C1), ("VRMA", C2) },
        QuoteSubIndicator.Mfi => new[] { ("MFI", C1) },
        QuoteSubIndicator.Emv => new[] { ("EMV", C1), ("EMVMA", C2) },
        QuoteSubIndicator.Psy => new[] { ("PSY", C1), ("PSYMA", C2) },
        QuoteSubIndicator.Arbr => new[] { ("AR", C1), ("BR", C2) },
        QuoteSubIndicator.Asi => new[] { ("ASI", C1), ("ASIMA", C2) },
        _ => Array.Empty<(string, OxyColor)>(),
    };

    // 深色面板的左右边距：价格轴放右边（通达信风格），左边只留一点点，右边给价格刻度留够。
    private const double LeftMargin = 8;
    private const double RightMargin = 58;

    public static QuoteChartResult Build(List<Bar> bars, QuoteSubIndicator sub1Kind, QuoteSubIndicator sub2Kind)
    {
        var closes = bars.Select(b => b.Close).ToList();
        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);
        var ma60 = TechnicalIndicators.SMA(closes, 60);
        int last = bars.Count - 1;

        int visibleCount = Math.Min(ChartBuilder.DefaultVisibleBars, bars.Count);
        double visibleStart = bars.Count - visibleCount;
        double visibleEnd = last + 0.6;
        var (dayStep, monthStep) = ChartAxisSync.ComputeSteps(
            visibleCount, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth);

        var (mainDay, mainMonth) = ChartBuilder.BuildDateAxes(bars, "QuoteMainDay", visibleStart, visibleEnd, dayStep, monthStep);
        // 主图日期不显示文字（日期统一在最下面那个副图显示），但保留竖向网格线。
        StyleDateAxis(mainDay, showLabels: false, gridlines: true);
        StyleDateAxis(mainMonth, showLabels: false, gridlines: false);

        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();

        var main = NewDarkModel();
        main.Axes.Add(mainDay);
        main.Axes.Add(mainMonth);
        var mainYAxis = NewPriceAxis();
        main.Axes.Add(mainYAxis);

        var candles = new CandleStickSeries
        {
            Title = "K线",
            XAxisKey = mainDay.Key,
            IncreasingColor = UpColor,
            DecreasingColor = DownColor,
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
        ChartBuilder.AddLine(main, mainDay.Key, ma60, "MA60", Ma60Color);

        // 现价横线 + 右端价格标签（涨红跌青），仿通达信最新价那条线。
        double prevClose = last >= 1 ? closes[last - 1] : closes[last];
        var priceTagColor = closes[last] >= prevClose ? UpColor : DownColor;
        main.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = mainDay.Key,
            Y = closes[last],
            Color = priceTagColor,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
            Text = closes[last].ToString("F2"),
            TextColor = priceTagColor,
            TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Right,
            TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom,
        });

        var (mainHairX, mainHairY) = AddCrosshair(main, mainDay.Key, last);

        var sub1 = BuildSub(bars, sub1Kind, "QuoteSub1Day", visibleStart, visibleEnd, dayStep, monthStep, last, showDateLabels: false);
        var sub2 = BuildSub(bars, sub2Kind, "QuoteSub2Day", visibleStart, visibleEnd, dayStep, monthStep, last, showDateLabels: true);

        var yRanges = new List<(LinearAxis, Func<int, int, (double, double)?>)> { (mainYAxis, ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20, ma60)) };
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
            MainYAxis = mainYAxis,
            MainCrosshair = mainHairX,
            MainHairY = mainHairY,
            Sub1 = sub1.Model,
            Sub1DateAxis = sub1.DateAxis,
            Sub1YAxis = sub1.YAxis,
            Sub1Crosshair = sub1.HairX,
            Sub1HairY = sub1.HairY,
            Sub1FormatInfo = sub1.FormatInfo,
            Sub2 = sub2.Model,
            Sub2DateAxis = sub2.DateAxis,
            Sub2YAxis = sub2.YAxis,
            Sub2Crosshair = sub2.HairX,
            Sub2HairY = sub2.HairY,
            Sub2FormatInfo = sub2.FormatInfo,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Ma60 = ma60,
        };
    }

    private record SubBuildResult(
        PlotModel Model, LinearAxis DateAxis, LinearAxis MonthAxis, LineAnnotation HairX, LineAnnotation HairY,
        Func<int, string> FormatInfo, LinearAxis YAxis, Func<int, int, (double, double)?>? RangeFn);

    private static SubBuildResult BuildSub(
        List<Bar> bars, QuoteSubIndicator kind, string keyPrefix,
        double visibleStart, double visibleEnd, double dayStep, double monthStep, int last, bool showDateLabels)
    {
        var (dayAxis, monthAxis) = ChartBuilder.BuildDateAxes(bars, keyPrefix, visibleStart, visibleEnd, dayStep, monthStep);
        StyleDateAxis(dayAxis, showLabels: showDateLabels, gridlines: true);
        StyleDateAxis(monthAxis, showLabels: showDateLabels, gridlines: false);
        var model = NewDarkModel();
        model.Axes.Add(dayAxis);
        model.Axes.Add(monthAxis);

        var yAxis = NewPriceAxis();
        model.Axes.Add(yAxis);

        string key = dayAxis.Key;
        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");
        string Fmt0(double v) => double.IsNaN(v) ? "—" : v.ToString("F0");
        Func<int, string> formatInfo;
        Func<int, int, (double, double)?>? rangeFn = null;

        var closes = bars.Select(b => b.Close).ToList();
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var opens = bars.Select(b => b.Open).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();

        void AddUpDownStems(string upTitle, string downTitle, IReadOnlyList<double> values)
        {
            var up = new StemSeries { Title = upTitle, Color = UpColor, StrokeThickness = 3, XAxisKey = key };
            var down = new StemSeries { Title = downTitle, Color = DownColor, StrokeThickness = 3, XAxisKey = key };
            for (int i = 0; i < bars.Count; i++)
                (bars[i].Close >= bars[i].Open ? up : down).Points.Add(new DataPoint(i, values[i]));
            model.Series.Add(up);
            model.Series.Add(down);
        }
        void ZeroLine() => model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = key, Y = 0, Color = AxisTextColor, LineStyle = LineStyle.Solid });
        void HLine(double y) => model.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Horizontal, XAxisKey = key, Y = y, Color = GridColor, LineStyle = LineStyle.Dash });

        switch (kind)
        {
            case QuoteSubIndicator.Volume:
            {
                AddUpDownStems("成交量(涨)", "成交量(跌)", volumes);
                var volMa5 = TechnicalIndicators.SMA(volumes, 5);
                var volMa10 = TechnicalIndicators.SMA(volumes, 10);
                ChartBuilder.AddLine(model, key, volMa5, "MA5", C1);
                ChartBuilder.AddLine(model, key, volMa10, "MA10", C2);
                formatInfo = idx => $"成交量:{Fmt0(volumes[idx])}  MA5:{Fmt0(volMa5[idx])}  MA10:{Fmt0(volMa10[idx])}";
                rangeFn = ChartBuilder.YRangeFn(volumes, volMa5, volMa10);
                break;
            }

            case QuoteSubIndicator.Amount:
            {
                var amounts = bars.Select(b => b.Amount).ToList();
                AddUpDownStems("成交额(涨)", "成交额(跌)", amounts);
                formatInfo = idx => $"成交额:{FormatYi(amounts[idx])}";
                rangeFn = ChartBuilder.YRangeFn(amounts);
                break;
            }

            case QuoteSubIndicator.Turnover:
            {
                var turnovers = bars.Select(b => b.Turnover).ToList();
                ChartBuilder.AddLine(model, key, turnovers.ToArray(), "换手率", C3);
                formatInfo = idx => $"换手率:{Fmt(turnovers[idx])}%";
                rangeFn = ChartBuilder.YRangeFn(turnovers);
                break;
            }

            case QuoteSubIndicator.Macd:
            {
                var (dif, dea) = TechnicalIndicators.MACD(closes);
                var macdHist = new double[bars.Count];
                for (int i = 0; i < bars.Count; i++)
                    macdHist[i] = double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;
                ChartBuilder.AddHistogram(model, key, macdHist);
                ChartBuilder.AddLine(model, key, dif, "DIF（快线）", C1);
                ChartBuilder.AddLine(model, key, dea, "DEA（慢线）", C2);
                ZeroLine();
                formatInfo = idx => $"DIF:{Fmt(dif[idx])}  DEA:{Fmt(dea[idx])}  MACD柱:{Fmt(macdHist[idx])}";
                rangeFn = ChartBuilder.YRangeFn(dif, dea, macdHist);
                break;
            }

            case QuoteSubIndicator.Kdj:
            {
                var (k, d, _) = TechnicalIndicators.KDJ(closes, highs, lows);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, k, "K", C1);
                ChartBuilder.AddLine(model, key, d, "D", C2);
                formatInfo = idx => $"K:{Fmt(k[idx])}  D:{Fmt(d[idx])}";
                break;
            }

            case QuoteSubIndicator.Rsi:
            {
                var rsi = TechnicalIndicators.RSI(closes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, rsi, "RSI", C3);
                formatInfo = idx => $"RSI:{Fmt(rsi[idx])}";
                break;
            }

            case QuoteSubIndicator.Boll:
            {
                var (mid, upper, lower) = TechnicalIndicators.BOLL(closes);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", C1);
                ChartBuilder.AddLine(model, key, mid, "中轨", C2);
                ChartBuilder.AddLine(model, key, upper, "上轨", C5);
                ChartBuilder.AddLine(model, key, lower, "下轨", C4);
                formatInfo = idx => $"中轨:{Fmt(mid[idx])}  上轨:{Fmt(upper[idx])}  下轨:{Fmt(lower[idx])}";
                rangeFn = ChartBuilder.YRangeFn(closes, upper, lower);
                break;
            }

            case QuoteSubIndicator.Ema:
            {
                var ema12 = TechnicalIndicators.EMA(closes, 12);
                var ema26 = TechnicalIndicators.EMA(closes, 26);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", OxyColors.Gray);
                ChartBuilder.AddLine(model, key, ema12, "EMA12", C1);
                ChartBuilder.AddLine(model, key, ema26, "EMA26", C2);
                formatInfo = idx => $"EMA12:{Fmt(ema12[idx])}  EMA26:{Fmt(ema26[idx])}";
                rangeFn = ChartBuilder.YRangeFn(closes, ema12, ema26);
                break;
            }

            case QuoteSubIndicator.Sar:
            {
                var sar = TechnicalIndicators.SAR(highs, lows);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", C1);
                var dots = new ScatterSeries { Title = "SAR", MarkerType = MarkerType.Circle, MarkerSize = 2, MarkerFill = UpColor, XAxisKey = key };
                for (int i = 0; i < sar.Length; i++)
                    if (!double.IsNaN(sar[i])) dots.Points.Add(new ScatterPoint(i, sar[i]));
                model.Series.Add(dots);
                formatInfo = idx => $"SAR:{Fmt(sar[idx])}";
                rangeFn = ChartBuilder.YRangeFn(closes, sar);
                break;
            }

            case QuoteSubIndicator.Dmi:
            {
                var (pdi, mdi, adx, adxr) = TechnicalIndicators.DMI(highs, lows, closes);
                ChartBuilder.AddLine(model, key, pdi, "+DI", UpColor);
                ChartBuilder.AddLine(model, key, mdi, "-DI", DownColor);
                ChartBuilder.AddLine(model, key, adx, "ADX", C1);
                ChartBuilder.AddLine(model, key, adxr, "ADXR", C3);
                formatInfo = idx => $"+DI:{Fmt(pdi[idx])}  -DI:{Fmt(mdi[idx])}  ADX:{Fmt(adx[idx])}  ADXR:{Fmt(adxr[idx])}";
                rangeFn = ChartBuilder.YRangeFn(pdi, mdi, adx, adxr);
                break;
            }

            case QuoteSubIndicator.Bias:
            {
                var b6 = TechnicalIndicators.BIAS(closes, 6);
                var b12 = TechnicalIndicators.BIAS(closes, 12);
                var b24 = TechnicalIndicators.BIAS(closes, 24);
                ChartBuilder.AddLine(model, key, b6, "BIAS6", C1);
                ChartBuilder.AddLine(model, key, b12, "BIAS12", C2);
                ChartBuilder.AddLine(model, key, b24, "BIAS24", C3);
                ZeroLine();
                formatInfo = idx => $"BIAS6:{Fmt(b6[idx])}  BIAS12:{Fmt(b12[idx])}  BIAS24:{Fmt(b24[idx])}";
                rangeFn = ChartBuilder.YRangeFn(b6, b12, b24);
                break;
            }

            case QuoteSubIndicator.Cci:
            {
                var cci = TechnicalIndicators.CCI(highs, lows, closes);
                ChartBuilder.AddLine(model, key, cci, "CCI", C1);
                HLine(100); HLine(-100);
                formatInfo = idx => $"CCI:{Fmt(cci[idx])}";
                rangeFn = ChartBuilder.YRangeFn(cci);
                break;
            }

            case QuoteSubIndicator.Wr:
            {
                var wr10 = TechnicalIndicators.WR(highs, lows, closes, 10);
                var wr6 = TechnicalIndicators.WR(highs, lows, closes, 6);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, wr10, "WR10", C1);
                ChartBuilder.AddLine(model, key, wr6, "WR6", C2);
                formatInfo = idx => $"WR10:{Fmt(wr10[idx])}  WR6:{Fmt(wr6[idx])}";
                break;
            }

            case QuoteSubIndicator.Mtm:
            {
                var (mtm, mtmMa) = TechnicalIndicators.MTM(closes);
                ChartBuilder.AddLine(model, key, mtm, "MTM", C1);
                ChartBuilder.AddLine(model, key, mtmMa, "MTMMA", C2);
                ZeroLine();
                formatInfo = idx => $"MTM:{Fmt(mtm[idx])}  MTMMA:{Fmt(mtmMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(mtm, mtmMa);
                break;
            }

            case QuoteSubIndicator.Roc:
            {
                var (roc, rocMa) = TechnicalIndicators.ROC(closes);
                ChartBuilder.AddLine(model, key, roc, "ROC", C1);
                ChartBuilder.AddLine(model, key, rocMa, "ROCMA", C2);
                ZeroLine();
                formatInfo = idx => $"ROC:{Fmt(roc[idx])}  ROCMA:{Fmt(rocMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(roc, rocMa);
                break;
            }

            case QuoteSubIndicator.Trix:
            {
                var (trix, trixMa) = TechnicalIndicators.TRIX(closes);
                ChartBuilder.AddLine(model, key, trix, "TRIX", C1);
                ChartBuilder.AddLine(model, key, trixMa, "TRIXMA", C2);
                ZeroLine();
                formatInfo = idx => $"TRIX:{Fmt(trix[idx])}  TRIXMA:{Fmt(trixMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(trix, trixMa);
                break;
            }

            case QuoteSubIndicator.Dma:
            {
                var (dma, ama) = TechnicalIndicators.DMA(closes);
                ChartBuilder.AddLine(model, key, dma, "DMA", C1);
                ChartBuilder.AddLine(model, key, ama, "AMA", C2);
                ZeroLine();
                formatInfo = idx => $"DMA:{Fmt(dma[idx])}  AMA:{Fmt(ama[idx])}";
                rangeFn = ChartBuilder.YRangeFn(dma, ama);
                break;
            }

            case QuoteSubIndicator.Obv:
            {
                var (obv, obvMa) = TechnicalIndicators.OBV(closes, volumes);
                ChartBuilder.AddLine(model, key, obv, "OBV", C1);
                ChartBuilder.AddLine(model, key, obvMa, "OBVMA", C2);
                formatInfo = idx => $"OBV:{Fmt0(obv[idx])}  OBVMA:{Fmt0(obvMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(obv, obvMa);
                break;
            }

            case QuoteSubIndicator.Vr:
            {
                var (vr, vrMa) = TechnicalIndicators.VR(closes, volumes);
                ChartBuilder.AddLine(model, key, vr, "VR", C1);
                ChartBuilder.AddLine(model, key, vrMa, "VRMA", C2);
                formatInfo = idx => $"VR:{Fmt(vr[idx])}  VRMA:{Fmt(vrMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(vr, vrMa);
                break;
            }

            case QuoteSubIndicator.Mfi:
            {
                var mfi = TechnicalIndicators.MFI(highs, lows, closes, volumes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, mfi, "MFI", C1);
                formatInfo = idx => $"MFI:{Fmt(mfi[idx])}";
                break;
            }

            case QuoteSubIndicator.Emv:
            {
                var (emv, emvMa) = TechnicalIndicators.EMV(highs, lows, volumes);
                ChartBuilder.AddLine(model, key, emv, "EMV", C1);
                ChartBuilder.AddLine(model, key, emvMa, "EMVMA", C2);
                ZeroLine();
                formatInfo = idx => $"EMV:{Fmt(emv[idx])}  EMVMA:{Fmt(emvMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(emv, emvMa);
                break;
            }

            case QuoteSubIndicator.Psy:
            {
                var (psy, psyMa) = TechnicalIndicators.PSY(closes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, psy, "PSY", C1);
                ChartBuilder.AddLine(model, key, psyMa, "PSYMA", C2);
                formatInfo = idx => $"PSY:{Fmt(psy[idx])}  PSYMA:{Fmt(psyMa[idx])}";
                break;
            }

            case QuoteSubIndicator.Arbr:
            {
                var (ar, br) = TechnicalIndicators.ARBR(opens, highs, lows, closes);
                ChartBuilder.AddLine(model, key, ar, "AR", C1);
                ChartBuilder.AddLine(model, key, br, "BR", C2);
                formatInfo = idx => $"AR:{Fmt(ar[idx])}  BR:{Fmt(br[idx])}";
                rangeFn = ChartBuilder.YRangeFn(ar, br);
                break;
            }

            default: // Asi
            {
                var (asi, asiMa) = TechnicalIndicators.ASI(opens, highs, lows, closes);
                ChartBuilder.AddLine(model, key, asi, "ASI", C1);
                ChartBuilder.AddLine(model, key, asiMa, "ASIMA", C2);
                ZeroLine();
                formatInfo = idx => $"ASI:{Fmt(asi[idx])}  ASIMA:{Fmt(asiMa[idx])}";
                rangeFn = ChartBuilder.YRangeFn(asi, asiMa);
                break;
            }
        }

        var (hairX, hairY) = AddCrosshair(model, dayAxis.Key, last);
        return new SubBuildResult(model, dayAxis, monthAxis, hairX, hairY, formatInfo, yAxis, rangeFn);
    }

    private static string FormatYi(double v) => double.IsNaN(v) ? "—" : v switch
    {
        >= 1e8 => $"{v / 1e8:F2}亿",
        >= 1e4 => $"{v / 1e4:F2}万",
        _ => v.ToString("F0"),
    };

    // ===== 深色样式辅助 =====

    private static PlotModel NewDarkModel()
    {
        var m = new PlotModel
        {
            Background = Bg,
            PlotAreaBorderColor = GridColor,
            TextColor = AxisTextColor,
            PlotMargins = new OxyThickness(LeftMargin, double.NaN, RightMargin, double.NaN),
        };
        return m;
    }

    private static LinearAxis NewPriceAxis() => new()
    {
        Position = AxisPosition.Right,
        IsPanEnabled = true,
        IsZoomEnabled = true,
        TextColor = AxisTextColor,
        TicklineColor = GridColor,
        AxislineColor = GridColor,
        AxislineStyle = LineStyle.Solid,
        MajorGridlineStyle = LineStyle.Solid,
        MajorGridlineColor = GridColor,
        MinorGridlineStyle = LineStyle.None,
        MajorTickSize = 3,
        MinorTickSize = 0,
        FontSize = 10,
    };

    private static void StyleDateAxis(Axis axis, bool showLabels, bool gridlines)
    {
        axis.TextColor = showLabels ? AxisTextColor : OxyColors.Transparent;
        axis.TicklineColor = GridColor;
        axis.AxislineColor = GridColor;
        axis.AxislineStyle = LineStyle.Solid;
        axis.MajorGridlineStyle = gridlines ? LineStyle.Solid : LineStyle.None;
        axis.MajorGridlineColor = GridColor;
        axis.MinorGridlineStyle = LineStyle.None;
        axis.MajorTickSize = showLabels ? 3 : 0;
        axis.MinorTickSize = 0;
        axis.FontSize = 10;
    }

    /// <summary>加一条竖线(时间)+一条横线(价格)十字光标，返回给窗口在鼠标移动时更新位置。</summary>
    private static (LineAnnotation X, LineAnnotation Y) AddCrosshair(PlotModel model, string xAxisKey, int last)
    {
        var vx = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            XAxisKey = xAxisKey,
            X = last,
            Color = CrosshairColor,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        var hy = new LineAnnotation
        {
            Type = LineAnnotationType.Horizontal,
            XAxisKey = xAxisKey,
            // 初始给一个远离任何可见区间的大负值（合法数值，避免 NaN 触发渲染异常）——这样横向
            // 光标线初始在所有面板都落在可见范围外、看不见；鼠标移到某个面板上才更新到光标处。
            Y = QuoteChartHiddenY,
            Color = CrosshairColor,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1,
        };
        model.Annotations.Add(vx);
        model.Annotations.Add(hy);
        return (vx, hy);
    }
}
