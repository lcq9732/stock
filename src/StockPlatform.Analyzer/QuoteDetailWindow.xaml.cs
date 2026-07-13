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
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

/// <summary>"行情详情"——典型股票APP样式的纯行情窗口，跟"条件详情"（DetailWindow/
/// GoldenCrossDetailWindow/等）是两回事：条件详情回答"这个方法为什么选中了它"，这个窗口只回答
/// "这只股票现在长什么样"，不依赖是哪个方法选的、甚至可以是没被任何方法选中的股票。K线粒度
/// （日K/周K/月K）和两个副图的指标（成交量/MACD/KDJ/RSI）都能在界面上手工切换，切换后整体
/// 重新 Build 一次图表——不做增量更新，简单、不容易漏状态（见 RebuildChart）。</summary>
public partial class QuoteDetailWindow : Window
{
    private readonly string _code;
    private readonly IBarRepository _barRepository;
    private string _granularity = Granularity.Day;
    private QuoteChartResult _chart = new();

    public QuoteDetailWindow(string code, string name, IBarRepository barRepository)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _code = code;
        _barRepository = barRepository;
        TitleText.Text = $"{code} {name}";

        SetQuoteHeader();
        HighlightGranularityButton(DayButton);
        Sub1IndicatorCombo.SelectedIndex = (int)QuoteSubIndicator.Volume;
        Sub2IndicatorCombo.SelectedIndex = (int)QuoteSubIndicator.Macd;

        MainPlot.MouseMove += OnMainMouseMove;
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
        ChangeText.Text = $"{(change >= 0 ? "+" : "")}{change:F2} ({(change >= 0 ? "+" : "")}{changePct:F2}%)";
        ChangeText.Foreground = color;

        OpenText.Text = last.Open.ToString("F2");
        HighText.Text = last.High.ToString("F2");
        LowText.Text = last.Low.ToString("F2");
        PrevCloseText.Text = prevClose.ToString("F2");
        VolumeText.Text = FormatLargeNumber(last.Volume);
        AmountText.Text = FormatLargeNumber(last.Amount);
        TurnoverText.Text = $"{last.Turnover:F2}%";
    }

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
        _chart = QuoteChartBuilder.Build(bars, sub1Kind, sub2Kind);

        DataContext = new QuoteDetailViewModel
        {
            MainPlotModel = _chart.Main,
            Sub1PlotModel = _chart.Sub1,
            Sub2PlotModel = _chart.Sub2,
        };

        MainPlot.Controller = CreatePlotController();
        Sub1Plot.Controller = CreatePlotController();
        Sub2Plot.Controller = CreatePlotController();

        SetLegend(Sub1LegendPanel, QuoteChartBuilder.LegendFor(sub1Kind));
        SetLegend(Sub2LegendPanel, QuoteChartBuilder.LegendFor(sub2Kind));

        // 默认显示最新一根K线的数据（不用等鼠标悬浮），鼠标移到图表上之后会跟着变成悬浮那天的。
        UpdateMainInfo(bars.Count - 1);
        Sub1InfoText.Text = _chart.Sub1FormatInfo(bars.Count - 1);
        Sub2InfoText.Text = _chart.Sub2FormatInfo(bars.Count - 1);
    }

    private static string GranularityLabel(string granularity) => granularity switch
    {
        Granularity.Week => "周线",
        Granularity.Month => "月线",
        _ => "日线",
    };

    private static void SetLegend(StackPanel panel, (string Label, OxyColor Color)[] items)
    {
        panel.Children.Clear();
        for (int i = 0; i < items.Length; i++)
        {
            var (label, color) = items[i];
            panel.Children.Add(new Rectangle
            {
                Width = 14,
                Height = 3,
                Fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), // 深色表头上的浅色图例文字
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, i == items.Length - 1 ? 0 : 10, 0),
            });
        }
    }

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
        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");
        string Arrow(double[] ma) =>
            idx > 0 && !double.IsNaN(ma[idx]) && !double.IsNaN(ma[idx - 1]) ? (ma[idx] >= ma[idx - 1] ? "↑" : "↓") : "";
        Brush ArrowColor(double[] ma) =>
            idx > 0 && !double.IsNaN(ma[idx]) && !double.IsNaN(ma[idx - 1]) && ma[idx] < ma[idx - 1] ? Brushes.Green : Brushes.Red;
        SolidColorBrush ToBrush(OxyColor c) => new(Color.FromRgb(c.R, c.G, c.B));

        void AddMaRun(string label, double[] ma, OxyColor lineColor)
        {
            MainInfoText.Inlines.Add(new Run($"{label}:{Fmt(ma[idx])}") { Foreground = ToBrush(lineColor) });
            MainInfoText.Inlines.Add(new Run($"{Arrow(ma)}  ") { Foreground = ArrowColor(ma) });
        }

        MainInfoText.Inlines.Clear();
        MainInfoText.Inlines.Add(new Run(
            $"日期:{bar.PeriodStart:yyyy-MM-dd}  开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}  涨跌幅:{bar.PctChange:F2}%  "));
        AddMaRun("MA5", _chart.Ma5, QuoteChartBuilder.Ma5Color);
        AddMaRun("MA10", _chart.Ma10, QuoteChartBuilder.Ma10Color);
        AddMaRun("MA20", _chart.Ma20, QuoteChartBuilder.Ma20Color);
        AddMaRun("MA60", _chart.Ma60, QuoteChartBuilder.Ma60Color);
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
        Sub1InfoText.Text = _chart.Sub1FormatInfo(idx);
        Sub2InfoText.Text = _chart.Sub2FormatInfo(idx);
    }
}
