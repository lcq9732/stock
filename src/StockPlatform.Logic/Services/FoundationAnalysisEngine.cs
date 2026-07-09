using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "峰哥法"（类名沿用 Foundation——这个名字描述算法本身，不随人名而变）— applies the checklist in
/// doc/analysis-app-design.md section 3.2 to one stock at one granularity, using data already
/// present in local storage — no network access.
/// </summary>
public class FoundationAnalysisEngine
{
    private readonly IBarRepository _barRepository;

    public FoundationAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    public StockScreenResult Analyze(string code, string granularity, int lookback = 20)
    {
        var bars = _barRepository.Query(code, granularity);
        int minRequired = Math.Max(35, lookback + 1);
        if (bars.Count < minRequired)
        {
            return new StockScreenResult
            {
                Code = code,
                Granularity = granularity,
                Error = $"历史数据不足（仅 {bars.Count} 条），至少需要 {minRequired} 条",
            };
        }

        var closes = bars.Select(b => b.Close).ToList();
        int i = bars.Count - 1;

        var (dif, dea) = TechnicalIndicators.MACD(closes);
        var (boll, _, _) = TechnicalIndicators.BOLL(closes, lookback);

        var match = FoundationBreakoutDetector.TryFind(bars, i, lookback);
        bool foundationSatisfied = match != null;
        string foundationBasis = match is { } m
            ? $"低位平台={m.Stage1Low:F2}~{m.Stage1High:F2}；" +
              $"高位平台={m.Stage2Low:F2}~{m.Stage2High:F2}（较低位平台+{(m.Stage2Low / m.Stage1High - 1) * 100:F1}%）；" +
              $"破位新低={m.Stage3Low:F2}（较低位平台{(m.Stage3Low / m.Stage1Low - 1) * 100:F1}%）；" +
              $"再突破：最新收盘={closes[i]:F2} > 高位平台高点={m.Stage2High:F2}"
            : $"未在最近{Math.Min(lookback, 60)}根K线内找到该形态（低位平台→高位平台→破位新低→再突破高点）";

        bool aboveBoll = closes[i] > boll[i];
        bool macdAboveZero = dif[i] > 0 && dea[i] > 0;

        var result = new StockScreenResult
        {
            Code = code,
            Granularity = granularity,
            DataDate = bars[i].PeriodStart,
            LastClose = closes[i],
            Criteria = new List<CriterionResult>
            {
                new()
                {
                    Name = "低位盘整-高位滞涨-破位新低-再破高点",
                    Satisfied = foundationSatisfied,
                    Basis = foundationBasis,
                },
                new()
                {
                    Name = "收盘价在BOLL中线上方",
                    Satisfied = aboveBoll,
                    Basis = $"最新收盘={closes[i]:F2}；BOLL中线={boll[i]:F2}",
                },
                new()
                {
                    Name = "MACD零轴之上",
                    Satisfied = macdAboveZero,
                    Basis = $"DIF={dif[i]:F3}；DEA={dea[i]:F3}",
                },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }
}
