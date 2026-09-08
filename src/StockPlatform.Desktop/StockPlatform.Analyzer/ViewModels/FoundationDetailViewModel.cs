using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"峰哥法"(一根K线贯穿MA5/MA10/MA20 + 三线粘合 + 低位)的 DetailViewModel——2个面板(主图K线 / 成交量)。
/// CriterionDisplay 复用 DetailViewModel.cs 里的定义。</summary>
public class FoundationDetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel VolumePlotModel { get; init; } = new();

    public FoundationChartResult Chart { get; init; } = new();
}
