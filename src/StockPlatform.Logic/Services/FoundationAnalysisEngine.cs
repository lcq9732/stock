using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "峰哥法"（类名沿用 Foundation——描述算法本身，不随人名/规则变化而改类名）。
///
/// 【2026-07-13 规则再次简化】按用户要求，只保留一个最直接的条件（见 doc/analysis-app-design.md 3.2.2）：
///   C1 近 N 个交易日内(N 可调, 默认7)出现过至少一次涨停(收盘涨停, 复用 LimitUpClassifier)。
/// 之前那版还带一个"涨停后持续放量"的 C2，已按用户要求去掉——现在只要近 N 天内有过涨停就列出来。
/// 固定日线(涨停本身就是日线概念)。条件详情图在K线上标出涨停那几天（见 FoundationChartBuilder，
/// 它用同一套 LimitUpClassifier 口径自行标注，图文一致）。
/// </summary>
public class FoundationAnalysisEngine
{
    private readonly IBarRepository _barRepository;

    public FoundationAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    /// <param name="lookbackDays">涨停回看窗口 N（最近多少个交易日内出现过涨停），用户可调，默认7。</param>
    public StockScreenResult Analyze(string code, string name, int lookbackDays)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        int minRequired = lookbackDays + 2;
        if (bars.Count < minRequired)
            return new StockScreenResult
            {
                Code = code,
                Name = name,
                Granularity = Granularity.Day,
                Error = $"日线历史数据不足（仅 {bars.Count} 条），至少需要 {minRequired} 条",
            };

        var closes = bars.Select(b => b.Close).ToList();
        int i = bars.Count - 1;
        double PctChange(int t) => t >= 1 && closes[t - 1] > 0 ? (closes[t] - closes[t - 1]) / closes[t - 1] * 100 : 0;

        // C1：最近 N 个交易日内(含今天)是否出现过涨停；统计次数、记录最近一次。
        int windowStart = Math.Max(1, i - lookbackDays + 1);
        int lastLimitUp = -1, count = 0;
        for (int t = windowStart; t <= i; t++)
            if (LimitUpClassifier.IsLimitUp(code, name, PctChange(t))) { lastLimitUp = t; count++; }
        bool c1 = lastLimitUp != -1;
        string c1Basis = c1
            ? $"最近{lookbackDays}个交易日内出现涨停 {count} 次，最近一次在 {bars[lastLimitUp].PeriodStart:yyyy-MM-dd}（涨幅{PctChange(lastLimitUp):F2}%，涨停线{LimitUpClassifier.LimitUpPercent(code, name):F0}%）"
            : $"最近{lookbackDays}个交易日内没有出现涨停";

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = closes[i],
            // 用涨停次数当排序分：涨停越多的排前面（见结果表按 SortScore 展示的方法）。
            SortScore = count,
            Criteria = new List<CriterionResult>
            {
                new() { Name = $"近{lookbackDays}个交易日内出现过涨停", Satisfied = c1, Basis = c1Basis },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }
}
