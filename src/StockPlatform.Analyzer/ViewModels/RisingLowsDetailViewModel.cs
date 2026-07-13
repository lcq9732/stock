using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"阶梯低点法" 的 DetailViewModel——3个面板(主图/MACD/成交量)。CriterionDisplay 复用
/// DetailViewModel.cs 里已有的定义。</summary>
public class RisingLowsDetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel MacdPlotModel { get; init; } = new();
    public PlotModel VolumePlotModel { get; init; } = new();

    public RisingLowsChartResult Chart { get; init; } = new();
}
