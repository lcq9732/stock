using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// Finds a converging-triangle shape (descending resistance line through the swing highs *after the
/// peak* + ascending support line through the swing lows *after the trough*) shared by
/// TriangleConvergenceAnalysisEngine (the Satisfied checks) and TriangleConvergenceChartBuilder
/// (drawing the same two lines on the chart) — kept in one place so the chart can never show
/// different trendlines than the ones the rule actually used.
///
/// **How the two lines are anchored (rewritten to match how a human draws them, see the 000066 /
/// 000977 reference charts in doc/analysis-app-design.md 3.2.1).** The naive approach — least-squares
/// through *every* swing high/low in the window — fails on the real pattern: these stocks first rally
/// hard (rising highs) and only *then* form the triangle, so mixing the pre-peak rally highs into the
/// resistance fit flattens or inverts it. Instead:
///   • Resistance is fit only on swing highs from the window's highest peak onward (they descend).
///   • Support is fit only on swing lows from the window's lowest trough onward (they ascend).
///   • Breakout case (the peak is so recent there aren't enough highs after it — price already broke
///     out): re-anchor the resistance on the highest pre-breakout swing high and fit the descending
///     highs up to that breakout, so the line is the one price is now poking above.
/// The caller (engine) judges direction/fit/convergence; this class only locates + fits the lines.
/// </summary>
public static class TriangleConvergenceDetector
{
    // 至少需要几个摆动点才谈得上"拟合一条线"——2个点必然完全拟合（R²恒为1，没有检验意义），3个
    // 才有效。达不达标（R²、方向）由 AnalysisEngine 用阈值把关，这里只负责"够不够点去拟合"。
    public const int MinSwingPoints = 3;

    public class Match
    {
        public double HighSlope { get; init; }
        public double HighIntercept { get; init; }
        public double HighR2 { get; init; }
        public double LowSlope { get; init; }
        public double LowIntercept { get; init; }
        public double LowR2 { get; init; }
        public List<int> SwingHighIndices { get; init; } = new();
        public List<int> SwingLowIndices { get; init; } = new();

        public double HighValueAt(int index) => HighSlope * index + HighIntercept;
        public double LowValueAt(int index) => LowSlope * index + LowIntercept;
    }

    /// <summary>Null if there aren't enough swing points on either side within the window to fit the
    /// anchored resistance/support lines — a structural "can't even attempt this shape" result,
    /// distinct from "fitted the lines but they didn't pass the caller's direction/R²/convergence
    /// thresholds".</summary>
    public static Match? TryFind(IReadOnlyList<Bar> bars, int today, int lookbackDays, int swingWindow)
    {
        int windowStart = Math.Max(swingWindow, today - lookbackDays + 1);
        int windowEnd = today - swingWindow; // 摆动点判定需要左右各swingWindow天，最近几天不可能成为摆动点

        var swingHighs = new List<int>();
        var swingLows = new List<int>();
        for (int t = windowStart; t <= windowEnd; t++)
        {
            bool isHigh = true, isLow = true;
            for (int k = t - swingWindow; k <= t + swingWindow; k++)
            {
                if (k == t) continue;
                if (bars[k].High > bars[t].High) isHigh = false;
                if (bars[k].Low < bars[t].Low) isLow = false;
            }
            if (isHigh) swingHighs.Add(t);
            if (isLow) swingLows.Add(t);
        }

        if (swingHighs.Count < MinSwingPoints || swingLows.Count < MinSwingPoints) return null;

        // 压力线：锚在阶段最高峰，只连峰之后的摆动高点（它们逐个走低）。
        int peakIdx = swingHighs.OrderByDescending(i => bars[i].High).ThenBy(i => i).First();
        var highPts = swingHighs.Where(i => i >= peakIdx).ToList();
        if (highPts.Count < MinSwingPoints)
        {
            // 突破场景：真正的峰太靠近末尾（价格已经突破），改锚在"突破前"最高的摆动高点上，
            // 连接到那之前的下降高点——这条线正是价格当前正在向上突破的那条。
            int cutoff = today - Math.Max(swingWindow + 1, lookbackDays / 6);
            var pre = swingHighs.Where(i => i <= cutoff).ToList();
            if (pre.Count < MinSwingPoints) return null;
            int prePeak = pre.OrderByDescending(i => bars[i].High).ThenBy(i => i).First();
            highPts = swingHighs.Where(i => i >= prePeak && i <= cutoff).ToList();
            if (highPts.Count < MinSwingPoints) return null;
        }

        // 支撑线：锚在阶段最低谷，只连谷之后的摆动低点（它们逐个抬高）。
        int troughIdx = swingLows.OrderBy(i => bars[i].Low).ThenBy(i => i).First();
        var lowPts = swingLows.Where(i => i >= troughIdx).ToList();
        if (lowPts.Count < MinSwingPoints) return null;

        var (highSlope, highIntercept, highR2) = LinearRegression(highPts, i => bars[i].High);
        var (lowSlope, lowIntercept, lowR2) = LinearRegression(lowPts, i => bars[i].Low);

        return new Match
        {
            HighSlope = highSlope, HighIntercept = highIntercept, HighR2 = highR2,
            LowSlope = lowSlope, LowIntercept = lowIntercept, LowR2 = lowR2,
            SwingHighIndices = highPts, SwingLowIndices = lowPts,
        };
    }

    private static (double Slope, double Intercept, double R2) LinearRegression(List<int> xs, Func<int, double> y)
    {
        int n = xs.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var x in xs)
        {
            double yv = y(x);
            sumX += x; sumY += yv; sumXY += x * yv; sumXX += (double)x * x;
        }
        double meanX = sumX / n, meanY = sumY / n;
        double denom = sumXX - n * meanX * meanX;
        double slope = denom == 0 ? 0 : (sumXY - n * meanX * meanY) / denom;
        double intercept = meanY - slope * meanX;

        double ssTot = 0, ssRes = 0;
        foreach (var x in xs)
        {
            double yv = y(x);
            double pred = slope * x + intercept;
            ssTot += (yv - meanY) * (yv - meanY);
            ssRes += (yv - pred) * (yv - pred);
        }
        double r2 = ssTot == 0 ? 1 : 1 - ssRes / ssTot;
        return (slope, intercept, r2);
    }
}
