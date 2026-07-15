using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"耀哥法"（原名触底回升法）版本的 DetailViewModel——2个PlotModel，见
/// BottomReboundChartBuilder.BottomReboundChartResult。CriterionDisplay 复用 DetailViewModel.cs
/// 里已有的定义。</summary>
public class BottomReboundDetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel MacdPlotModel { get; init; } = new();

    public BottomReboundChartResult Chart { get; init; } = new();
}
