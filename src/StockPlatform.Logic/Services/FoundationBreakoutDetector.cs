using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// Detects the "筑基法" four-stage pattern (doc/analysis-app-design.md section 3.2): a low
/// platform, then a higher platform, then a breakdown to a new low below the first platform, then
/// — right now, at the most recent bar — a breakout back above the high platform's peak. All four
/// stages must be found inside the same lookback window; the breakout stage is only satisfied if
/// "today" (the bar passed as <see cref="TryFind"/>'s <c>today</c> index) is the bar doing the
/// breaking, not some earlier bar in the window.
/// </summary>
public static class FoundationBreakoutDetector
{
    public class Match
    {
        public int Stage1Start { get; init; }
        public int Stage1End { get; init; }
        public int Stage2Start { get; init; }
        public int Stage2End { get; init; }
        public int Stage3Start { get; init; }
        public int Stage3End { get; init; }
        public double Stage1Low { get; init; }
        public double Stage1High { get; init; }
        public double Stage2Low { get; init; }
        public double Stage2High { get; init; }
        public double Stage3Low { get; init; }
    }

    private const int MaxStage1Bars = 5;
    private const int MaxStage2Bars = 3;
    private const int MaxStage3Bars = 5;
    private const double FlatnessThreshold = 0.05;
    private const double RiseThreshold = 0.10;
    private const double DropThreshold = 0.05;

    // Search cost grows with the cube of the window size (three nested stage searches) — capped
    // independently of the user-facing lookback so a large lookback (set for BOLL/other rules)
    // can't make a full batch run of ~5000 stocks take unreasonably long.
    private const int MaxSearchWindow = 60;

    public static Match? TryFind(IReadOnlyList<Bar> bars, int today, int lookback)
    {
        int windowSize = Math.Min(lookback, MaxSearchWindow);
        int windowStart = Math.Max(0, today - windowSize + 1);

        for (int s1Start = windowStart; s1Start < today; s1Start++)
        {
            for (int len1 = 1; len1 <= MaxStage1Bars; len1++)
            {
                int s1End = s1Start + len1 - 1;
                if (s1End >= today) break;
                var (s1Low, s1High) = MinMax(bars, s1Start, s1End);
                if ((s1High - s1Low) / s1Low > FlatnessThreshold) continue;

                for (int s2Start = s1End + 1; s2Start < today; s2Start++)
                {
                    for (int len2 = 1; len2 <= MaxStage2Bars; len2++)
                    {
                        int s2End = s2Start + len2 - 1;
                        if (s2End >= today) break;
                        var (s2Low, s2High) = MinMax(bars, s2Start, s2End);
                        if ((s2High - s2Low) / s2Low > FlatnessThreshold) continue;
                        if (s2Low < s1High * (1 + RiseThreshold)) continue;

                        // Stage 4 depends only on s2High and today's close, not on stage 3's
                        // exact window — check it once here instead of inside the stage-3 loop.
                        if (bars[today].Close <= s2High) continue;

                        for (int s3Start = s2End + 1; s3Start < today; s3Start++)
                        {
                            for (int len3 = 1; len3 <= MaxStage3Bars; len3++)
                            {
                                int s3End = s3Start + len3 - 1;
                                if (s3End >= today) break;
                                var (s3Low, _) = MinMax(bars, s3Start, s3End);
                                if (s3Low > s1Low * (1 - DropThreshold)) continue;

                                return new Match
                                {
                                    Stage1Start = s1Start,
                                    Stage1End = s1End,
                                    Stage2Start = s2Start,
                                    Stage2End = s2End,
                                    Stage3Start = s3Start,
                                    Stage3End = s3End,
                                    Stage1Low = s1Low,
                                    Stage1High = s1High,
                                    Stage2Low = s2Low,
                                    Stage2High = s2High,
                                    Stage3Low = s3Low,
                                };
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    private static (double Low, double High) MinMax(IReadOnlyList<Bar> bars, int start, int end)
    {
        double low = double.MaxValue, high = double.MinValue;
        for (int t = start; t <= end; t++)
        {
            low = Math.Min(low, bars[t].Low);
            high = Math.Max(high, bars[t].High);
        }
        return (low, high);
    }
}
