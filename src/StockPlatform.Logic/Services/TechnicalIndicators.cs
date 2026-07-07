namespace StockPlatform.Logic.Services;

/// <summary>Standard indicator calculators, aligned 1:1 with the input series (NaN where not enough history).</summary>
public static class TechnicalIndicators
{
    public static double[] SMA(IReadOnlyList<double> values, int period)
    {
        var result = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            if (i < period - 1) { result[i] = double.NaN; continue; }
            double sum = 0;
            for (int j = i - period + 1; j <= i; j++) sum += values[j];
            result[i] = sum / period;
        }
        return result;
    }

    public static double[] EMA(IReadOnlyList<double> values, int period)
    {
        var result = new double[values.Count];
        double k = 2.0 / (period + 1);
        double prev = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (i == 0) { prev = values[i]; result[i] = prev; continue; }
            prev = values[i] * k + prev * (1 - k);
            result[i] = prev;
        }
        return result;
    }

    public static (double[] Dif, double[] Dea) MACD(IReadOnlyList<double> closes, int fast = 12, int slow = 26, int signal = 9)
    {
        var emaFast = EMA(closes, fast);
        var emaSlow = EMA(closes, slow);
        var dif = new double[closes.Count];
        for (int i = 0; i < closes.Count; i++) dif[i] = emaFast[i] - emaSlow[i];
        var dea = EMA(dif, signal);
        return (dif, dea);
    }

    /// <summary>Bollinger Bands: middle = SMA(close, period), upper/lower = middle +/- k * stddev.</summary>
    public static (double[] Middle, double[] Upper, double[] Lower) BOLL(IReadOnlyList<double> closes, int period = 20, double k = 2.0)
    {
        var middle = SMA(closes, period);
        var upper = new double[closes.Count];
        var lower = new double[closes.Count];

        for (int i = 0; i < closes.Count; i++)
        {
            if (i < period - 1) { upper[i] = double.NaN; lower[i] = double.NaN; continue; }
            double sumSq = 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                var diff = closes[j] - middle[i];
                sumSq += diff * diff;
            }
            var stdDev = Math.Sqrt(sumSq / period);
            upper[i] = middle[i] + k * stdDev;
            lower[i] = middle[i] - k * stdDev;
        }

        return (middle, upper, lower);
    }

    /// <summary>KDJ stochastic oscillator. RSV = (close - LLV(n)) / (HHV(n) - LLV(n)) * 100 (50 when
    /// flat); K/D are smoothed moving averages of RSV/K seeded at 50. J is computed for
    /// completeness even though the golden-cross method only uses K/D.</summary>
    public static (double[] K, double[] D, double[] J) KDJ(
        IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows, int n = 9, int m1 = 3, int m2 = 3)
    {
        int count = closes.Count;
        var k = new double[count];
        var d = new double[count];
        var j = new double[count];
        double prevK = 50, prevD = 50;
        for (int i = 0; i < count; i++)
        {
            if (i < n - 1)
            {
                k[i] = double.NaN; d[i] = double.NaN; j[i] = double.NaN;
                continue;
            }
            double hhv = double.MinValue, llv = double.MaxValue;
            for (int t = i - n + 1; t <= i; t++)
            {
                hhv = Math.Max(hhv, highs[t]);
                llv = Math.Min(llv, lows[t]);
            }
            double rsv = hhv == llv ? 50 : (closes[i] - llv) / (hhv - llv) * 100;
            double curK = (m1 - 1.0) / m1 * prevK + 1.0 / m1 * rsv;
            double curD = (m2 - 1.0) / m2 * prevD + 1.0 / m2 * curK;
            k[i] = curK;
            d[i] = curD;
            j[i] = 3 * curK - 2 * curD;
            prevK = curK;
            prevD = curD;
        }
        return (k, d, j);
    }

    /// <summary>Wilder-style RSI: first `period` average gain/loss is a plain mean, then smoothed
    /// with a 1/period decay factor.</summary>
    public static double[] RSI(IReadOnlyList<double> closes, int period = 14)
    {
        int count = closes.Count;
        var result = new double[count];
        double avgGain = 0, avgLoss = 0;
        for (int i = 0; i < count; i++)
        {
            if (i == 0) { result[i] = double.NaN; continue; }
            double change = closes[i] - closes[i - 1];
            double gain = Math.Max(change, 0);
            double loss = Math.Max(-change, 0);

            if (i <= period)
            {
                avgGain += gain;
                avgLoss += loss;
                if (i < period) { result[i] = double.NaN; continue; }
                avgGain /= period;
                avgLoss /= period;
            }
            else
            {
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
            }

            result[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        }
        return result;
    }
}
