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

    /// <summary>近5年**平均**股息率（0.05=5%）——只有底仓法算，其它方法 null。
    /// 跟 <see cref="DividendYield"/> 并排看才有意义：当期股息率明显高于这个均值时，多半是含了
    /// 一次性大额分红，或者股价刚大跌（分母变小），两种都不该当成"每年都能拿到这么多"。</summary>
    public double? AvgDividendYield { get; set; }

    /// <summary>连续分红年数（按除权除息日所属年计，算法见 DividendMetrics.ConsecutiveYears）
    /// ——只有底仓法算，其它方法 null。底仓法的硬条件之一：底仓赌的是"未来还会不会继续分红"，
    /// 只看近12个月的股息率分不出"连分十年的电力股"和"去年头一回分红"的票。</summary>
    public int? ConsecutiveDividendYears { get; set; }

    /// <summary>派息趋势的**结论**（递增 / 持平 / 波动 / 中断）——只有底仓法算，其它方法 null。
    /// **只提示不过滤**：底仓最怕的不是股息率低一点，而是派息一年比一年少。
    /// 2026-08-20：这里原来装的是"波动｜22年0.510 23年0.430 …"整串，结果表一列放不下、
    /// 扫一眼也读不出重点；逐年明细改成 <see cref="AnnualDividends"/> 由条件详情里的柱状图呈现。</summary>
    public string? DividendTrend { get; set; }

    /// <summary>近5年逐年每股派息（升序，元/股）——只有底仓法填，条件详情里画柱状图用。
    /// 一眼能看出是稳步递增还是某年腰斩，比一行数字快得多。</summary>
    public List<(int Year, double PerShare)>? AnnualDividends { get; set; }

    /// <summary>近3年逐年归母净利（升序，元）——只有底仓法填，条件详情里画柱状图用。
    /// 结果表那一列只显示累计值（<see cref="ThreeYearCumProfit"/>），逐年看图。</summary>
    public List<(int Year, double NetProfit)>? AnnualProfits { get; set; }

    /// <summary>按当前股息率、达成"目标年化股息"所需投入的资金（元）——只有底仓法在用户填了目标
    /// 时才算，否则 null。这是"全压这一只"的口径，实际要除以计划配置的只数，见引擎里的执行参考。</summary>
    public double? RequiredCapitalForTarget { get; set; }

    /// <summary>近60个交易日 |当日涨跌| 的均值（0.03=3%）；没算的方法为 null。同样是结果表要
    /// 直接展示的列——它决定该配多大仓位（-10%止损在5%日波动下两三天就会被噪音打掉），
    /// 也是回调法区分底仓/主动仓的依据之一。</summary>
    public double? DailyVolatility { get; set; }

    /// <summary>盈利趋势的展示文本（如"25年 +2.53 / 3年累计 +5.88"，单位亿元；连亏会带 ⚠）。
    /// **只展示不过滤**——回测显示把它做成硬条件反而降低收益（见 IFinancialRepository
    /// .GetRecentAnnualNetProfitByCode 的说明），但回测样本排除了ST/退市股，测不出踩雷风险，
    /// 所以把这个信息摆在结果表里由用户自己判断。没算的方法为 null。</summary>
    public string? ProfitTrend { get; set; }

    /// <summary>最近3个完整年度归母净利润的累计值（元）；用于结果表排序和高亮。没算的方法为 null。</summary>
    public double? ThreeYearCumProfit { get; set; }

    /// <summary>该结果落在哪个回测档位的**短标签**（如"深跌 65% / 3650"= 档位名 + 历史胜率 +
    /// 样本数）；没分档的方法为 null。条件详情里已有完整说明，这里是给结果表当一列用的精简版——
    /// 不点开就能一眼看出这只票所处档位的历史胜率和样本量（样本量决定这个胜率有多可信）。</summary>
    public string? DepthBucket { get; set; }

    /// <summary>KDJ 子状态的展示文本（如"今日刚叉 K18 ⚠"/"延续 K46 最优区"）；只有短线法算。
    /// 为什么单独开一列：2026-08-18 起"当日刚金叉"不再否决入选（K>D 就算通过），但回测里它是
    /// 全表最差的一档，跟"金叉已延续几天"的风险完全不是一回事。既然不再用它过滤，就必须在结果表
    /// 里显式标出来，否则"严格组"三个字会把两种质量差很远的信号混成一样。没算的方法为 null。</summary>
    public string? KdjState { get; set; }

    /// <summary>可选的分类标签——同一个方法的结果分成用途不同的几组时用（目前只有回调法：
    /// "底仓"=高股息低波动、适合长期持有吃分红；"主动仓"=位置在回测验证档位内、适合到价就走。
    /// 其它方法为 null）。为什么要分：位置判断和用途判断是两回事，一只票"跌到位了"不代表
    /// 它适合长期拿，反过来高股息蓝筹常年在MA20上方、永远进不了回调信号但正是底仓该买的。</summary>
    public string? Category { get; set; }

    /// <summary>形态摘要（如"09-04｜位置12%｜间距0.85%｜振幅5.30%｜站上三线"）——只有峰哥法填，
    /// 其它方法 null。跟 <see cref="KdjState"/>/<see cref="DepthBucket"/> 同类：是结果表要直接
    /// 摆出来的一列，不点开"条件详情"就能看出这只票是哪天穿的三线、穿在什么位置、穿完站住没有。
    /// 峰哥法**不过滤方向**（阴阳都要，用户确认），所以阴/阳放在 <see cref="Category"/> 单独一列，
    /// 用户自己挑——实测阳线只比阴线略好（10日 +1.12% vs +0.93%），不足以做成硬条件。</summary>
    public string? PatternNote { get; set; }

    /// <summary>Optional 0–100 quality/ranking score for methods where "passed" is a fuzzy match and
    /// candidates should be ranked rather than treated as equally good. Only 三角收敛
    /// (TriangleConvergenceAnalysisEngine) sets it today — its "收敛质量" (how tightly the two
    /// trendlines narrow + how well price stays between them); null for the other methods, whose
    /// results are all equally "passed all rules". The tab sorts its results grid by this desc.</summary>
    public double? SortScore { get; set; }
}
