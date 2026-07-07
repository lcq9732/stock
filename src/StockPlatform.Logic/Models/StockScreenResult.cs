namespace StockPlatform.Logic.Models;

public class CriterionResult
{
    public string Name { get; set; } = "";
    public bool Satisfied { get; set; }
    public string Basis { get; set; } = "";
}

/// <summary>
/// Result of applying the 3-rule checklist (see doc/analysis-app-design.md section 3.2) to one
/// stock at one user-chosen granularity. Each analysis run only ever targets a single granularity.
/// </summary>
public class StockScreenResult
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Granularity { get; set; } = "";
    public bool Passed { get; set; }
    public List<CriterionResult> Criteria { get; set; } = new();
    public string? Error { get; set; }
}
