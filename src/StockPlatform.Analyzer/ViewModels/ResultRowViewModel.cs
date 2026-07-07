using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

public class ResultRowViewModel
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
    };
}
