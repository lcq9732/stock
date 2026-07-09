using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

public partial class BottomReboundDetailWindow : Window
{
    private readonly BottomReboundChartResult _chart;

    public BottomReboundDetailWindow(StockScreenResult result, List<Bar> bars, double difThreshold)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _chart = BottomReboundChartBuilder.Build(bars, difThreshold);

        DataContext = new BottomReboundDetailViewModel
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

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");

        MainInfoText.Text =
            $"日期:{bar.PeriodStart:yyyy-MM-dd}  开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}  MA5:{Fmt(_chart.Ma5[idx])}  MA10:{Fmt(_chart.Ma10[idx])}  MA20:{Fmt(_chart.Ma20[idx])}";
        MacdInfoText.Text =
            $"DIF:{Fmt(_chart.Dif[idx])}  DEA:{Fmt(_chart.Dea[idx])}  MACD柱:{Fmt(_chart.MacdHist[idx])}";
    }
}
