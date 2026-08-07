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

    /// <summary>回调法的用途分类："底仓"/"主动仓"/"底仓+主动仓"；其它方法为空。
    /// 见 StockScreenResult.Category。</summary>
    public string Category { get; init; } = "";
    public bool IsBaseHolding => Category.Contains(PullbackAnalysisEngine.CategoryBase);

    /// <summary>股息率（近12个月已实施派息 ÷ 现价）；没算的方法显示空。</summary>
    public double? DividendYield { get; init; }
    public string DividendYieldText => DividendYield.HasValue ? $"{DividendYield.Value * 100:F2}%" : "";

    /// <summary>档位标签："档位名 胜率% / 样本数"，见 StockScreenResult.DepthBucket。
    /// 样本数一起显示是有意的——超跌档78%的胜率只建立在319个样本上，别只看胜率。</summary>
    public string DepthBucket { get; init; } = "";

    /// <summary>日均波幅（近60日）；没算的方法显示空。超过4.5%时加 ⚠ ——那一档回测只有
    /// 0.03%/胜率50.2%（等于随机），且-10%止损在这种波动下两三天就会被噪音打掉。</summary>
    public double? DailyVolatility { get; init; }
    public string DailyVolatilityText => DailyVolatility.HasValue
        ? $"{DailyVolatility.Value * 100:F2}%{(DailyVolatility.Value > 0.045 ? " ⚠" : "")}"
        : "";

    // 回调法结果表专用的三个参考价（跟 SortScore 一样，是方法专属字段挂在共享行模型上）。
    // 两个目标各有依据、由用户自己选，理由见 PullbackAnalysisEngine 里 QuickTargetPct 的注释。
    public string QuickTargetText => Fmt(PullbackAnalysisEngine.DefaultQuickTargetPct);
    public string BigTargetText => Fmt(PullbackAnalysisEngine.DefaultTargetPct);
    public string StopPriceText => Fmt(-PullbackAnalysisEngine.DefaultStopPct);

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
        DailyVolatility = r.DailyVolatility,
        DepthBucket = r.DepthBucket ?? "",
    };
}
