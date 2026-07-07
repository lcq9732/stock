using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

public partial class DetailWindow : Window
{
    private readonly ChartResult _chart;

    public DetailWindow(StockScreenResult result, List<Bar> bars, int lookback)
    {
        InitializeComponent();
        // Setting WindowState=Maximized here (or even in XAML) doesn't reliably stick — WPF
        // needs a completed layout pass first. Deferring to Loaded is the standard workaround.
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _chart = ChartBuilder.Build(bars, lookback);

        DataContext = new DetailViewModel
        {
            Title = $"{result.Code} {result.Name}（{result.Granularity}）",
            Criteria = result.Criteria.Select(c => new CriterionDisplay
            {
                Icon = c.Satisfied ? "✓" : "✗",
                Name = c.Name,
                Basis = c.Basis,
            }).ToList(),
            MainPlotModel = _chart.Main,
            MacdPlotModel = _chart.Macd,
            Chart = _chart,
        };

        // OxyPlot's own default mouse bindings should already cover pan/zoom, but they're
        // replaced here explicitly so "左右/上下拖动、滚轮缩放" is guaranteed rather than assumed:
        // left-drag pans (any direction), wheel zooms at the cursor, right-drag draws a zoom
        // rectangle, double-click resets. Each PlotView gets its own controller instance.
        MainPlot.Controller = CreatePlotController();
        MacdPlot.Controller = CreatePlotController();

        // 鼠标移到任意一张图上时，两张图联动显示同一天的十字线，并各自在自己图表左上角用中文
        // 显示相关数据（K线图显示OHLC/BOLL，MACD图显示DIF/DEA/MACD柱）——离图更近，不用来回看。
        MainPlot.MouseMove += OnPlotMouseMove;
        MacdPlot.MouseMove += OnPlotMouseMove;

        // ChartBuilder只能在窗口显示前猜一个宽度，日期标签的疏密由此校正——尤其是窗口默认最大化
        // 后，实际可用宽度比那个猜测值大很多，不校正的话很多本该有空间显示的日期会被跳过不显示。
        // 每次改变大小（包括还原/拖动改变窗口大小）都会再触发一次，保持疏密始终匹配实际空间。
        MainPlot.SizeChanged += (_, e) => _chart.UpdatePlotWidth(e.NewSize.Width);
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

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;
        if (_chart.Bars.Count == 0) return;

        // X *is* the bar index now (index-based LinearAxis, not DateTimeAxis — see
        // ChartBuilder), so finding "which bar is the mouse over" is just a round-and-clamp,
        // no search needed.
        var axis = ReferenceEquals(plotView, MainPlot) ? _chart.MainDateAxis : _chart.MacdDateAxis;
        var position = e.GetPosition(plotView);
        var rawIndex = axis.InverseTransform(position.X);
        var idx = Math.Clamp((int)Math.Round(rawIndex), 0, _chart.Bars.Count - 1);
        var bar = _chart.Bars[idx];

        _chart.MainCrosshair.X = idx;
        _chart.MacdCrosshair.X = idx;
        _chart.Main.InvalidatePlot(false);
        _chart.Macd.InvalidatePlot(false);

        var boll = _chart.BollMid[idx];
        var dif = _chart.Dif[idx];
        var dea = _chart.Dea[idx];
        var hist = _chart.MacdHist[idx];
        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");

        var dateLine = $"日期: {bar.PeriodStart:yyyy-MM-dd}";
        MainInfoText.Text =
            $"{dateLine}\n开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}\n涨跌幅:{bar.PctChange:F2}%   BOLL中轨:{Fmt(boll)}";
        MacdInfoText.Text =
            $"{dateLine}\nDIF:{Fmt(dif)}  DEA:{Fmt(dea)}  MACD柱:{Fmt(hist)}";
    }
}
