using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// Finds the "底部上升形态" pieces (doc/analysis-app-design.md section 3.2.3, rule 3) shared by
/// BottomReboundAnalysisEngine (the Satisfied check) and BottomReboundChartBuilder (drawing the
/// same bottom/streak on the chart) — kept in one place so the chart can never show a different
/// bottom day than the one the rule actually used.
/// </summary>
public static class BottomReboundPatternDetector
{
    public const int BottomWindowDays = 20;
    public const int MinUpStreak = 3;

    public class Match
    {
        public int BottomIndex { get; init; }
        public int BestStreakLen { get; init; }
        /// <summary>-1 if no up-streak was found at all (BestStreakLen == 0).</summary>
        public int BestStreakStart { get; init; }
        public int BestStreakEnd { get; init; }
        public bool HasUpStreak => BestStreakLen >= MinUpStreak;
    }

    public static Match Find(IReadOnlyList<Bar> bars, int today)
    {
        int windowStart = Math.Max(0, today - BottomWindowDays + 1);
        int bottomIdx = windowStart;
        for (int t = windowStart; t <= today; t++)
            if (bars[t].Low < bars[bottomIdx].Low) bottomIdx = t;

        int bestStreakLen = 0, bestStreakEnd = -1, curLen = 0;
        for (int t = bottomIdx + 1; t <= today; t++)
        {
            curLen = bars[t].Close > bars[t - 1].Close ? curLen + 1 : 0;
            if (curLen > bestStreakLen) { bestStreakLen = curLen; bestStreakEnd = t; }
        }
        int bestStreakStart = bestStreakLen > 0 ? bestStreakEnd - bestStreakLen + 1 : -1;

        return new Match
        {
            BottomIndex = bottomIdx,
            BestStreakLen = bestStreakLen,
            BestStreakStart = bestStreakStart,
            BestStreakEnd = bestStreakEnd,
        };
    }
}
