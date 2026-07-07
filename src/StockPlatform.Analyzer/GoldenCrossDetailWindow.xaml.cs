using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

public partial class GoldenCrossDetailWindow : Window
{
    private readonly GoldenCrossChartResult _chart;

    public GoldenCrossDetailWindow(StockScreenResult result, List<Bar> bars)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _chart = GoldenCrossChartBuilder.Build(bars);

        DataContext = new GoldenCrossDetailViewModel
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
            KdjPlotModel = _chart.Kdj,
            RsiPlotModel = _chart.Rsi,
            VolumePlotModel = _chart.Volume,
            Chart = _chart,
        };

        MainPlot.Controller = CreatePlotController();
        MacdPlot.Controller = CreatePlotController();
        KdjPlot.Controller = CreatePlotController();
        RsiPlot.Controller = CreatePlotController();
        VolumePlot.Controller = CreatePlotController();

        MainPlot.MouseMove += OnPlotMouseMove;
        MacdPlot.MouseMove += OnPlotMouseMove;
        KdjPlot.MouseMove += OnPlotMouseMove;
        RsiPlot.MouseMove += OnPlotMouseMove;
        VolumePlot.MouseMove += OnPlotMouseMove;

        // 5个面板共用同一套横轴含义，只需要跟真实宽度校正一次（任意一个面板的宽度都一样，用主图的
        // 就够了）——同样的道理见 DetailWindow.xaml.cs 和 ChartBuilder 里的说明。
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
            _ when ReferenceEquals(plotView, KdjPlot) => _chart.KdjDateAxis,
            _ when ReferenceEquals(plotView, RsiPlot) => _chart.RsiDateAxis,
            _ => _chart.VolumeDateAxis,
        };
        var position = e.GetPosition(plotView);
        var rawIndex = axis.InverseTransform(position.X);
        var idx = Math.Clamp((int)Math.Round(rawIndex), 0, _chart.Bars.Count - 1);
        var bar = _chart.Bars[idx];

        _chart.MainCrosshair.X = idx;
        _chart.MacdCrosshair.X = idx;
        _chart.KdjCrosshair.X = idx;
        _chart.RsiCrosshair.X = idx;
        _chart.VolumeCrosshair.X = idx;
        _chart.Main.InvalidatePlot(false);
        _chart.Macd.InvalidatePlot(false);
        _chart.Kdj.InvalidatePlot(false);
        _chart.Rsi.InvalidatePlot(false);
        _chart.Volume.InvalidatePlot(false);

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");
        var dateLine = $"日期: {bar.PeriodStart:yyyy-MM-dd}";

        MainInfoText.Text =
            $"{dateLine}\n开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}\nMA5:{Fmt(_chart.Ma5[idx])}  MA10:{Fmt(_chart.Ma10[idx])}";
        MacdInfoText.Text =
            $"{dateLine}\nDIF:{Fmt(_chart.Dif[idx])}  DEA:{Fmt(_chart.Dea[idx])}  MACD柱:{Fmt(_chart.MacdHist[idx])}";
        KdjInfoText.Text =
            $"{dateLine}\nK:{Fmt(_chart.K[idx])}  D:{Fmt(_chart.D[idx])}";
        RsiInfoText.Text =
            $"{dateLine}\nRSI:{Fmt(_chart.RsiValues[idx])}";
        var volMa5 = _chart.VolMa5[idx];
        var ratio = double.IsNaN(volMa5) || volMa5 == 0 ? "—" : (_chart.Volumes[idx] / volMa5).ToString("F2");
        VolumeInfoText.Text =
            $"{dateLine}\n成交量:{_chart.Volumes[idx]:F0}  5日均量:{Fmt(volMa5)}  比值:{ratio}";
    }
}
