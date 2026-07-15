using OxyPlot;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"彬哥法"（原名中盘起爆法）版本的 DetailViewModel——3个PlotModel（日线主图+周线MACD+月线MACD），见
/// MidCapPullbackChartBuilder.MidCapPullbackChartResult。CriterionDisplay 复用 DetailViewModel.cs
/// 里已有的定义。</summary>
public class MidCapPullbackDetailViewModel
{
    public string Title { get; init; } = "";
    public List<CriterionDisplay> Criteria { get; init; } = new();
    public PlotModel MainPlotModel { get; init; } = new();
    public PlotModel WeekMacdPlotModel { get; init; } = new();
    public PlotModel MonthMacdPlotModel { get; init; } = new();

    public MidCapPullbackChartResult Chart { get; init; } = new();
}
