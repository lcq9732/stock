using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "金叉法" — the 7-condition checklist ported from the legacy single-stock watchlist tool
/// (src/StockAnalyzer.Logic/Services/StockAnalyzerService.cs). Unlike 筑基法 this doesn't require
/// every condition to pass — a stock counts as a candidate once at least 5 of the 7 are satisfied
/// (see <see cref="PassThreshold"/>). Daily bars only: several of the 7 conditions (the 20-day
/// platform breakout, the "近10日" RSI lookback) assume a daily trading calendar and would need
/// re-tuning to mean the same thing on week/month/minute bars, which the original design never
/// covered.
/// </summary>
public class GoldenCrossAnalysisEngine
{
    private const int MinBarsRequired = 40;
    private const int PassThreshold = 5;

    private readonly IBarRepository _barRepository;

    public GoldenCrossAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    public StockScreenResult Analyze(string code)
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
        var highs = bars.Select(b => b.High).ToList();
        var lows = bars.Select(b => b.Low).ToList();
        var volumes = bars.Select(b => b.Volume).ToList();
        int i = bars.Count - 1;

        var ma5 = TechnicalIndicators.SMA(closes, 5);
        var ma10 = TechnicalIndicators.SMA(closes, 10);
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        var (k, d, _) = TechnicalIndicators.KDJ(closes, highs, lows);
        var rsi = TechnicalIndicators.RSI(closes);
        var vol5 = TechnicalIndicators.SMA(volumes, 5);

        bool ma5CrossUp = ma5[i - 1] <= ma10[i - 1] && ma5[i] > ma10[i];
        bool ma10TurningUp = ma10[i] > ma10[i - 1] && ma10[i - 1] <= ma10[i - 2];
        bool macdGoldenCross = dif[i - 1] <= dea[i - 1] && dif[i] > dea[i];
        bool kdjGoldenCrossIn20to50 = k[i - 1] <= d[i - 1] && k[i] > d[i] && k[i] >= 20 && k[i] <= 50;

        double rsiRecentMin = double.NaN;
        for (int t = Math.Max(0, i - 10); t < i; t++)
            if (!double.IsNaN(rsi[t]) && (double.IsNaN(rsiRecentMin) || rsi[t] < rsiRecentMin))
                rsiRecentMin = rsi[t];
        bool rsiWasNear30 = !double.IsNaN(rsiRecentMin) && rsiRecentMin <= 35;
        bool rsiCrossAbove50 = rsi[i - 1] < 50 && rsi[i] >= 50;
        bool rsiBreakout = rsiWasNear30 && rsiCrossAbove50;

        bool volumeSurge = !double.IsNaN(vol5[i - 1]) && volumes[i] >= 1.5 * vol5[i - 1];

        double resistance = double.MinValue;
        for (int t = i - 20; t < i; t++) resistance = Math.Max(resistance, highs[t]);
        bool platformBreakout = closes[i] > resistance;

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
                    Name = "MA5 上穿 MA10",
                    Satisfied = ma5CrossUp,
                    Basis = $"昨日 MA5={ma5[i - 1]:F2} MA10={ma10[i - 1]:F2}；今日 MA5={ma5[i]:F2} MA10={ma10[i]:F2}",
                },
                new()
                {
                    Name = "MA10 开始拐头向上",
                    Satisfied = ma10TurningUp,
                    Basis = $"前日 MA10={ma10[i - 2]:F2}；昨日 MA10={ma10[i - 1]:F2}；今日 MA10={ma10[i]:F2}",
                },
                new()
                {
                    Name = "MACD 金叉",
                    Satisfied = macdGoldenCross,
                    Basis = $"昨日 DIF={dif[i - 1]:F3} DEA={dea[i - 1]:F3}；今日 DIF={dif[i]:F3} DEA={dea[i]:F3}",
                },
                new()
                {
                    Name = "KDJ 在20~50区域金叉",
                    Satisfied = kdjGoldenCrossIn20to50,
                    Basis = $"昨日 K={k[i - 1]:F1} D={d[i - 1]:F1}；今日 K={k[i]:F1} D={d[i]:F1}（需在20~50区间）",
                },
                new()
                {
                    Name = "RSI 从30附近向上突破50",
                    Satisfied = rsiBreakout,
                    Basis = $"近10日RSI最低={(double.IsNaN(rsiRecentMin) ? "N/A" : rsiRecentMin.ToString("F1"))}；昨日 RSI={rsi[i - 1]:F1}；今日 RSI={rsi[i]:F1}",
                },
                new()
                {
                    Name = "成交量≥5日均量的1.5倍",
                    Satisfied = volumeSurge,
                    Basis = $"今日成交量={volumes[i]:F0}；昨日5日均量={vol5[i - 1]:F0}；比值={(double.IsNaN(vol5[i - 1]) || vol5[i - 1] == 0 ? "N/A" : (volumes[i] / vol5[i - 1]).ToString("F2"))}（需≥1.50）",
                },
                new()
                {
                    Name = "股价突破最近20日平台或压力位",
                    Satisfied = platformBreakout,
                    Basis = $"今日收盘={closes[i]:F2}；前20日最高价={resistance:F2}",
                },
            },
        };
        result.Passed = result.Criteria.AtLeastSatisfiedIgnoringMissingData(PassThreshold);
        return result;
    }
}
