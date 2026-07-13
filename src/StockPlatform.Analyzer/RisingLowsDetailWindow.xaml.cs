using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

public partial class RisingLowsDetailWindow : Window
{
    private readonly RisingLowsChartResult _chart;

    public RisingLowsDetailWindow(StockScreenResult result, List<Bar> bars)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _chart = RisingLowsChartBuilder.Build(bars);

        DataContext = new RisingLowsDetailViewModel
        {
            Title = $"{result.Code} {result.Name}（{result.Granularity}）",
            Criteria = result.Criteria.Select(CriterionDisplay.From).ToList(),
            MainPlotModel = _chart.Main,
            MacdPlotModel = _chart.Macd,
            VolumePlotModel = _chart.Volume,
            Chart = _chart,
        };

        MainPlot.Controller = CreatePlotController();
        MacdPlot.Controller = CreatePlotController();
        VolumePlot.Controller = CreatePlotController();

        MainPlot.MouseMove += OnPlotMouseMove;
        MacdPlot.MouseMove += OnPlotMouseMove;
        VolumePlot.MouseMove += OnPlotMouseMove;

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

        var axis = plotView switch
        {
            _ when ReferenceEquals(plotView, MainPlot) => _chart.MainDateAxis,
            _ when ReferenceEquals(plotView, MacdPlot) => _chart.MacdDateAxis,
            _ => _chart.VolumeDateAxis,
        };
        var position = e.GetPosition(plotView);
        var rawIndex = axis.InverseTransform(position.X);
        var idx = Math.Clamp((int)Math.Round(rawIndex), 0, _chart.Bars.Count - 1);
        var bar = _chart.Bars[idx];

        _chart.MainCrosshair.X = idx;
        _chart.MacdCrosshair.X = idx;
        _chart.VolumeCrosshair.X = idx;
        _chart.Main.InvalidatePlot(false);
        _chart.Macd.InvalidatePlot(false);
        _chart.Volume.InvalidatePlot(false);

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");

        MainInfoText.Text =
            $"日期:{bar.PeriodStart:yyyy-MM-dd}  开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}  MA5:{Fmt(_chart.Ma5[idx])}  MA10:{Fmt(_chart.Ma10[idx])}  MA20:{Fmt(_chart.Ma20[idx])}";
        MacdInfoText.Text =
            $"DIF:{Fmt(_chart.Dif[idx])}  DEA:{Fmt(_chart.Dea[idx])}  MACD柱:{Fmt(_chart.MacdHist[idx])}";
        var volMa5 = _chart.VolMa5[idx];
        var ratio = double.IsNaN(volMa5) || volMa5 == 0 ? "—" : (_chart.Volumes[idx] / volMa5).ToString("F2");
        VolumeInfoText.Text =
            $"成交量:{_chart.Volumes[idx]:F0}  5日均量:{Fmt(volMa5)}  比值:{ratio}";
    }
}
