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

    /// <summary>近12个月已实施派息算出的股息率（0.05=5%）；没算的方法为 null。放在这里而不是
    /// 只写进 Criteria 文字里，是因为它是结果表要直接展示、用户扫一眼就要比较的列（见回调法）。</summary>
    public double? DividendYield { get; set; }

    /// <summary>近60个交易日 |当日涨跌| 的均值（0.03=3%）；没算的方法为 null。同样是结果表要
    /// 直接展示的列——它决定该配多大仓位（-10%止损在5%日波动下两三天就会被噪音打掉），
    /// 也是回调法区分底仓/主动仓的依据之一。</summary>
    public double? DailyVolatility { get; set; }

    /// <summary>该结果落在哪个回测档位的**短标签**（如"深跌 65% / 3650"= 档位名 + 历史胜率 +
    /// 样本数）；没分档的方法为 null。条件详情里已有完整说明，这里是给结果表当一列用的精简版——
    /// 不点开就能一眼看出这只票所处档位的历史胜率和样本量（样本量决定这个胜率有多可信）。</summary>
    public string? DepthBucket { get; set; }

    /// <summary>可选的分类标签——同一个方法的结果分成用途不同的几组时用（目前只有回调法：
    /// "底仓"=高股息低波动、适合长期持有吃分红；"主动仓"=位置在回测验证档位内、适合到价就走。
    /// 其它方法为 null）。为什么要分：位置判断和用途判断是两回事，一只票"跌到位了"不代表
    /// 它适合长期拿，反过来高股息蓝筹常年在MA20上方、永远进不了回调信号但正是底仓该买的。</summary>
    public string? Category { get; set; }

    /// <summary>Optional 0–100 quality/ranking score for methods where "passed" is a fuzzy match and
    /// candidates should be ranked rather than treated as equally good. Only 三角收敛
    /// (TriangleConvergenceAnalysisEngine) sets it today — its "收敛质量" (how tightly the two
    /// trendlines narrow + how well price stays between them); null for the other methods, whose
    /// results are all equally "passed all rules". The tab sorts its results grid by this desc.</summary>
    public double? SortScore { get; set; }
}
