using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "三角收敛" — detects a symmetric converging triangle (a descending resistance line drawn through
/// the swing highs *after the stage peak* + an ascending support line drawn through the swing lows
/// *after the stage trough*, anchored/segmented the way a human draws them — see
/// TriangleConvergenceDetector) plus a MACD-confirmed touch/breakout signal at the most recent bar.
/// rule1 requires resistance sloping down (R² ≥ minR2) AND support sloping up AND the two lines not
/// yet crossed — R² is gated only on the clean descending resistance line; the support line is a
/// noisy lower boundary (often R² 0.1~0.2) so it's only required to slope up, not to fit tightly.
/// Fixed to daily bars.
///
/// Lookback (形态搜索窗口), SwingWindow (局部高低点判定窗口) and MinR2 (压力线拟合优度下限) are all
/// threaded in from the caller rather than fixed constants like the other methods' thresholds — this
/// method's premise (which window, which points count as swing highs/lows, how tight a fit) is
/// inherently sensitive to those, so the user tunes them against real charts. Lookback in particular
/// must be long enough to reach back to the stage trough that starts the prior rally (default 90),
/// or the support line can't be anchored correctly.
/// </summary>
public class TriangleConvergenceAnalysisEngine
{
    /// <summary>Default R² floor — kept as a fallback/display value. Actual screening uses the
    /// per-call minR2 (see Analyze), which the UI exposes as an adjustable field alongside
    /// Lookback/SwingWindow rather than baking in one fixed number for every stock. Only a lower
    /// bound: a higher R² just means the trendline fits better, so there's no upper cutoff.</summary>
    public const double MinR2 = 0.45;
    public const double TouchTolerancePct = 2.0;
    public const double BreakoutPct = 1.5;

    private readonly IBarRepository _barRepository;

    public TriangleConvergenceAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    public StockScreenResult Analyze(string code, string name, int lookbackDays, int swingWindow, double minR2 = MinR2)
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
        double? sortScore = null;

        if (match == null)
        {
            rule1 = false;
            rule1Basis = $"最近{lookbackDays}天内摆动高/低点不足{TriangleConvergenceDetector.MinSwingPoints}个，无法拟合趋势线";
            rule2Basis = "形态不成立，不判断触线/突破";
            rule3Basis = "形态不成立，不判断MACD配合";
        }
        else
        {
            // 对称三角形：压力线（锚在阶段高峰、连峰后走低的高点）向下，支撑线（锚在阶段低谷、连谷后
            // 抬高的低点）向上，两线向右收口。① R² 只卡压力线——那条干净下行的线是形态的决定性特征；
            // 支撑线只是价格的下沿包络，真实行情里常被中途回踩的低点带得拟合很松（R²可能只有0.1~0.2），
            // 强行要求它拟合达标反而会把人一眼能看出的三角形筛掉，所以支撑线只要求"向上"不要求R²。
            // ② 还必须保证到今天为止两线尚未交叉（gapEnd>0）——支撑线一旦涨到压力线上面去，几何上就
            // 不再是三角形而是收敛过头翻转了，不算数。
            int windowStart = Math.Max(swingWindow, today - lookbackDays + 1);
            double gapStart = match.HighValueAt(windowStart) - match.LowValueAt(windowStart);
            double gapEnd = match.HighValueAt(today) - match.LowValueAt(today);
            bool shape = match.HighSlope < 0 && match.LowSlope > 0 && gapStart > 0 && gapEnd > 0;

            rule1 = match.HighR2 >= minR2 && shape;
            rule1Basis = $"压力线（锚在阶段高峰后的{match.SwingHighIndices.Count}个走低高点）：斜率={match.HighSlope:F4}，R²={match.HighR2:F3}；" +
                         $"支撑线（锚在阶段低谷后的{match.SwingLowIndices.Count}个抬高低点）：斜率={match.LowSlope:F4}，R²={match.LowR2:F3}；" +
                         (gapStart > 0 && gapEnd > 0
                             ? $"两线间距由{gapStart:F2}收窄到{gapEnd:F2}（收窄到{(gapEnd / gapStart * 100):F0}%，未交叉）；"
                             : "两线在窗口内已经交叉（收敛过头翻转，不算三角形）；") +
                         $"阈值：压力线向下（斜率<0）且R²≥{minR2:F2}、支撑线向上（斜率>0）、到今天两线未交叉（标准对称收敛三角形）";

            // 收敛质量(0~100)：一半看间距收窄程度(收窄越多越好)，一半看价格有多少时间被夹在两线之间
            // (越贴合三角形定义越好)。只用于结果排序，不参与是否入选(rule1)的判定。
            if (rule1)
            {
                double convRatio = Math.Clamp(gapEnd / gapStart, 0, 1);
                int inside = 0, total = 0;
                for (int i = windowStart; i <= today; i++)
                {
                    double hi = match.HighValueAt(i), lo = match.LowValueAt(i);
                    double margin = (hi - lo) * 0.05;
                    if (dayBars[i].Close <= hi + margin && dayBars[i].Close >= lo - margin) inside++;
                    total++;
                }
                double contain = total > 0 ? (double)inside / total : 0;
                sortScore = 100 * (0.5 * (1 - convRatio) + 0.5 * contain);
            }

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
            SortScore = sortScore,
            Criteria = new List<CriterionResult>
            {
                new() { Name = "三角收敛形态成立（两线收窄，拟合达标）", Satisfied = rule1, Basis = rule1Basis },
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
