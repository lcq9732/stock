using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "三角收敛" — detects a converging-triangle shape (descending resistance through swing highs +
/// ascending support through swing lows, see TriangleConvergenceDetector) plus a MACD-confirmed
/// touch/breakout signal at the most recent bar. Fixed to daily bars.
///
/// Lookback (形态搜索窗口) and SwingWindow (局部高低点判定窗口) are both threaded in from the
/// caller rather than fixed constants like the other three methods' thresholds — this method's
/// whole premise (which points even count as "local highs/lows") is inherently sensitive to that
/// choice, so the user needs to tune it against real charts, not trust one baked-in default.
/// </summary>
public class TriangleConvergenceAnalysisEngine
{
    public const double MinR2 = 0.6;
    public const double TouchTolerancePct = 2.0;
    public const double BreakoutPct = 1.5;

    private readonly IBarRepository _barRepository;

    public TriangleConvergenceAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    public StockScreenResult Analyze(string code, string name, int lookbackDays, int swingWindow)
    {
        var dayBars = _barRepository.Query(code, Granularity.Day);
        int minRequired = lookbackDays + swingWindow * 2 + 10;
        if (dayBars.Count < minRequired)
            return Error(code, $"日线历史数据不足（仅 {dayBars.Count} 条），至少需要 {minRequired} 条（形态窗口{lookbackDays}+摆动点窗口{swingWindow}×2+缓冲10）");

        int today = dayBars.Count - 1;
        var match = TriangleConvergenceDetector.TryFind(dayBars, today, lookbackDays, swingWindow);

        var closes = dayBars.Select(b => b.Close).ToList();
        var (dif, dea) = TechnicalIndicators.MACD(closes);
        double Hist(int i) => double.IsNaN(dif[i]) || double.IsNaN(dea[i]) ? double.NaN : (dif[i] - dea[i]) * 2;

        bool rule1, rule2 = false, rule3 = false;
        string rule1Basis, rule2Basis, rule3Basis;

        if (match == null)
        {
            rule1 = false;
            rule1Basis = $"最近{lookbackDays}天内摆动高/低点不足{TriangleConvergenceDetector.MinSwingPoints}个，无法拟合趋势线";
            rule2Basis = "形态不成立，不判断触线/突破";
            rule3Basis = "形态不成立，不判断MACD配合";
        }
        else
        {
            rule1 = match.HighR2 >= MinR2 && match.LowR2 >= MinR2 && match.HighSlope < 0 && match.LowSlope > 0;
            rule1Basis = $"压力线：斜率={match.HighSlope:F4}，R²={match.HighR2:F3}（{match.SwingHighIndices.Count}个高点）；" +
                         $"支撑线：斜率={match.LowSlope:F4}，R²={match.LowR2:F3}（{match.SwingLowIndices.Count}个低点）；" +
                         $"阈值：R²均≥{MinR2:F1}，且压力线向下、支撑线向上";

            double close = dayBars[today].Close;
            double highToday = match.HighValueAt(today);
            double lowToday = match.LowValueAt(today);
            double breakoutActualPct = (close - highToday) / highToday * 100;
            double touchActualPct = (close - lowToday) / lowToday * 100;

            bool isBreaking = breakoutActualPct >= BreakoutPct;
            bool isTouching = !isBreaking && Math.Abs(touchActualPct) <= TouchTolerancePct;

            rule2 = isBreaking || isTouching;
            rule2Basis = isBreaking
                ? $"收盘{close:F2}突破压力线（压力线当前位置≈{highToday:F2}），超出{breakoutActualPct:F2}%（阈值≥{BreakoutPct:F1}%）"
                : isTouching
                    ? $"收盘{close:F2}贴近支撑线（支撑线当前位置≈{lowToday:F2}），偏离{touchActualPct:F2}%（容差≤{TouchTolerancePct:F1}%）"
                    : $"收盘{close:F2}既未突破压力线（≈{highToday:F2}）也未贴近支撑线（≈{lowToday:F2}）";

            double h0 = Hist(today);
            double h1 = today >= 1 ? Hist(today - 1) : double.NaN;
            double h2 = today >= 2 ? Hist(today - 2) : double.NaN;
            bool goldenCross = today >= 1 && dif[today] > dea[today] && dif[today - 1] <= dea[today - 1];
            bool histTurningUp = today >= 2 && !double.IsNaN(h0) && !double.IsNaN(h1) && !double.IsNaN(h2) && h0 > h1 && h1 <= h2;
            bool histPositive = !double.IsNaN(h0) && h0 > 0;

            if (isTouching)
            {
                rule3 = goldenCross || histTurningUp;
                rule3Basis = $"DIF={dif[today]:F3}，DEA={dea[today]:F3}，MACD柱={h0:F3}（要求DIF上穿DEA或柱状图由缩短转为放大，作为支撑企稳的确认）";
            }
            else if (isBreaking)
            {
                rule3 = histPositive || goldenCross;
                rule3Basis = $"DIF={dif[today]:F3}，DEA={dea[today]:F3}，MACD柱={h0:F3}（要求柱状图转正或DIF上穿DEA，作为突破的确认）";
            }
            else
            {
                rule3Basis = "价格未触及支撑线也未突破压力线，不判断MACD配合";
            }
        }

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = dayBars[today].PeriodStart,
            LastClose = dayBars[today].Close,
            Criteria = new List<CriterionResult>
            {
                new() { Name = "三角收敛形态成立（压力线向下、支撑线向上，拟合达标）", Satisfied = rule1, Basis = rule1Basis },
                new() { Name = "当前价格触及支撑线或突破压力线", Satisfied = rule2, Basis = rule2Basis },
                new() { Name = "MACD配合确认", Satisfied = rule3, Basis = rule3Basis },
            },
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }

    private static StockScreenResult Error(string code, string message) => new()
    {
        Code = code,
        Granularity = Granularity.Day,
        Error = message,
    };
}
