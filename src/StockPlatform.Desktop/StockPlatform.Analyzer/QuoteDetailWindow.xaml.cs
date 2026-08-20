using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

/// <summary>"行情详情"——典型股票APP样式的纯行情窗口，跟"条件详情"（DetailWindow/
/// GoldenCrossDetailWindow/等）是两回事：条件详情回答"这个方法为什么选中了它"，这个窗口只回答
/// "这只股票现在长什么样"，不依赖是哪个方法选的、甚至可以是没被任何方法选中的股票。K线粒度
/// （日K/周K/月K）和两个副图的指标（成交量/MACD/KDJ/RSI）都能在界面上手工切换，切换后整体
/// 重新 Build 一次图表——不做增量更新，简单、不容易漏状态（见 RebuildChart）。</summary>
public partial class QuoteDetailWindow : Window
{
    private readonly string _code;
    private readonly string _name;
    private readonly IBarRepository _barRepository;
    /// <summary>本地数据库文件——"其他数据"按钮要按表直接读十几张表，走
    /// <see cref="SqliteStockDossierReader"/> 而不是各仓储（原因见那个类的注释），所以这里需要路径。</summary>
    private readonly string _dbPath;
    private string _granularity = Granularity.Day;
    private QuoteChartResult _chart = new();

    // ── 斐波那契回撤位的状态（2026-08-17新增）──
    // 手动选点存的是**日期**不是下标：切日K/周K/月K后同一个下标指的完全是另一天，存下标会让
    // 用户选的那段在切粒度后跳到别处去。日期在哪个粒度下都能映射回正确的那根K线（见 IndexOfDate）。
    private DateTime? _fibManualA;
    private DateTime? _fibManualB;

    public QuoteDetailWindow(string code, string name, IBarRepository barRepository, string dbPath)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _code = code;
        _name = name;
        _barRepository = barRepository;
        _dbPath = dbPath;
        TitleText.Text = $"{code} {name}";

        SetQuoteHeader();
        // "叠加大盘"这几个字用叠加线本身的颜色——跟主图 MA/副图各指标"颜色标在字上"的做法一致，
        // 一眼就知道图上哪条线是它。色值只在 QuoteChartBuilder.OverlayColor 定义一处，不在XAML里写死。
        OverlayIndexCheck.Foreground = ToBrush(QuoteChartBuilder.OverlayColor);
        // 同理，"斐波那契"这几个字用回撤线本身的金色。
        FibCheck.Foreground = ToBrush(QuoteChartBuilder.FibColor);
        FibScopeCombo.SelectedIndex = 0;   // 默认短线60日，理由见 FibonacciRetracement.ShortLookback
        HighlightGranularityButton(DayButton);
        // 默认 MACD + KDJ（2026-08-07 由 成交量+MACD 改成这样）——短线法的入场判定就是看这两个
        // （MACD柱连续收窄、KDJ金叉延续），打开行情详情要能直接对上条件详情里写的数值。
        // 成交量仍可从下拉框切回来。
        Sub1IndicatorCombo.SelectedIndex = (int)QuoteSubIndicator.Macd;
        Sub2IndicatorCombo.SelectedIndex = (int)QuoteSubIndicator.Kdj;

        MainPlot.MouseMove += OnMainMouseMove;
        // Ctrl+左键 = 手动选斐波那契端点。用 Preview（隧道）事件抢在 OxyPlot 的控制器之前拿到点击，
        // 否则左键会先被 PanAt 吃掉；不按 Ctrl 时不拦截，平移/缩放照旧。
        MainPlot.PreviewMouseLeftButtonDown += OnMainLeftButtonDown;
        Sub1Plot.MouseMove += OnSubMouseMove;
        Sub2Plot.MouseMove += OnSubMouseMove;
        MainPlot.SizeChanged += (_, e) => _chart.UpdatePlotWidth(e.NewSize.Width);

