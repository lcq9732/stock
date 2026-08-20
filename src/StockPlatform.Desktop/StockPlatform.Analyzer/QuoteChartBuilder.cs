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
/// 副图（默认 MACD + KDJ，2026-08-07 由"成交量+MACD"改成这样：短线法的入场判定看的就是这两个
/// 指标，打开行情详情要能直接对上条件详情里的数值；成交量可从下拉框切回来）。
/// 三个面板共用同一套日期轴，横向拖动/缩放联动。
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
    /// <summary>给定bar下标，返回这个副图当前指标该显示的信息栏内容——**按值分段带颜色**
    /// （2026-08-11改，之前是一整条白字）：每段的颜色跟图上那条线/那根柱子一致，这样不用去对照
    /// 右上角图例就知道哪个数字对应哪条线，跟主图 MA5/MA10/MA20/MA60 的显示方式统一。</summary>
    public Func<int, IReadOnlyList<QuoteChartBuilder.InfoSegment>> Sub1FormatInfo { get; init; }
        = _ => Array.Empty<QuoteChartBuilder.InfoSegment>();

    public PlotModel Sub2 { get; init; } = new();
    public LinearAxis Sub2DateAxis { get; init; } = null!;
    public LinearAxis Sub2YAxis { get; init; } = null!;
    public LineAnnotation Sub2Crosshair { get; init; } = null!;
    public LineAnnotation Sub2HairY { get; init; } = null!;
    public Func<int, IReadOnlyList<QuoteChartBuilder.InfoSegment>> Sub2FormatInfo { get; init; }
        = _ => Array.Empty<QuoteChartBuilder.InfoSegment>();

    /// <summary>叠加的大盘指数名称（如"上证指数"）；没叠加时为 null。</summary>
    public string? OverlayName { get; init; }

    /// <summary>叠加线的数值——已按"可见区间左边缘"等比缩放到个股的价格刻度上，所以它跟个股收盘价
    /// 可以直接比：<c>个股收盘/叠加值-1</c> 就是这段时间的**相对强弱**（正=跑赢大盘）。平移/缩放会
    /// 重新锚定，这个数组的内容随之原地更新（信息栏每次都重新读，拿到的总是当前锚点下的值）。
    /// 没叠加时为 null。</summary>
    public double[]? OverlayScaled { get; init; }

    public Action<double> UpdatePlotWidth { get; init; } = _ => { };

    /// <summary>本次画上去的斐波那契波段（没勾就是 null）——窗口的信息栏要显示它的端点和各档价位，
    /// 拿的必须是图上真正用的那一段，不能各算各的。</summary>
    public FibSwing? Fib { get; init; }

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

    // 副图各条线颜色——为深色底做了提亮。同一个颜色既用来画线，也用来给信息栏里对应的数值上色
    // （见 InfoSegment / Seg），所以图上的线和字的颜色天然一致；原来那套图例（LegendFor）因此
    // 成了重复信息，已于 2026-08-11 连同界面上的色块一起删除。
    private static readonly OxyColor C1 = OxyColor.FromRgb(255, 255, 255); // 白
    private static readonly OxyColor C2 = OxyColor.FromRgb(255, 215, 0);   // 黄
    private static readonly OxyColor C3 = OxyColor.FromRgb(255, 80, 255);  // 品红
    private static readonly OxyColor C4 = OxyColor.FromRgb(0, 210, 150);   // 青绿
    private static readonly OxyColor C5 = OxyColor.FromRgb(255, 140, 0);   // 橙

    /// <summary>大盘叠加线的颜色——灰蓝，刻意选一个跟K线红/青、四条均线（白/黄/品红/绿）都不撞的
    /// 中性色，一眼能认出"这条不是这只股票自己的线"。
    ///
    /// 公开出去（跟 <see cref="Ma5Color"/> 那几个同样的道理）：界面上"叠加大盘"复选框的文字色和信息栏里
    /// 指数名称的颜色都取自这里，保证"字的颜色 = 图上那条线的颜色"，不用在 XAML/代码里各写一遍色值
    /// （写死几份迟早会改漏一处、对不上）。</summary>
    public static readonly OxyColor OverlayColor = OxyColor.FromRgb(120, 170, 255);


    // 深色面板的左右边距：价格轴放右边（通达信风格），左边只留一点点，右边给价格刻度留够。
    private const double LeftMargin = 8;
    private const double RightMargin = 58;

    /// <param name="indexOverlay">要叠加的大盘指数（名称 + 它自己的K线）——传 null 就是不叠加，
    /// 行为跟加这个功能之前完全一样。叠加的画法见方法体里"大盘叠加"那段注释。</param>
    /// <param name="fib">要叠加的斐波那契回撤位所依据的波段——传 null 就是不画。波段本身由调用方
    /// 决定（自动识别或用户手动点选），这里只负责把线画出来。</param>
    public static QuoteChartResult Build(
        List<Bar> bars, QuoteSubIndicator sub1Kind, QuoteSubIndicator sub2Kind,
        (string Name, List<Bar> Bars)? indexOverlay = null,
        FibSwing? fib = null)
    {
        Action<int, int>? reanchorOverlay = null;
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

        if (fib != null) AddFibonacci(main, mainDay.Key, fib, bars.Count);

        var (mainHairX, mainHairY) = AddCrosshair(main, mainDay.Key, last);

        var sub1 = BuildSub(bars, sub1Kind, "QuoteSub1Day", visibleStart, visibleEnd, dayStep, monthStep, last, showDateLabels: false);
        var sub2 = BuildSub(bars, sub2Kind, "QuoteSub2Day", visibleStart, visibleEnd, dayStep, monthStep, last, showDateLabels: true);

        // ── 大盘叠加（2026-08-13新增）──
        // 做法：把指数按**可见区间左边缘**等比缩放到个股的价格刻度上
        //     叠加值[i] = 指数[i] / 指数[左边缘] * 个股收盘[左边缘]
        // 于是叠加线和K线从同一点出发，**两者的垂直差距就是这段时间的相对强弱**（个股在线上=跑赢
        // 大盘，线下=跑输）。这样K线仍是真实价格、价格轴照旧有意义，也不用引第二个Y轴——双轴可以
        // 靠调刻度把任意两条线"看起来相关"，是自欺欺人。等比缩放不改变指数的形状，趋势不失真。
        // 平移/缩放时按新的左边缘重新锚定（见下面传给 Wire 的 onVisibleRangeChanged），所以它永远
        // 回答"从我现在看的这一段起，谁更强"。
        double[]? overlayScaled = null;
        LineSeries? overlaySeries = null;
        if (indexOverlay is { } ov && ov.Bars.Count > 0)
        {
            var aligned = IndexOverlayMatcher.AlignToBars(bars, ov.Bars);   // 按交易日对齐，不是按下标
            overlayScaled = new double[bars.Count];
            overlaySeries = new LineSeries
            {
                Title = ov.Name,
                Color = OverlayColor,
                StrokeThickness = 1.6,
                LineStyle = LineStyle.Solid,
                XAxisKey = mainDay.Key,
                TrackerFormatString = ov.Name + " {4:F2}",
            };
            main.Series.Add(overlaySeries);

            // 按给定的左边缘重算整条叠加线（只重算数值，不重建系列对象）
            void Reanchor(int startIdx, int endIdx)
            {
                if (overlayScaled == null || overlaySeries == null) return;
                // 找左边缘往右第一个"指数和个股都有值"的下标当锚点——左边缘那根可能正好是个股上市
                // 前/指数缺值，直接拿它当基准会得出 NaN 或荒谬的比例。
                int anchor = -1;
                for (int i = Math.Max(0, startIdx); i < bars.Count && i <= Math.Max(endIdx, startIdx); i++)
                {
                    if (!double.IsNaN(aligned[i]) && aligned[i] > 0 && bars[i].Close > 0) { anchor = i; break; }
                }
                overlaySeries.Points.Clear();
                if (anchor < 0)
                {
                    Array.Fill(overlayScaled, double.NaN);
                    return;
                }
                double factor = bars[anchor].Close / aligned[anchor];
                for (int i = 0; i < bars.Count; i++)
                {
                    overlayScaled[i] = double.IsNaN(aligned[i]) ? double.NaN : aligned[i] * factor;
                    // NaN 不进点集——OxyPlot 遇到 NaN 会断线，这正是我们要的（缺数据就不画）
                    if (!double.IsNaN(overlayScaled[i])) overlaySeries.Points.Add(new DataPoint(i, overlayScaled[i]));
                }
            }

            Reanchor((int)Math.Floor(visibleStart), (int)Math.Ceiling(visibleEnd));
            reanchorOverlay = Reanchor;
        }

        // 主图Y范围要把叠加线算进去，否则它会跑出面板上下边界（叠加线的值域跟着相对强弱走，
        // 可能明显高于/低于个股价格区间）。没有叠加时行为跟以前完全一样。
        var mainRangeFn = overlayScaled == null
            ? ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20, ma60)
            : ChartBuilder.YRangeFn(highs, lows, ma5, ma10, ma20, ma60, overlayScaled);
        var yRanges = new List<(LinearAxis, Func<int, int, (double, double)?>)> { (mainYAxis, mainRangeFn) };
        if (sub1.RangeFn != null) yRanges.Add((sub1.YAxis, sub1.RangeFn));
        if (sub2.RangeFn != null) yRanges.Add((sub2.YAxis, sub2.RangeFn));

        var updateWidth = ChartAxisSync.Wire(
            new[] { main, sub1.Model, sub2.Model },
            new[] { mainDay, sub1.DateAxis, sub2.DateAxis },
            new[] { mainMonth, sub1.MonthAxis, sub2.MonthAxis },
            visibleStart, visibleEnd, ChartBuilder.InitialPlotWidthGuess, ChartBuilder.PxPerDayLabel, ChartBuilder.PxPerMonthLabel, ChartBuilder.TradingDaysPerMonth,
            candleSeries: new[] { candles },
            yAxisRanges: yRanges,
            onVisibleRangeChanged: reanchorOverlay);

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
            OverlayName = overlaySeries == null ? null : indexOverlay!.Value.Name,
            OverlayScaled = overlayScaled,
            Fib = fib,
            UpdatePlotWidth = updateWidth,
            Bars = bars,
            Ma5 = ma5,
            Ma10 = ma10,
            Ma20 = ma20,
            Ma60 = ma60,
        };
    }

    // ── 斐波那契回撤位（2026-08-17新增）──
    // 金色系，跟均线/叠加线的颜色都不撞。61.8%（黄金分割位）和两个端点画得更实，其余档位暗一些：
    // 图上同时有7~9条线，全用一个亮度会糊成一片、也会喧宾夺主压住K线本身。
    public static readonly OxyColor FibColor = OxyColor.FromRgb(255, 190, 60);
    private static readonly OxyColor FibKeyColor = OxyColor.FromRgb(255, 225, 130);
    private static readonly OxyColor FibExtColor = OxyColor.FromRgb(160, 130, 70);

    /// <summary>
    /// 把回撤位画到主图上：每档一条水平线（从波段起点向右延伸到最新一根），外加一条连接高低点的
    /// 斜线，让人一眼看出这些线是量哪一段量出来的。
    ///
    /// 两个刻意的处理：
    /// ① 线画在K线**下层**（BelowSeries）——回撤位是背景刻度，不该盖住K线实体。
    /// ② 扩展位（&gt;100%）**不纳入Y轴范围计算**（见 Build 里的 mainRangeFn，那里只喂了高低价和均线）。
    ///    扩展位常常远离当前价格区间，纳进去会把K线压扁成一条带子；线超出面板就不显示那一段，
    ///    这是可接受的——需要看扩展位时把图缩小即可。
    /// </summary>
    private static void AddFibonacci(PlotModel main, string xAxisKey, FibSwing fib, int barCount)
    {
        // 连接高低点的斜线：波段本身。虚线、细，只是用来交代"这些水平线是从哪两点量出来的"。
        var trend = new LineSeries
        {
            Title = "FIB波段",
            XAxisKey = xAxisKey,
            Color = OxyColor.FromAColor(150, FibColor),
            StrokeThickness = 1.2,
            LineStyle = LineStyle.Dash,
            TrackerFormatString = "FIB波段",
        };
        trend.Points.Add(new DataPoint(fib.LowIdx, fib.Low));
        trend.Points.Add(new DataPoint(fib.HighIdx, fib.High));
        // 按时间先后排点，否则上涨/下跌波段里会画成反向的线段（OxyPlot 按点序连线）。
        if (fib.LowIdx > fib.HighIdx) trend.Points.Reverse();
        main.Series.Add(trend);

        foreach (var lv in FibonacciRetracement.Levels(fib))
        {
            bool isEndpoint = lv.Ratio is 0 or 1;
            bool isGolden = Math.Abs(lv.Ratio - 0.618) < 1e-9;
            var color = lv.IsExtension ? FibExtColor : (isEndpoint || isGolden ? FibKeyColor : FibColor);

            main.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                XAxisKey = xAxisKey,
                Y = lv.Price,
                // 从波段起点画到最右——回撤位只对"这段行情之后"有意义，往左延伸到上市第一天纯属噪音。
                MinimumX = fib.StartIdx,
                MaximumX = barCount + 0.6,
                Color = lv.IsExtension ? OxyColor.FromAColor(170, color) : color,
                LineStyle = isEndpoint || isGolden ? LineStyle.Solid : LineStyle.Dot,
                StrokeThickness = isGolden ? 1.4 : 1,
                Layer = AnnotationLayer.BelowSeries,
                Text = $"{lv.Label} {lv.Price:F2}",
                // 文字一律用亮色（线本身才分主次）——标签不可避免要压在K线上，暗色文字压上去就读不出来了。
                TextColor = FibKeyColor,
                FontSize = 11,
                // 文字压在线的左端（波段起点附近）——右端留给现价标签和价格轴，挤在一起就都看不清了。
                TextLinePosition = 0.02,
                TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom,
            });
        }
    }

    /// <summary>
    /// 信息栏里的一段文字 + 它的颜色（2026-08-11新增）——颜色取自图上对应那条线/柱子，让"哪个数字是
    /// 哪条线"一眼可见，不必去对照右上角图例。
    ///
    /// <see cref="Text"/> 里已经把值**右对齐补白到固定宽度**、并带上尾部两个空格：信息栏用的是等宽
    /// 字体（Consolas，见 QuoteDetailWindow.xaml），标签本身是常量宽度，所以只要值的字符数固定，
    /// 整条信息栏各字段的横向位置就固定，不会因为数字从 5.6 变成 -12.34 而整排往左右跳（这是用户
    /// 2026-08-11 反馈的问题）。
    /// </summary>
    public readonly record struct InfoSegment(string Text, OxyColor Color);

    /// <summary>拼一段信息栏文字：<c>标签:值</c>，值右对齐到 <paramref name="width"/> 个字符宽、
    /// 后面固定跟两个空格当字段间隔。宽度按量级选：价格/指标值用默认8，成交量这类大数用12。</summary>
    private static InfoSegment Seg(string label, string value, OxyColor color, int width = 8)
        => new($"{label}:{value.PadLeft(width)}  ", color);

    private record SubBuildResult(
        PlotModel Model, LinearAxis DateAxis, LinearAxis MonthAxis, LineAnnotation HairX, LineAnnotation HairY,
        Func<int, IReadOnlyList<InfoSegment>> FormatInfo, LinearAxis YAxis, Func<int, int, (double, double)?>? RangeFn);

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
        Func<int, IReadOnlyList<InfoSegment>> formatInfo;
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
                formatInfo = idx => new[] { Seg("成交量", Fmt0(volumes[idx]), bars[idx].Close >= bars[idx].Open ? UpColor : DownColor, 12),
                                             Seg("MA5", Fmt0(volMa5[idx]), C1, 12), Seg("MA10", Fmt0(volMa10[idx]), C2, 12) };
                rangeFn = ChartBuilder.YRangeFn(volumes, volMa5, volMa10);
                break;
            }

            case QuoteSubIndicator.Amount:
            {
                var amounts = bars.Select(b => b.Amount).ToList();
                AddUpDownStems("成交额(涨)", "成交额(跌)", amounts);
                formatInfo = idx => new[] { Seg("成交额", FormatYi(amounts[idx]), bars[idx].Close >= bars[idx].Open ? UpColor : DownColor, 10) };
                rangeFn = ChartBuilder.YRangeFn(amounts);
                break;
            }

            case QuoteSubIndicator.Turnover:
            {
                var turnovers = bars.Select(b => b.Turnover).ToList();
                ChartBuilder.AddLine(model, key, turnovers.ToArray(), "换手率", C3);
                formatInfo = idx => new[] { Seg("换手率", Fmt(turnovers[idx]) + "%", C3) };
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
                // MACD柱放最前面（2026-08-11 按用户要求）——它是这个指标里最先看的那个值（柱由红转绿/
                // 由绿转红就是信号），DIF/DEA 是它的来源，排在后面。颜色随正负走，跟图上的柱子一致。
                formatInfo = idx => new[] { Seg("MACD柱", Fmt(macdHist[idx]), macdHist[idx] >= 0 ? UpColor : OxyColors.LimeGreen),
                                             Seg("DIF", Fmt(dif[idx]), C1), Seg("DEA", Fmt(dea[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(dif, dea, macdHist);
                break;
            }

            case QuoteSubIndicator.Kdj:
            {
                var (k, d, j) = TechnicalIndicators.KDJ(closes, highs, lows);
                // J = 3K-2D，经常冲出 0~100（这是它的正常形态，也是超买超卖信号最强的地方），
                // 所以不能死钉 0..100 把它裁掉——以 0/100 为基准框，再按 J 的实际范围往外扩。
                var kdjAll = k.Concat(d).Concat(j).Where(v => !double.IsNaN(v)).ToList();
                yAxis.Minimum = kdjAll.Count > 0 ? Math.Min(0, Math.Floor(kdjAll.Min())) : 0;
                yAxis.Maximum = kdjAll.Count > 0 ? Math.Max(100, Math.Ceiling(kdjAll.Max())) : 100;
                ChartBuilder.AddLine(model, key, k, "K", C1);
                ChartBuilder.AddLine(model, key, d, "D", C2);
                ChartBuilder.AddLine(model, key, j, "J", C3);
                formatInfo = idx => new[] { Seg("K", Fmt(k[idx]), C1), Seg("D", Fmt(d[idx]), C2), Seg("J", Fmt(j[idx]), C3) };
                break;
            }

            case QuoteSubIndicator.Rsi:
            {
                var rsi = TechnicalIndicators.RSI(closes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, rsi, "RSI", C3);
                formatInfo = idx => new[] { Seg("RSI", Fmt(rsi[idx]), C3) };
                break;
            }

            case QuoteSubIndicator.Boll:
            {
                var (mid, upper, lower) = TechnicalIndicators.BOLL(closes);
                ChartBuilder.AddLine(model, key, closes.ToArray(), "收盘", C1);
                ChartBuilder.AddLine(model, key, mid, "中轨", C2);
                ChartBuilder.AddLine(model, key, upper, "上轨", C5);
                ChartBuilder.AddLine(model, key, lower, "下轨", C4);
                formatInfo = idx => new[] { Seg("中轨", Fmt(mid[idx]), C2), Seg("上轨", Fmt(upper[idx]), C5), Seg("下轨", Fmt(lower[idx]), C4) };
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
                formatInfo = idx => new[] { Seg("EMA12", Fmt(ema12[idx]), C1), Seg("EMA26", Fmt(ema26[idx]), C2) };
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
                formatInfo = idx => new[] { Seg("SAR", Fmt(sar[idx]), UpColor) };
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
                formatInfo = idx => new[] { Seg("+DI", Fmt(pdi[idx]), UpColor), Seg("-DI", Fmt(mdi[idx]), DownColor),
                                             Seg("ADX", Fmt(adx[idx]), C1), Seg("ADXR", Fmt(adxr[idx]), C3) };
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
                formatInfo = idx => new[] { Seg("BIAS6", Fmt(b6[idx]), C1), Seg("BIAS12", Fmt(b12[idx]), C2), Seg("BIAS24", Fmt(b24[idx]), C3) };
                rangeFn = ChartBuilder.YRangeFn(b6, b12, b24);
                break;
            }

            case QuoteSubIndicator.Cci:
            {
                var cci = TechnicalIndicators.CCI(highs, lows, closes);
                ChartBuilder.AddLine(model, key, cci, "CCI", C1);
                HLine(100); HLine(-100);
                formatInfo = idx => new[] { Seg("CCI", Fmt(cci[idx]), C1) };
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
                formatInfo = idx => new[] { Seg("WR10", Fmt(wr10[idx]), C1), Seg("WR6", Fmt(wr6[idx]), C2) };
                break;
            }

            case QuoteSubIndicator.Mtm:
            {
                var (mtm, mtmMa) = TechnicalIndicators.MTM(closes);
                ChartBuilder.AddLine(model, key, mtm, "MTM", C1);
                ChartBuilder.AddLine(model, key, mtmMa, "MTMMA", C2);
                ZeroLine();
                formatInfo = idx => new[] { Seg("MTM", Fmt(mtm[idx]), C1), Seg("MTMMA", Fmt(mtmMa[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(mtm, mtmMa);
                break;
            }

            case QuoteSubIndicator.Roc:
            {
                var (roc, rocMa) = TechnicalIndicators.ROC(closes);
                ChartBuilder.AddLine(model, key, roc, "ROC", C1);
                ChartBuilder.AddLine(model, key, rocMa, "ROCMA", C2);
                ZeroLine();
                formatInfo = idx => new[] { Seg("ROC", Fmt(roc[idx]), C1), Seg("ROCMA", Fmt(rocMa[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(roc, rocMa);
                break;
            }

            case QuoteSubIndicator.Trix:
            {
                var (trix, trixMa) = TechnicalIndicators.TRIX(closes);
                ChartBuilder.AddLine(model, key, trix, "TRIX", C1);
                ChartBuilder.AddLine(model, key, trixMa, "TRIXMA", C2);
                ZeroLine();
                formatInfo = idx => new[] { Seg("TRIX", Fmt(trix[idx]), C1), Seg("TRIXMA", Fmt(trixMa[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(trix, trixMa);
                break;
            }

            case QuoteSubIndicator.Dma:
            {
                var (dma, ama) = TechnicalIndicators.DMA(closes);
                ChartBuilder.AddLine(model, key, dma, "DMA", C1);
                ChartBuilder.AddLine(model, key, ama, "AMA", C2);
                ZeroLine();
                formatInfo = idx => new[] { Seg("DMA", Fmt(dma[idx]), C1), Seg("AMA", Fmt(ama[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(dma, ama);
                break;
            }

            case QuoteSubIndicator.Obv:
            {
                var (obv, obvMa) = TechnicalIndicators.OBV(closes, volumes);
                ChartBuilder.AddLine(model, key, obv, "OBV", C1);
                ChartBuilder.AddLine(model, key, obvMa, "OBVMA", C2);
                formatInfo = idx => new[] { Seg("OBV", Fmt0(obv[idx]), C1, 12), Seg("OBVMA", Fmt0(obvMa[idx]), C2, 12) };
                rangeFn = ChartBuilder.YRangeFn(obv, obvMa);
                break;
            }

            case QuoteSubIndicator.Vr:
            {
                var (vr, vrMa) = TechnicalIndicators.VR(closes, volumes);
                ChartBuilder.AddLine(model, key, vr, "VR", C1);
                ChartBuilder.AddLine(model, key, vrMa, "VRMA", C2);
                formatInfo = idx => new[] { Seg("VR", Fmt(vr[idx]), C1), Seg("VRMA", Fmt(vrMa[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(vr, vrMa);
                break;
            }

            case QuoteSubIndicator.Mfi:
            {
                var mfi = TechnicalIndicators.MFI(highs, lows, closes, volumes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, mfi, "MFI", C1);
                formatInfo = idx => new[] { Seg("MFI", Fmt(mfi[idx]), C1) };
                break;
            }

            case QuoteSubIndicator.Emv:
            {
                var (emv, emvMa) = TechnicalIndicators.EMV(highs, lows, volumes);
                ChartBuilder.AddLine(model, key, emv, "EMV", C1);
                ChartBuilder.AddLine(model, key, emvMa, "EMVMA", C2);
                ZeroLine();
                formatInfo = idx => new[] { Seg("EMV", Fmt(emv[idx]), C1), Seg("EMVMA", Fmt(emvMa[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(emv, emvMa);
                break;
            }

            case QuoteSubIndicator.Psy:
            {
                var (psy, psyMa) = TechnicalIndicators.PSY(closes);
                yAxis.Minimum = 0; yAxis.Maximum = 100;
                ChartBuilder.AddLine(model, key, psy, "PSY", C1);
                ChartBuilder.AddLine(model, key, psyMa, "PSYMA", C2);
                formatInfo = idx => new[] { Seg("PSY", Fmt(psy[idx]), C1), Seg("PSYMA", Fmt(psyMa[idx]), C2) };
                break;
            }

            case QuoteSubIndicator.Arbr:
            {
                var (ar, br) = TechnicalIndicators.ARBR(opens, highs, lows, closes);
                ChartBuilder.AddLine(model, key, ar, "AR", C1);
                ChartBuilder.AddLine(model, key, br, "BR", C2);
                formatInfo = idx => new[] { Seg("AR", Fmt(ar[idx]), C1), Seg("BR", Fmt(br[idx]), C2) };
                rangeFn = ChartBuilder.YRangeFn(ar, br);
                break;
            }

            default: // Asi
            {
                var (asi, asiMa) = TechnicalIndicators.ASI(opens, highs, lows, closes);
                ChartBuilder.AddLine(model, key, asi, "ASI", C1);
                ChartBuilder.AddLine(model, key, asiMa, "ASIMA", C2);
                ZeroLine();
                formatInfo = idx => new[] { Seg("ASI", Fmt(asi[idx]), C1), Seg("ASIMA", Fmt(asiMa[idx]), C2) };
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
