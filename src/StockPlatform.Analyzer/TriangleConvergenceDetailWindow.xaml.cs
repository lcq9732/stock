using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer;

public partial class TriangleConvergenceDetailWindow : Window
{
    private readonly ChartResult _chart;

    public TriangleConvergenceDetailWindow(StockScreenResult result, List<Bar> bars, int lookbackDays, int swingWindow)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        // 用跟 TriangleConvergenceAnalysisEngine 完全相同的参数重新找一次形态——两条趋势线永远
        // 跟判断依据文字描述的是同一组摆动点，不会出现图上画的线跟文字对不上的情况。
        var match = TriangleConvergenceDetector.TryFind(bars, bars.Count - 1, lookbackDays, swingWindow);
        _chart = TriangleConvergenceChartBuilder.Build(bars, lookbackDays, match);

        DataContext = new DetailViewModel
        {
            Title = $"{result.Code} {result.Name}（{result.Granularity}）",
            Criteria = result.Criteria.Select(CriterionDisplay.From).ToList(),
            MainPlotModel = _chart.Main,
            MacdPlotModel = _chart.Macd,
            Chart = _chart,
        };

        MainPlot.Controller = CreatePlotController();
        MacdPlot.Controller = CreatePlotController();

        MainPlot.MouseMove += OnPlotMouseMove;
        MacdPlot.MouseMove += OnPlotMouseMove;

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

        var axis = ReferenceEquals(plotView, MainPlot) ? _chart.MainDateAxis : _chart.MacdDateAxis;
        var position = e.GetPosition(plotView);
        var rawIndex = axis.InverseTransform(position.X);
        var idx = Math.Clamp((int)Math.Round(rawIndex), 0, _chart.Bars.Count - 1);
        var bar = _chart.Bars[idx];

        _chart.MainCrosshair.X = idx;
        _chart.MacdCrosshair.X = idx;
        _chart.Main.InvalidatePlot(false);
        _chart.Macd.InvalidatePlot(false);

        var dif = _chart.Dif[idx];
        var dea = _chart.Dea[idx];
        var hist = _chart.MacdHist[idx];
        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");

        MainInfoText.Text =
            $"日期:{bar.PeriodStart:yyyy-MM-dd}  开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}  涨跌幅:{bar.PctChange:F2}%";
        MacdInfoText.Text =
            $"DIF:{Fmt(dif)}  DEA:{Fmt(dea)}  MACD柱:{Fmt(hist)}";
    }
}
