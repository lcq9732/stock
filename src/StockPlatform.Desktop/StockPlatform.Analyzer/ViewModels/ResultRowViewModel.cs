using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

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

    /// <summary>分析当天的收盘价（来自 StockScreenResult.LastClose）——回调法的结果表要用它
    /// 直接列出两个止盈目标价和止损价，省得每只都点开"条件详情"看。</summary>
    public double? LastClose { get; init; }

    /// <summary>可选的用途分类；目前没有方法在用（原回调法的"底仓/主动仓"分类随该方法改造成
    /// 底仓法一起去掉了——底仓法的结果全是底仓，分类列没有意义）。见 StockScreenResult.Category。</summary>
    public string Category { get; init; } = "";

    /// <summary>股息率（近12个月已实施派息 ÷ 现价）；没算的方法显示空。</summary>
    public double? DividendYield { get; init; }
    public string DividendYieldText => DividendYield.HasValue ? $"{DividendYield.Value * 100:F2}%" : "";

    // ── 以下四个是底仓法专用（跟 SortScore 一样，方法专属字段挂在共享行模型上）──

    /// <summary>近5年平均股息率。跟 <see cref="DividendYieldText"/> 并排显示：当期明显高出一截
    /// 时（这里超过均值1.5倍就标 ⚠），多半含一次性大额分红或股价刚大跌，不是可持续的水平。</summary>
    public double? AvgDividendYield { get; init; }
    public string AvgDividendYieldText => AvgDividendYield.HasValue
        ? $"{AvgDividendYield.Value * 100:F2}%" +
          (DividendYield.HasValue && AvgDividendYield.Value > 0 &&
           DividendYield.Value > AvgDividendYield.Value * 1.5 ? " ⚠" : "")
        : "";

    /// <summary>连续分红年数——底仓法的硬条件之一（≥5年）。</summary>
    public int? ConsecutiveDividendYears { get; init; }
    public string ConsecutiveDividendYearsText => ConsecutiveDividendYears.HasValue
        ? $"{ConsecutiveDividendYears.Value} 年"
        : "";

    /// <summary>派息趋势（"递增｜22年0.320 23年0.350 …"）。只展示不过滤。</summary>
    public string DividendTrend { get; init; } = "";

    /// <summary>达成目标年化股息所需投入（万元）——"全压这一只"的口径，用户没填目标时为空。</summary>
    public double? RequiredCapitalForTarget { get; init; }
    public string RequiredCapitalText => RequiredCapitalForTarget is > 0
        ? $"{RequiredCapitalForTarget.Value / 1e4:F1} 万"
        : "";

    /// <summary>档位标签："档位名 胜率% / 样本数"，见 StockScreenResult.DepthBucket。
    /// 样本数一起显示是有意的——超跌档78%的胜率只建立在319个样本上，别只看胜率。</summary>
    public string DepthBucket { get; init; } = "";

    /// <summary>KDJ 子状态（如"今日刚叉 K18 ⚠"/"延续 K46 最优区"）；只有短线法有值。
    /// 单独一列的原因见 StockScreenResult.KdjState：当日刚金叉已不再否决入选，但它是回测里最差的
    /// 一档，不显式标出来就会跟"金叉延续中"混成同一个"严格组"。</summary>
    public string KdjState { get; init; } = "";
    /// <summary>当日刚金叉——结果表里标红。跟 IsBaseHolding 一样从文本判，标记由
    /// ShortTermAnalysisEngine.KdjStateLabel 产生，改文案时两边一起改。</summary>
    public bool IsKdjFreshCross => KdjState.Contains("刚叉");
    /// <summary>金叉延续且K在40~60——四档里最稳的一档，标绿加粗。</summary>
    public bool IsKdjSweetSpot => KdjState.Contains("最优");

    /// <summary>近年归母净利趋势（如"23年+6.81 24年+3.40 25年+0.85｜累计+11.06亿"）。连亏或
    /// 三年累计为负时末尾带 ⚠。**只展示不过滤**——回测显示做成硬条件反而降低收益，但样本排除了
    /// ST/退市股、测不出踩雷风险，所以摆出来让用户自己判断（见 StockScreenResult.ProfitTrend）。</summary>
    public string ProfitTrend { get; init; } = "";
    /// <summary>三年累计净利（元）——给表格按它排序用，负值那些排在一起最容易被看见。</summary>
    public double? ThreeYearCumProfit { get; init; }

    /// <summary>三年累计净利的显示文本（如 <c>+72.77亿</c>，累计为负带 ⚠）——底仓法的结果表只显示
    /// 这个累计值，逐年明细在【条件详情】里画成柱状图（2026-08-20 用户要求：Grid 里塞
    /// "23年+29.52 24年+21.59 25年+21.66｜累计+72.77亿" 整串，列宽不够也读不出重点）。</summary>
    public string ThreeYearCumProfitText => ThreeYearCumProfit is { } cum
        ? $"{cum / 1e8:+0.00;-0.00}亿{(cum <= 0 ? " ⚠" : "")}"
        : "";

    /// <summary>日均波幅（近60日）；没算的方法显示空。超过4.5%时加 ⚠ ——那一档回测只有
    /// 0.03%/胜率50.2%（等于随机），且-10%止损在这种波动下两三天就会被噪音打掉。</summary>
    public double? DailyVolatility { get; init; }
    public string DailyVolatilityText => DailyVolatility.HasValue
        ? $"{DailyVolatility.Value * 100:F2}%{(DailyVolatility.Value > 0.045 ? " ⚠" : "")}"
        : "";

    // 主动仓的三个参考价（短线法结果表在用）。两个目标各有依据、由用户自己选，理由见
    // TradeDiscipline 的注释。**底仓法不用这组价**——底仓靠持有时间和分红，没有价格止盈止损。
    public string QuickTargetText => Fmt(TradeDiscipline.QuickTargetPct);
    public string BigTargetText => Fmt(TradeDiscipline.TargetPct);
    public string StopPriceText => Fmt(-TradeDiscipline.StopPct);

    private string Fmt(double pct) =>
        LastClose is > 0 ? (LastClose.Value * (1 + pct)).ToString("F2") : "";

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
        LastClose = r.LastClose,
        Category = r.Category ?? "",
        DividendYield = r.DividendYield,
        AvgDividendYield = r.AvgDividendYield,
        ConsecutiveDividendYears = r.ConsecutiveDividendYears,
        DividendTrend = r.DividendTrend ?? "",
        RequiredCapitalForTarget = r.RequiredCapitalForTarget,
        DailyVolatility = r.DailyVolatility,
        DepthBucket = r.DepthBucket ?? "",
        KdjState = r.KdjState ?? "",
        ProfitTrend = r.ProfitTrend ?? "",
        ThreeYearCumProfit = r.ThreeYearCumProfit,
    };
}
