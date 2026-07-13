using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "峰哥法"（类名沿用 Foundation——描述算法本身，不随人名/规则变化而改类名）。
///
/// 【2026-07-10 规则整体替换】旧版是"低位平台→高位平台→破位新低→再突破 + BOLL中线上 + MACD零轴上"
/// 的四段式形态，已按用户要求换成一个更直接的两步筛选（见 doc/analysis-app-design.md 3.2.2）：
///   C1 近 N 个交易日内(N 可调, 1~15)出现过至少一次涨停(收盘涨停, 复用 LimitUpClassifier)；
///   C2 最近那次涨停(记为 L)之后, 成交量"持续放量"——L 到今天的平均量 ≥ 涨停前5日均量 × k(可调,
///      默认1.5), 且 L 之后没有任何一天缩回到涨停前5日均量以下(体现"持续", 不是放一天就歇)。
/// 用户已确认取"甲"口径：涨停就发生在今天(L=今天)也算——此时 [L,今天] 只有今天一根, 退化为
/// "涨停当天本身放量 ≥ 前5日均量 × k"。固定日线(涨停本身就是日线概念)。两条全满足才入选。
/// </summary>
public class FoundationAnalysisEngine
{
    private const int VolumeBaselineDays = 5; // 涨停前"前5日均量"的窗口

    private readonly IBarRepository _barRepository;

    public FoundationAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    /// <param name="lookbackDays">涨停回看窗口 N（最近多少个交易日内出现过涨停），用户可调，范围约1~15。</param>
    /// <param name="volumeMultiple">C2 放量倍数 k（涨停后平均量相对涨停前5日均量的倍数下限），默认1.5。</param>
    public StockScreenResult Analyze(string code, string name, int lookbackDays, double volumeMultiple)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        int minRequired = lookbackDays + VolumeBaselineDays + 2;
        if (bars.Count < minRequired)
            return new StockScreenResult
            {
                Code = code,
                Name = name,
                Granularity = Granularity.Day,
                Error = $"日线历史数据不足（仅 {bars.Count} 条），至少需要 {minRequired} 条",
            };

        var closes = bars.Select(b => b.Close).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int i = bars.Count - 1;

        double PctChange(int t) => t >= 1 && closes[t - 1] > 0 ? (closes[t] - closes[t - 1]) / closes[t - 1] * 100 : 0;

        // C1：最近 N 个交易日内(含今天)是否出现过涨停，取最近一次记为 L。
        int windowStart = Math.Max(1, i - lookbackDays + 1);
        int lastLimitUp = -1;
        for (int t = windowStart; t <= i; t++)
            if (LimitUpClassifier.IsLimitUp(code, name, PctChange(t))) lastLimitUp = t;
        bool c1 = lastLimitUp != -1;
        string c1Basis = c1
            ? $"最近{lookbackDays}个交易日内出现涨停：最近一次在 {bars[lastLimitUp].PeriodStart:yyyy-MM-dd}（涨幅{PctChange(lastLimitUp):F2}%，涨停线{LimitUpClassifier.LimitUpPercent(code, name):F0}%）"
            : $"最近{lookbackDays}个交易日内没有出现涨停";

        // C2：涨停 L 之后成交量持续放量（L 到今天平均量 ≥ 涨停前5日均量 × k，且期间没有一天缩回基准以下）。
        bool c2 = false;
        string c2Basis;
        if (c1)
        {
            int b0 = Math.Max(0, lastLimitUp - VolumeBaselineDays);
            int baselineCount = lastLimitUp - b0;
            double baseline = baselineCount > 0 ? volumes.Skip(b0).Take(baselineCount).Average() : double.NaN;

            double avgAfter = volumes.Skip(lastLimitUp).Take(i - lastLimitUp + 1).Average(); // [L, 今天]
            double minAfter = double.MaxValue;
            for (int t = lastLimitUp; t <= i; t++) minAfter = Math.Min(minAfter, volumes[t]);

            if (double.IsNaN(baseline) || baseline <= 0)
            {
                c2Basis = "涨停日之前没有足够的成交量数据作基准，无法判断放量";
            }
            else
            {
                bool sustained = avgAfter >= baseline * volumeMultiple && minAfter >= baseline;
                c2 = sustained;
                c2Basis = $"涨停前5日均量={baseline:F0}；涨停日L至今平均量={avgAfter:F0}（={avgAfter / baseline:F2}×，需≥{volumeMultiple:F1}×）；" +
                          $"L之后最低单日量={(minAfter == double.MaxValue ? 0 : minAfter):F0}（需≥基准{baseline:F0}，即不缩回基准以下）";
            }
        }
        else c2Basis = "没有涨停，不判断涨停后的量能";

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = closes[i],
            Criteria = new List<CriterionResult>
            {
                new() { Name = $"C1 近{lookbackDays}个交易日内出现过涨停", Satisfied = c1, Basis = c1Basis },
                new() { Name = $"C2 涨停后持续放量(≥前5日均量×{volumeMultiple:F1}且不缩回基准以下)", Satisfied = c2, Basis = c2Basis },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }
}
