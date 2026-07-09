using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// Finds a converging-triangle shape (descending resistance line through swing highs + ascending
/// support line through swing lows) shared by TriangleConvergenceAnalysisEngine (the Satisfied
/// checks) and TriangleConvergenceChartBuilder (drawing the same two lines on the chart) — kept in
/// one place so the chart can never show different trendlines than the ones the rule actually used.
///
/// Only detects the "symmetric" converging case (resistance slope &lt; 0 AND support slope &gt; 0
/// is judged by the caller, not enforced here); pure ascending/descending triangles (one flat
/// side) are not specially handled by this v1 — they'll just fail that slope-direction check.
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

    /// <summary>Null if there aren't enough swing points on either side within the window to fit a
    /// line at all — a structural "can't even attempt this shape" result, distinct from "fitted a
    /// line but it didn't pass the caller's R²/direction thresholds".</summary>
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

        var (highSlope, highIntercept, highR2) = LinearRegression(swingHighs, i => bars[i].High);
        var (lowSlope, lowIntercept, lowR2) = LinearRegression(swingLows, i => bars[i].Low);

        return new Match
        {
            HighSlope = highSlope, HighIntercept = highIntercept, HighR2 = highR2,
            LowSlope = lowSlope, LowIntercept = lowIntercept, LowR2 = lowR2,
            SwingHighIndices = swingHighs, SwingLowIndices = swingLows,
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
