using System.Windows;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

public partial class MidCapPullbackDetailWindow : Window
{
    private readonly MidCapPullbackChartResult _chart;

    public MidCapPullbackDetailWindow(StockScreenResult result, List<Bar> dayBars, List<Bar> weekBars, List<Bar> monthBars)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowState = WindowState.Maximized;

        _chart = MidCapPullbackChartBuilder.Build(dayBars, weekBars, monthBars, result.Code, result.Name);

        DataContext = new MidCapPullbackDetailViewModel
        {
            Title = $"{result.Code} {result.Name}（{result.Granularity}）",
            Criteria = result.Criteria.Select(CriterionDisplay.From).ToList(),
            MainPlotModel = _chart.Main,
            WeekMacdPlotModel = _chart.WeekMacd,
            MonthMacdPlotModel = _chart.MonthMacd,
            Chart = _chart,
        };

        MainPlot.Controller = CreatePlotController();
        WeekMacdPlot.Controller = CreatePlotController();
        MonthMacdPlot.Controller = CreatePlotController();

        // 三个面板周期不同（日/周/月），没法像其它方法那样联动同一根横轴，各自独立响应鼠标。
        MainPlot.MouseMove += OnMainMouseMove;
        WeekMacdPlot.MouseMove += OnWeekMouseMove;
        MonthMacdPlot.MouseMove += OnMonthMouseMove;

        MainPlot.SizeChanged += (_, e) => _chart.UpdateMainWidth(e.NewSize.Width);
        WeekMacdPlot.SizeChanged += (_, e) => _chart.UpdateWeekWidth(e.NewSize.Width);
        MonthMacdPlot.SizeChanged += (_, e) => _chart.UpdateMonthWidth(e.NewSize.Width);
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

    private static int IndexUnderMouse(OxyPlot.Wpf.PlotView plotView, LinearAxis axis, MouseEventArgs e, int count)
    {
        var position = e.GetPosition(plotView);
        var rawIndex = axis.InverseTransform(position.X);
        return Math.Clamp((int)Math.Round(rawIndex), 0, count - 1);
    }

    private void OnMainMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;
        if (_chart.DayBars.Count == 0) return;
        var idx = IndexUnderMouse(plotView, _chart.MainDateAxis, e, _chart.DayBars.Count);
        var bar = _chart.DayBars[idx];

        _chart.MainCrosshair.X = idx;
        _chart.Main.InvalidatePlot(false);

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F2");
        MainInfoText.Text =
            $"日期:{bar.PeriodStart:yyyy-MM-dd}  开:{bar.Open:F2}  高:{bar.High:F2}  低:{bar.Low:F2}  收:{bar.Close:F2}  MA15:{Fmt(_chart.Ma15[idx])}";
    }

    private void OnWeekMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;
        if (_chart.WeekBars.Count == 0) return;
        var idx = IndexUnderMouse(plotView, _chart.WeekMacdDateAxis, e, _chart.WeekBars.Count);
        var bar = _chart.WeekBars[idx];

        _chart.WeekMacdCrosshair.X = idx;
        _chart.WeekMacd.InvalidatePlot(false);

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F3");
        WeekMacdInfoText.Text =
            $"DIF:{Fmt(_chart.WeekDif[idx])}  DEA:{Fmt(_chart.WeekDea[idx])}  MACD柱:{Fmt(_chart.WeekMacdHist[idx])}";
    }

    private void OnMonthMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not OxyPlot.Wpf.PlotView plotView) return;
        if (_chart.MonthBars.Count == 0) return;
        var idx = IndexUnderMouse(plotView, _chart.MonthMacdDateAxis, e, _chart.MonthBars.Count);
        var bar = _chart.MonthBars[idx];

        _chart.MonthMacdCrosshair.X = idx;
        _chart.MonthMacd.InvalidatePlot(false);

        string Fmt(double v) => double.IsNaN(v) ? "—" : v.ToString("F3");
        MonthMacdInfoText.Text =
            $"DIF:{Fmt(_chart.MonthDif[idx])}  DEA:{Fmt(_chart.MonthDea[idx])}  MACD柱:{Fmt(_chart.MonthMacdHist[idx])}";
    }
}
