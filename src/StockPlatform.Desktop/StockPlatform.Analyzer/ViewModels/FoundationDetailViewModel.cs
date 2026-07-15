using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"峰哥法"(涨停+持续放量)的 DetailViewModel——2个面板(主图K线 / 成交量)。
/// CriterionDisplay 复用 DetailViewModel.cs 里的定义。</summary>
public class FoundationDetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel VolumePlotModel { get; init; } = new();

    public FoundationChartResult Chart { get; init; } = new();
}
