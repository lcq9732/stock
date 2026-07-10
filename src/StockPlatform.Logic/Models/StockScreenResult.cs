namespace StockPlatform.Logic.Models;

public class CriterionResult
{
    public string Name { get; set; } = "";
    public bool Satisfied { get; set; }
    public string Basis { get; set; } = "";

    /// <summary>True when this criterion is Satisfied=false specifically because a dependency the
    /// Fetcher hasn't populated yet (e.g. 流通市值/资金净流入) is missing — NOT because the
    /// condition was evaluated and genuinely not met. Lets a tab's "0 只满足条件" summary say
    /// "342 只因缺少流通市值数据被跳过" instead of silently looking identical to "checked, didn't
    /// qualify" (see MidCapPullbackAnalysisEngine/BottomReboundAnalysisEngine for the two current
    /// producers, and each XxxTabViewModel.RunAnalyzeAsync for the aggregation/log side).</summary>
    public bool DataMissing { get; set; }
}

/// <summary>Shared "how do the 3/5/7/10-rule engines turn a Criteria list into Passed" logic — a
/// criterion with DataMissing=true is excluded entirely rather than treated as failed, so a
/// dependency the Fetcher hasn't populated yet (流通市值/资金净流入) can't silently block an
/// otherwise-qualifying stock from ever showing up in the results grid. See each
/// XxxAnalysisEngine.Analyze for where these are called.</summary>
public static class CriteriaEvaluator
{
    /// <summary>AND of every criterion except the skipped (DataMissing) ones.</summary>
    public static bool AllSatisfiedIgnoringMissingData(this List<CriterionResult> criteria) =>
        criteria.Where(c => !c.DataMissing).All(c => c.Satisfied);

    /// <summary>"At least <paramref name="threshold"/> satisfied" (see 金叉法), counting only
    /// non-skipped criteria on both sides — a skipped criterion neither helps nor hurts.</summary>
    public static bool AtLeastSatisfiedIgnoringMissingData(this List<CriterionResult> criteria, int threshold) =>
        criteria.Where(c => !c.DataMissing).Count(c => c.Satisfied) >= threshold;
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

    /// <summary>The latest bar's date this result was computed from (i.e. "今天" as of the
    /// analysis run) and its closing price — not used by any of the 3/7/10-rule checks
    /// themselves, only recorded so a watchlist pick (see WatchlistEntry in
    /// StockPlatform.Analyzer) can say exactly which day's data/price it was based on, for later
    /// tracking whether the pick actually performed well.</summary>
    public DateTime? DataDate { get; set; }
    public double? LastClose { get; set; }

    /// <summary>Optional 0–100 quality/ranking score for methods where "passed" is a fuzzy match and
    /// candidates should be ranked rather than treated as equally good. Only 三角收敛
    /// (TriangleConvergenceAnalysisEngine) sets it today — its "收敛质量" (how tightly the two
    /// trendlines narrow + how well price stays between them); null for the other methods, whose
    /// results are all equally "passed all rules". The tab sorts its results grid by this desc.</summary>
    public double? SortScore { get; set; }
}
