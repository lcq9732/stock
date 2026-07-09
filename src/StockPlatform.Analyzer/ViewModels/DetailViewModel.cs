using OxyPlot;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

public class CriterionDisplay
{
    public string Icon { get; init; } = "";
    public string Name { get; init; } = "";
    public string Basis { get; init; } = "";

    /// <summary>"⚠跳过" for a DataMissing criterion — distinct from ✓/✗ so a skipped-for-missing-
    /// data condition never looks like it was actually evaluated and failed (see
    /// CriterionResult.DataMissing / CriteriaEvaluator). Every DetailWindow builds its sidebar
    /// list via this instead of inlining the Icon logic, so all 5 methods stay consistent.</summary>
    public static CriterionDisplay From(CriterionResult c) => new()
    {
        Icon = c.DataMissing ? "⚠跳过" : (c.Satisfied ? "✓" : "✗"),
        Name = c.Name,
        Basis = c.Basis,
    };
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
