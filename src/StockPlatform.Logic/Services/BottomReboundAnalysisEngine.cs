using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "耀哥法"（原名触底回升法，类名沿用 BottomRebound——描述算法本身，不随人名而变）— 5 conditions
/// (doc/analysis-app-design.md section 3.2.3), all AND, fixed to daily bars (the 20-day bottom
/// window and MA20 only mean what the design intends on a daily calendar, same reasoning as
/// 金叉法 being daily-only). 条件4（最近三天资金净流入）依赖 NetInflow 表，这张表是新加的、需要
/// Fetcher 重新拉取才会有数据——沿用 MidCapPullbackAnalysisEngine 处理流通市值缺失的做法：数据不
/// 存在时这条规则判不满足（而不是让整只股票直接报错跳过），其余4条规则照常计算和展示。
/// </summary>
public class BottomReboundAnalysisEngine
{
    private const int MinBarsRequired = 40;
    private const int MacdGoldenCrossWindowDays = 20;
    private const int NetInflowCheckDays = 3;

    private readonly IBarRepository _barRepository;
    private readonly INetInflowRepository _netInflowRepository;

    public BottomReboundAnalysisEngine(IBarRepository barRepository, INetInflowRepository netInflowRepository)
    {
        _barRepository = barRepository;
        _netInflowRepository = netInflowRepository;
    }

    /// <param name="difNearZeroThreshold">DIF must be >= -difNearZeroThreshold — 0 means "DIF必须
    /// ≥0"，调大后允许DIF略低于0轴也算"接近0轴"，对应用户说的"0轴附近的具体范围可配置"。</param>
    public StockScreenResult Analyze(string code, double difNearZeroThreshold = 0)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        if (bars.Count < MinBarsRequired)
        {
            return new StockScreenResult
            {
                Code = code,
                Granularity = Granularity.Day,
                Error = $"历史数据不足（仅 {bars.Count} 条），至少需要 {MinBarsRequired} 条",
            };
        }

        var closes = bars.Select(b => b.Close).ToList();
        int i = bars.Count - 1;

        var (dif, dea) = TechnicalIndicators.MACD(closes);
        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var ma20 = TechnicalIndicators.SMA(closes, 20);

        bool macdOk = dif[i] >= -difNearZeroThreshold;
        bool aboveMAs = closes[i] > ma5[i] && closes[i] > ma10[i] && closes[i] > ma20[i];

        var pattern = BottomReboundPatternDetector.Find(bars, i);
        int bottomIdx = pattern.BottomIndex;
        bool hasUpStreak = pattern.HasUpStreak;
        int bestStreakLen = pattern.BestStreakLen;
        int bestStreakStart = pattern.BestStreakStart;
        int bestStreakEnd = pattern.BestStreakEnd;

        // "底部上升形态"本身也要求已经重新站上三条均线——跟规则2是同一个检查，但这是这个形态
        // 自己定义的一部分（图里第3条最后一句），所以在这条规则里也显式判一次。
        bool bottomPatternOk = hasUpStreak && aboveMAs;

        // 规则4——最近3个交易日（含今天）的资金净流入必须都是正数。NetInflow是独立于Bar的表，
        // 按自己的日期水位线增量抓取，所以这里单独按日期范围查，不假设跟bars的下标能对上。
        var netInflowRows = _netInflowRepository.Query(code, bars[i].PeriodStart.AddDays(-10));
        bool hasEnoughNetInflowData = netInflowRows.Count >= NetInflowCheckDays;
        var recentInflows = hasEnoughNetInflowData ? netInflowRows.TakeLast(NetInflowCheckDays).ToList() : new List<NetInflow>();
        bool netInflowOk = hasEnoughNetInflowData && recentInflows.All(r => r.MainNetInflow > 0);

        // 规则5——最近20个交易日（含今天）里，DIF只要出现过至少一次由下向上穿过DEA（金叉）就算
        // 满足，不要求金叉发生在窗口末尾。记录窗口内最后一次金叉的位置用于展示依据。
        int gcWindowStart = Math.Max(1, i - MacdGoldenCrossWindowDays + 1);
        bool macdGoldenCrossOk = false;
        int lastGoldenCrossIdx = -1;
        for (int t = gcWindowStart; t <= i; t++)
        {
            if (double.IsNaN(dif[t - 1]) || double.IsNaN(dea[t - 1]) || double.IsNaN(dif[t]) || double.IsNaN(dea[t]))
                continue;
            if (dif[t - 1] <= dea[t - 1] && dif[t] > dea[t])
            {
                macdGoldenCrossOk = true;
                lastGoldenCrossIdx = t;
            }
        }

        var result = new StockScreenResult
        {
            Code = code,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = closes[i],
            Criteria = new List<CriterionResult>
            {
                new()
                {
                    Name = "日线MACD",
                    Satisfied = macdOk,
                    Basis = $"DIF={dif[i]:F3}；DEA={dea[i]:F3}（需要 DIF≥{-difNearZeroThreshold:F3}）",
                },
                new()
                {
                    Name = "均线条件：收盘价高于MA5/MA10/MA20",
                    Satisfied = aboveMAs,
                    Basis = $"最新收盘={closes[i]:F2}；MA5={ma5[i]:F2}；MA10={ma10[i]:F2}；MA20={ma20[i]:F2}",
                },
                new()
                {
                    Name = "底部上升形态",
                    Satisfied = bottomPatternOk,
                    Basis = $"底部={bars[bottomIdx].PeriodStart:yyyy-MM-dd}（最低价={bars[bottomIdx].Low:F2}）；" +
                        (hasUpStreak
                            ? $"此后最长连续上涨{bestStreakLen}天（{bars[bestStreakStart].PeriodStart:yyyy-MM-dd}~{bars[bestStreakEnd].PeriodStart:yyyy-MM-dd}）"
                            : $"此后未出现过≥{BottomReboundPatternDetector.MinUpStreak}天连续上涨（最长{bestStreakLen}天）") +
                        (aboveMAs ? "；已重新站上MA5/MA10/MA20" : "；尚未重新站上MA5/MA10/MA20"),
                },
                new()
                {
                    Name = "最近三天资金净流入",
                    Satisfied = netInflowOk,
                    DataMissing = !hasEnoughNetInflowData,
                    Basis = hasEnoughNetInflowData
                        ? $"最近{NetInflowCheckDays}个交易日资金净流入：" + string.Join("；", recentInflows.Select(r => $"{r.PeriodStart:yyyy-MM-dd}={r.MainNetInflow / 1e4:F0}万"))
                        : $"缺少资金净流入数据（仅有{netInflowRows.Count}条，需要至少{NetInflowCheckDays}条——请先在Fetcher里重新执行一次拉取）",
                },
                new()
                {
                    Name = "20天内MACD出现金叉",
                    Satisfied = macdGoldenCrossOk,
                    Basis = macdGoldenCrossOk
                        ? $"最近{MacdGoldenCrossWindowDays}个交易日内，{bars[lastGoldenCrossIdx].PeriodStart:yyyy-MM-dd} 出现MACD金叉（DIF由下向上穿过DEA）"
                        : $"最近{MacdGoldenCrossWindowDays}个交易日内未出现MACD金叉（DIF由下向上穿过DEA）",
                },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }
}
