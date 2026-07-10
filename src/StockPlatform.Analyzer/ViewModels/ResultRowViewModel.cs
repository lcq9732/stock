using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

public class ResultRowViewModel : ISelectableRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>行业分类（如"白酒"/"集成电路制造"），不是交易所板块——见 IndustryClassifier。</summary>
    public string Board { get; init; } = "";
    public bool Passed { get; init; }
    public int SatisfiedCount { get; init; }
    public int TotalCount { get; init; }
    public string? Error { get; init; }
    public StockScreenResult Result { get; init; } = new();

    /// <summary>三角收敛的"收敛质量"评分（0~100，越高形态越标准）；其它方法为 null。用于三角收敛
    /// 结果表的排序和展示，见 TriangleConvergenceTabViewModel / StockScreenResult.SortScore。</summary>
    public double? SortScore { get; init; }
    public string ConvergenceQualityText => SortScore.HasValue ? SortScore.Value.ToString("F0") : "";

    /// <summary>Bound to the DataGrid's checkbox column — plain mutable property (no
    /// INotifyPropertyChanged) is enough since nothing needs to react live to a check/uncheck,
    /// it's only read when "加入自选" is clicked (see FoundationTabViewModel etc.).</summary>
    public bool IsSelected { get; set; }

    public static ResultRowViewModel From(StockScreenResult r) => new()
    {
        Code = r.Code,
        Name = string.IsNullOrEmpty(r.Name) ? r.Code : r.Name,
        Board = IndustryClassifier.GetIndustry(r.Code),
        Passed = r.Passed,
        SatisfiedCount = r.Criteria.Count(c => c.Satisfied),
        TotalCount = r.Criteria.Count,
        Error = r.Error,
        Result = r,
        SortScore = r.SortScore,
    };
}