        RebuildChart();
    }

    /// <summary>抬头的价格/涨跌幅 + 开/最高/最低/昨收/量/额/换手，固定按日线算，跟下面图表当前
    /// 显示的K线粒度无关——这是"现在的实际行情"，不是"图表这一根K线的数据"，所以只在构造时算
    /// 一次，不会跟着粒度切换重新算。"现手"（逐笔实时成交手数）没有放——那是需要实时逐笔数据
    /// 才有的东西，这套系统只有日/周/月线，没有数据来源，不硬凑一个假数字。</summary>
    private void SetQuoteHeader()
    {
        var dayBars = _barRepository.Query(_code, Granularity.Day);
        if (dayBars.Count == 0) return;

        var last = dayBars[^1];
        var prevClose = dayBars.Count > 1 ? dayBars[^2].Close : last.Close;
        var change = last.Close - prevClose;
        var changePct = prevClose == 0 ? 0 : change / prevClose * 100;
        // 深色主题：涨红跌青（跟通达信一致，青色在黑底上比深绿更清楚）。
        var color = change >= 0 ? Brushes.Red : new SolidColorBrush(Color.FromRgb(0, 210, 210));
        LatestCloseText.Text = last.Close.ToString("F2");
        LatestCloseText.Foreground = color;
        // 涨跌额/涨跌幅都补白到固定宽度——数字位数变化时这段不会伸缩，右边的按钮位置也就不会动。
        ChangeText.Text = $"{(change >= 0 ? "+" : "")}{change:F2}".PadLeft(7)
                          + $" ({(change >= 0 ? "+" : "")}{changePct:F2}%)".PadLeft(10);
        ChangeText.Foreground = color;
        // 开/最高/最低/昨收/量/额/换手 原来是这里单独一行静态显示的，2026-08-11 已并入下面那条
        // 跟随十字光标的信息栏（见 UpdateMainInfo）——不悬浮时它显示的就是最新一根，等价于原来那行。
    }

    /// <summary>OxyPlot 的颜色转 WPF 画刷——把"图上线条的颜色"直接用到文字上（均线值、副图指标值、
    /// 叠加大盘的名字和复选框），色值只在 QuoteChartBuilder 里定义一份，两边永远对得上。</summary>
    private static SolidColorBrush ToBrush(OxyColor c) => new(Color.FromRgb(c.R, c.G, c.B));

    private static string FormatLargeNumber(double v) => v switch
    {
        >= 1e8 => $"{v / 1e8:F2}亿",
        >= 1e4 => $"{v / 1e4:F2}万",
        _ => v.ToString("F0"),
    };

    private void GranularityButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        _granularity = (string)button.Tag switch
        {
            "week" => Granularity.Week,
            "month" => Granularity.Month,
            _ => Granularity.Day,
        };
        HighlightGranularityButton(button);
        RebuildChart();
    }

    private void HighlightGranularityButton(Button selected)
    {
        foreach (var b in new[] { DayButton, WeekButton, MonthButton })
            b.Background = ReferenceEquals(b, selected) ? new SolidColorBrush(Color.FromRgb(0xCC, 0xE5, 0xFF)) : SystemColors.ControlBrush;
    }

    /// <summary>"其他数据"——把这只标的在库里除K线之外的所有数据读出来另开窗口展示（见
    /// <see cref="StockDossierWindow"/>）。放到后台线程读：要扫十几张表，其中 MarginDetail /
    /// BoardMember / Lhb 的主键最左列都不是股票代码（分别是 trade_date / board_code / trade_date），
    /// 按代码查是全表扫，在几 GB 的库上可能要一两秒，卡在UI线程上会让窗口假死。</summary>
    private async void OtherDataButton_Click(object sender, RoutedEventArgs e)
    {
        OtherDataButton.IsEnabled = false;
        var previousCursor = Cursor;
        Cursor = Cursors.Wait;
        try
        {
            var dossier = await Task.Run(() => new SqliteStockDossierReader(_dbPath).Read(_code, _name));
            new StockDossierWindow(dossier) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取其他数据失败：{ex.Message}", "其他数据", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = previousCursor;
            OtherDataButton.IsEnabled = true;
        }
    }

    /// <summary>勾/取消"叠加大盘"——整体重建一次图表（跟切粒度/换副图指标同一套做法，不做增量）。</summary>
    private void OverlayIndexCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildChart();
    }

    /// <summary>
    /// 取要叠加的大盘指数K线：按代码判断该股属于哪个交易所（沪市→上证指数、深市→深证成指，见
    /// <see cref="IndexOverlayMatcher.PickFor"/>），再从本地库读那个指数**同粒度**的K线。
    ///
    /// 用同粒度而不是一律日线：看周K/月K时叠加线也得是周/月，否则两条线的时间尺度对不上。指数的
    /// 周/月线跟个股一样是本地聚合出来的，库里本来就有。
    /// 读不到（北交所没有对应指数、或本地还没抓过该指数）就返回 null，界面上不画、信息栏说明原因。
    /// </summary>
    private (string Name, List<Bar> Bars)? TryGetIndexOverlay()
    {
        if (OverlayIndexCheck.IsChecked != true) return null;
        var picked = IndexOverlayMatcher.PickFor(_code);
        if (picked == null) return null;
        var bars = _barRepository.Query(picked.Value.Symbol, _granularity);
        return bars.Count == 0 ? null : (picked.Value.Name, bars);
    }

    // ── 斐波那契回撤位 ──

    /// <summary>下拉框的四个选项：前三个是自动识别的回看窗口，第4个是手动选点。</summary>
    private const int FibScopeManual = 3;

    private void FibCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildChart();
    }

    private void FibScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // 切到"手动选点"时把上一次选的点清掉，从头开始选——留着旧点会让人以为下拉一切换就生效了。
        if (FibScopeCombo.SelectedIndex == FibScopeManual) { _fibManualA = null; _fibManualB = null; }
        // 勾选框还没勾的话，改这个下拉等于表达"我要用斐波那契"，顺手勾上，省一次点击。
        if (FibCheck.IsChecked != true) FibCheck.IsChecked = true;
        RebuildChart();
    }

    /// <summary>Ctrl+左键在主图上点两下选波段：第一下记起点，第二下记终点并重画。已经选满两点后
    /// 再点，就重新开始选（把这一下当成新的起点）——不用先去点"清除"。</summary>
    private void OnMainLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (FibScopeCombo.SelectedIndex != FibScopeManual) return;
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;

        var idx = IndexUnderMouse(plotView, _chart.MainDateAxis, e);
        if (idx == null) return;
        e.Handled = true;   // 别让这一下变成图表平移

        var date = _chart.Bars[idx.Value].PeriodStart;
        if (_fibManualA == null || _fibManualB != null) { _fibManualA = date; _fibManualB = null; }
        else _fibManualB = date;

        if (FibCheck.IsChecked != true) FibCheck.IsChecked = true;
        RebuildChart();
    }

    /// <summary>把一个日期映射回当前粒度下的K线下标——手动选的点存的是日期（见字段注释），
    /// 取最后一根 PeriodStart &lt;= 该日期的K线：周/月线的 PeriodStart 是那一周/月的第一天，
    /// 用户当初在日线上点的某一天正好落在它覆盖的区间里。</summary>
    private static int? IndexOfDate(IReadOnlyList<Bar> bars, DateTime date)
    {
        int found = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].PeriodStart <= date) found = i;
            else break;
        }
        return found >= 0 ? found : null;
    }

    /// <summary>本次要画的斐波那契波段：没勾就是 null；自动模式按下拉选的回看窗口识别；
    /// 手动模式要两个点都选好了才画（只选了一个时返回 null，信息栏会提示还差一个）。</summary>
    private FibSwing? ResolveFibSwing(List<Bar> bars)
    {
        if (FibCheck.IsChecked != true || bars.Count < 2) return null;

        if (FibScopeCombo.SelectedIndex == FibScopeManual)
        {
            if (_fibManualA == null || _fibManualB == null) return null;
            var a = IndexOfDate(bars, _fibManualA.Value);
            var b = IndexOfDate(bars, _fibManualB.Value);
            return a == null || b == null ? null : FibonacciRetracement.FromRange(bars, a.Value, b.Value);
        }

        int lookback = FibScopeCombo.SelectedIndex switch
        {
            1 => FibonacciRetracement.MediumLookback,
            2 => FibonacciRetracement.LongLookback,
            _ => FibonacciRetracement.ShortLookback,
        };
        // 周/月线上"60根"是60周/60个月，那是好几年——回看窗口是按交易日定义的，换算成当前粒度
        // 的根数，否则切到周K后画出来的波段跟日K完全不是一回事。
        int barsPerUnit = _granularity switch { Granularity.Week => 5, Granularity.Month => 21, _ => 1 };
        return FibonacciRetracement.FindSwing(bars, Math.Max(2, lookback / barsPerUnit));
    }

    /// <summary>斐波那契信息条——把图上那批线平铺成文字：波段端点、现价回撤到哪儿了、各档价位，
    /// 并把离现价最近的支撑/阻力单独标出来（【仓位计算器】的【从K线取值】取的就是这两条）。</summary>
    private void UpdateFibInfo(List<Bar> bars)
    {
        if (FibCheck.IsChecked != true)
        {
            FibInfoBorder.Visibility = Visibility.Collapsed;
            return;
        }

        FibInfoBorder.Visibility = Visibility.Visible;
        FibInfoText.Inlines.Clear();
        var fibBrush = ToBrush(QuoteChartBuilder.FibColor);
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

        void Add(string text, Brush brush, bool bold = false)
            => FibInfoText.Inlines.Add(new Run(text)
            {
                Foreground = brush,
                FontWeight = bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
            });

        if (_chart.Fib is not { } fib)
        {
            if (FibScopeCombo.SelectedIndex == FibScopeManual)
                Add(_fibManualA == null
                        ? "手动选点：按住 Ctrl 在主图上点第 1 下（波段起点）"
                        : $"手动选点：起点已选 {_fibManualA:yyyy-MM-dd}，按住 Ctrl 再点第 2 下（终点）",
                    fibBrush);
            else
                Add($"这段时间里没有幅度超过 {FibonacciRetracement.MinSwingPct:0}% 的波段——横盘行情画回撤位没有意义，" +
                    "换更长的回看窗口，或改用手动选点。", labelBrush);
            return;
        }

        double px = bars[^1].Close;
        double retraced = FibonacciRetracement.RetracedPct(fib, px);

        Add(fib.Describe(), fibBrush, bold: true);
        Add("  ┃  ", labelBrush);
        Add($"现价 {px:F2} 已{(fib.IsUpSwing ? "回撤" : "反弹")} {retraced:F1}%", ToBrush(QuoteChartBuilder.FibColor));
        if (retraced > 100)
            Add("（整段涨幅已吐光，这段的回撤位不再有参考意义）", Brushes.Orange);

        var support = FibonacciRetracement.SupportBelow(fib, px);
        var resistance = FibonacciRetracement.ResistanceAbove(fib, px);
        Add("  ┃  ", labelBrush);
        Add("下方支撑:", labelBrush);
        Add(support is { } s ? $"{s.Price:F2}（{s.Label}，−{(px - s.Price) / px * 100:F1}%）" : "—（已在最低档下方）",
            new SolidColorBrush(Color.FromRgb(0, 210, 210)));
        Add("   上方阻力:", labelBrush);
        Add(resistance is { } r ? $"{r.Price:F2}（{r.Label}，+{(r.Price - px) / px * 100:F1}%）" : "—（已在最高档上方）",
            Brushes.Red);

        FibInfoText.Inlines.Add(new LineBreak());
        Add("各档：", labelBrush);
        foreach (var lv in FibonacciRetracement.Levels(fib))
            Add($"{lv.Label} {lv.Price:F2}   ", fibBrush);
    }

    private void SubIndicatorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 两个下拉框共用这一个处理函数——不用管是哪个触发的，直接整体重新 Build 一次最简单，
        // 也顺便保证两个副图不会因为各自独立处理而漏同步横轴。窗口刚打开、组合框第一次赋值
        // SelectedIndex 时也会触发这个事件，此时 MainPlot 还没设过 Model，RebuildChart 内部的
        // 判断会跳过（见下）。
        if (!IsLoaded) return;
        RebuildChart();
    }

    private void RebuildChart()
    {
        var bars = _barRepository.Query(_code, _granularity);
        if (bars.Count == 0)
        {
            MainInfoText.Text = $"没有找到该股票的{GranularityLabel(_granularity)}数据。";
            return;
        }

        var sub1Kind = (QuoteSubIndicator)Sub1IndicatorCombo.SelectedIndex;
        var sub2Kind = (QuoteSubIndicator)Sub2IndicatorCombo.SelectedIndex;
        _chart = QuoteChartBuilder.Build(bars, sub1Kind, sub2Kind, TryGetIndexOverlay(), ResolveFibSwing(bars));

        DataContext = new QuoteDetailViewModel
        {
            MainPlotModel = _chart.Main,
            Sub1PlotModel = _chart.Sub1,
            Sub2PlotModel = _chart.Sub2,
        };

        MainPlot.Controller = CreatePlotController();
        Sub1Plot.Controller = CreatePlotController();
        Sub2Plot.Controller = CreatePlotController();

        UpdateFibInfo(bars);

        // 默认显示最新一根K线的数据（不用等鼠标悬浮），鼠标移到图表上之后会跟着变成悬浮那天的。
        UpdateMainInfo(bars.Count - 1);
        RenderSubInfo(Sub1InfoText, _chart.Sub1FormatInfo(bars.Count - 1));
        RenderSubInfo(Sub2InfoText, _chart.Sub2FormatInfo(bars.Count - 1));
    }

    /// <summary>把副图信息栏画成**带颜色的分段文字**（2026-08-11新增）——每段颜色跟图上那条线/那根柱子
    /// 一致（见 QuoteChartBuilder.InfoSegment），跟主图 MA5/MA10/MA20/MA60 的显示方式统一，这样不必去
    /// 对照右上角图例就知道哪个数字对应哪条线。段内的值已经补白到固定宽度，字体是等宽的，所以数值长短
    /// 变化时各字段位置不动。</summary>
    private static void RenderSubInfo(TextBlock target, IReadOnlyList<QuoteChartBuilder.InfoSegment> segments)
    {
        target.Inlines.Clear();
        foreach (var seg in segments)
            target.Inlines.Add(new Run(seg.Text)
            {
                Foreground = new SolidColorBrush(Color.FromRgb(seg.Color.R, seg.Color.G, seg.Color.B)),
            });
    }

    private static string GranularityLabel(string granularity) => granularity switch
    {
        Granularity.Week => "周线",
        Granularity.Month => "月线",
        _ => "日线",
    };

    // 两个副图表头右侧原来有一排"色块+名称"的图例，2026-08-11 整套删掉了（连同
    // QuoteChartBuilder.LegendFor）——信息栏里每个数值已经用它在图上那条线/柱子的颜色显示，
    // 图例是重复信息，还占掉表头右侧一段宽度。

    private static PlotController CreatePlotController()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        controller.BindMouseDown(OxyMouseButton.Left, PlotCommands.PanAt);
        controller.BindMouseWheel(PlotCommands.ZoomWheel);
        controller.BindMouseDown(OxyMouseButton.Right, PlotCommands.ZoomRectangle);
        controller.BindMouseDown(OxyMouseButton.Left, OxyModifierKeys.None, 2, PlotCommands.ResetAt);
        return controller;
    }

    private int? IndexUnderMouse(OxyPlot.Wpf.PlotView plotView, LinearAxis axis, MouseEventArgs e)
    {
        if (_chart.Bars.Count == 0) return null;
        var position = e.GetPosition(plotView);
        var rawIndex = axis.InverseTransform(position.X);
        return Math.Clamp((int)Math.Round(rawIndex), 0, _chart.Bars.Count - 1);
    }

    /// <summary>主图表头那一行——日期+OHLC + MA5/10/20/60当前值，每条MA用自己线的颜色，
    /// 后面跟涨跌箭头（比前一根K线的MA值高就↑涨红，低就↓跌绿）。默认（RebuildChart刚构建完）
    /// 传最新一根K线的下标，鼠标悬浮时传悬浮那根的下标——同一个函数，不用区分调用来源。</summary>
    private void UpdateMainInfo(int idx)
    {
        var bar = _chart.Bars[idx];
        // 值一律右对齐补白到固定字符宽（信息栏用等宽字体 Consolas）——标签本身宽度是常量，所以数字
        // 从 5.6 变成 -12.34 时各字段的横向位置不动，不会整排左右跳（用户 2026-08-11 反馈的问题）。
        const int W = 8;                       // 价格/涨跌幅这类
        const int WQ = 9;                      // 量/额（"6.33万"/"4.56亿"）
        string Fmt(double v) => (double.IsNaN(v) ? "—" : v.ToString("F2")).PadLeft(W);
        string Arrow(double[] ma) =>
            // 取不到方向时补一个空格而不是空串——否则这一格宽度会变、后面的均线跟着挪。
            idx > 0 && !double.IsNaN(ma[idx]) && !double.IsNaN(ma[idx - 1]) ? (ma[idx] >= ma[idx - 1] ? "↑" : "↓") : " ";
        Brush ArrowColor(double[] ma) =>
            idx > 0 && !double.IsNaN(ma[idx]) && !double.IsNaN(ma[idx - 1]) && ma[idx] < ma[idx - 1] ? Brushes.Green : Brushes.Red;

        var labelBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        var valueBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

        void Add(string label, string value, Brush? valueColor = null)
        {
            MainInfoText.Inlines.Add(new Run($"{label}:") { Foreground = labelBrush });
            MainInfoText.Inlines.Add(new Run($"{value}  ") { Foreground = valueColor ?? valueBrush });
        }
        void AddMaRun(string label, double[] ma, OxyColor lineColor)
        {
            MainInfoText.Inlines.Add(new Run($"{label}:{Fmt(ma[idx])}") { Foreground = ToBrush(lineColor) });
            MainInfoText.Inlines.Add(new Run($"{Arrow(ma)}  ") { Foreground = ArrowColor(ma) });
        }

        // 涨跌幅现算（不再有存储字段）：这一根的收盘相对前一根收盘。
        double prevClose = idx > 0 ? _chart.Bars[idx - 1].Close : bar.Close;
        double pctChg = idx > 0 && prevClose > 0 ? (bar.Close - prevClose) / prevClose * 100 : 0;
        var chgBrush = pctChg >= 0 ? Brushes.Red : (Brush)new SolidColorBrush(Color.FromRgb(0, 210, 210));

        MainInfoText.Inlines.Clear();
        // 第一组：这一根K线自己的事实（日期打头——合并成一行后必须点明"这些数字是哪天的"）
        Add("日期", bar.PeriodStart.ToString("yyyy-MM-dd"));
        Add("开", Fmt(bar.Open));
        Add("最高", Fmt(bar.High));
        Add("最低", Fmt(bar.Low));
        Add("收", Fmt(bar.Close));
        Add("昨收", Fmt(prevClose));
        Add("量", FormatLargeNumber(bar.Volume).PadLeft(WQ));
        Add("额", FormatLargeNumber(bar.Amount).PadLeft(WQ));
        Add("换手", (bar.Turnover.ToString("F2") + "%").PadLeft(W));
        // 明显的分隔：竖线 + 两侧留白，把"这一根的行情"和"派生指标"分开
        // FontWeights 在 OxyPlot 和 System.Windows 里同名，这里必须写全名消歧义（本文件同时 using 了两者）
        MainInfoText.Inlines.Add(new Run("  ┃   ")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            FontWeight = System.Windows.FontWeights.Bold,
        });
        // 第二组：派生值——涨跌幅 + 四条均线（颜色跟图上的线一致）
        Add("涨跌幅", (pctChg.ToString("F2") + "%").PadLeft(W), chgBrush);
        AddMaRun("MA5", _chart.Ma5, QuoteChartBuilder.Ma5Color);
        AddMaRun("MA10", _chart.Ma10, QuoteChartBuilder.Ma10Color);
        AddMaRun("MA20", _chart.Ma20, QuoteChartBuilder.Ma20Color);
        AddMaRun("MA60", _chart.Ma60, QuoteChartBuilder.Ma60Color);

        // 叠加了大盘就报"相对强弱"——叠加线已经等比缩放到个股的价格刻度、且跟K线从可见区间左边缘的
        // 同一点出发，所以 个股收盘/叠加值-1 就是"这段时间比大盘多涨/少涨了多少"，正=跑赢。
        // 这是叠加功能真正要看的那个数字，光看两条线的位置只能定性、这里给出定量。
        if (_chart.OverlayName != null && _chart.OverlayScaled is { } ovl
            && idx < ovl.Length && !double.IsNaN(ovl[idx]) && ovl[idx] > 0)
        {
            double rs = (bar.Close - ovl[idx]) / ovl[idx] * 100;
            MainInfoText.Inlines.Add(new Run("  vs ") { Foreground = labelBrush });
            MainInfoText.Inlines.Add(new Run(_chart.OverlayName)
            {
                Foreground = ToBrush(QuoteChartBuilder.OverlayColor),   // 跟叠加线/复选框同一个色值来源
            });
            MainInfoText.Inlines.Add(new Run(" 强弱:") { Foreground = labelBrush });
            MainInfoText.Inlines.Add(new Run($"{(rs >= 0 ? "+" : "")}{rs:F2}%".PadLeft(W))
            {
                Foreground = rs >= 0 ? Brushes.Red : (Brush)new SolidColorBrush(Color.FromRgb(0, 210, 210)),
                FontWeight = System.Windows.FontWeights.Bold,
            });
        }
    }

    private void OnMainMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;
        var idx = IndexUnderMouse(plotView, _chart.MainDateAxis, e);
        if (idx == null) return;
        double price = _chart.MainYAxis.InverseTransform(e.GetPosition(plotView).Y);
        MoveCrosshairAndUpdateInfo(idx.Value, _chart.MainHairY, price);
    }

    // 副图1/2共用这一个处理函数（跟主图分开是因为它们各自的横/纵轴实例不同，需要先认出是哪个
    // PlotView 才能拿对应的轴）——十字线（横竖两条）和信息文字联动更新。
    private void OnSubMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;
        bool isSub1 = ReferenceEquals(plotView, Sub1Plot);
        var dateAxis = isSub1 ? _chart.Sub1DateAxis : _chart.Sub2DateAxis;
        var yAxis = isSub1 ? _chart.Sub1YAxis : _chart.Sub2YAxis;
        var hairY = isSub1 ? _chart.Sub1HairY : _chart.Sub2HairY;
        var idx = IndexUnderMouse(plotView, dateAxis, e);
        if (idx == null) return;
        double value = yAxis.InverseTransform(e.GetPosition(plotView).Y);
        MoveCrosshairAndUpdateInfo(idx.Value, hairY, value);
    }

    /// <summary>竖线(时间)三个面板一起动；横线(价格/数值)只画在鼠标当前所在的那个面板上
    /// （<paramref name="activeHairY"/>），其它两个面板的横线清空——跟通用软件的十字光标一致。</summary>
    private void MoveCrosshairAndUpdateInfo(int idx, LineAnnotation activeHairY, double activeValue)
    {
        _chart.MainCrosshair.X = idx;
        _chart.Sub1Crosshair.X = idx;
        _chart.Sub2Crosshair.X = idx;

        _chart.MainHairY.Y = QuoteChartBuilder.QuoteChartHiddenY;
        _chart.Sub1HairY.Y = QuoteChartBuilder.QuoteChartHiddenY;
        _chart.Sub2HairY.Y = QuoteChartBuilder.QuoteChartHiddenY;
        activeHairY.Y = activeValue;

        _chart.Main.InvalidatePlot(false);
        _chart.Sub1.InvalidatePlot(false);
        _chart.Sub2.InvalidatePlot(false);

        UpdateMainInfo(idx);
        RenderSubInfo(Sub1InfoText, _chart.Sub1FormatInfo(idx));
        RenderSubInfo(Sub2InfoText, _chart.Sub2FormatInfo(idx));
    }
}
