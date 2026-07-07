using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

public class CriterionDisplay
{
    public string Icon { get; init; } = "";
    public string Name { get; init; } = "";
    public string Basis { get; init; } = "";
}

public class DetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel MacdPlotModel { get; init; } = new();

    /// <summary>Everything DetailWindow.xaml.cs needs to wire up the synced hover crosshair — see
    /// ChartBuilder.ChartResult. Not exposed via the two PlotModel properties above since the
    /// crosshair/axis objects need direct manipulation, not data binding.</summary>
    public ChartResult Chart { get; init; } = new();
}
