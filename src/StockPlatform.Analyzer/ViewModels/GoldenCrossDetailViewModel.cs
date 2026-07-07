using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"金叉法" 版本的 DetailViewModel——5个PlotModel而不是2个，见
/// GoldenCrossChartBuilder.GoldenCrossChartResult。CriterionDisplay 复用 DetailViewModel.cs 里
/// 已有的定义，不需要重复一份。</summary>
public class GoldenCrossDetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel MacdPlotModel { get; init; } = new();
    public PlotModel KdjPlotModel { get; init; } = new();
    public PlotModel RsiPlotModel { get; init; } = new();
    public PlotModel VolumePlotModel { get; init; } = new();

    public GoldenCrossChartResult Chart { get; init; } = new();
}
