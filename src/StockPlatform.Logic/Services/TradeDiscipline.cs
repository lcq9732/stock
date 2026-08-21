namespace StockPlatform.Logic.Services;

/// <summary>
/// 主动仓的止盈/止损默认幅度（2026-08-20 从原 PullbackAnalysisEngine 抽出）。
///
/// 为什么单独放：这组数原来挂在"回调法"引擎上，但该引擎已改造成
/// <see cref="CorePositionAnalysisEngine"/>（底仓法）——**底仓不用价格纪律**，它靠持有时间和分红。
/// 而结果表 ResultRowViewModel 和短线法仍要展示这三个价，所以常量挪到这个中立的地方。
///
/// 口径依据（2022-06~2026-02 的两轮回测，样本 186226 / 28020）：
///   +10% 止盈 / -10% 止损 / 最长持有120日 —— 各方法的条件取舍都是按这个口径校准的
///   +2%  止盈 / -5%  止损 / 最长持有60日  —— 用户实盘的实际打法，胜率 65.8% 但**期望为负**
///   （0.66×2% − 0.34×5% = −0.38%），且在这个口径下所有条件组合全部为负
///
/// 两个目标并列展示、不替用户做选择，是 2026-08-03 跟用户确认过的处理方式。
/// </summary>
public static class TradeDiscipline
{
    /// <summary>快目标（用户当前打法的实际止盈幅度）。⚠ 这个口径本身回测期望为负，见类注释。</summary>
    public const double QuickTargetPct = 0.02;

    /// <summary>大目标（各方法条件校准所用的止盈幅度）。</summary>
    public const double TargetPct = 0.10;

    /// <summary>止损幅度。不设止损不是消除亏损，而是把亏损推迟成尾部风险。</summary>
    public const double StopPct = 0.10;
}
