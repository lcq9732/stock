using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

public class QuoteDetailViewModel
{
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel Sub1PlotModel { get; init; } = new();
    public PlotModel Sub2PlotModel { get; init; } = new();
}
