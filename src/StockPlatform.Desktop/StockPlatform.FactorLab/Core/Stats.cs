namespace StockPlatform.FactorLab.Core;

/// <summary>秩相关等基础统计。截面因子评估全部用 Spearman（对异常值稳健，A股截面重尾）。</summary>
public static class Stats
{
    /// <summary>平均秩（并列取平均），输入无 NaN。</summary>
    public static double[] Ranks(double[] values)
    {
        int n = values.Length;
        var idx = Enumerable.Range(0, n).ToArray();
        Array.Sort(idx, (a, b) => values[a].CompareTo(values[b]));
        var ranks = new double[n];
        int i = 0;
        while (i < n)
        {
            int j = i;
            while (j + 1 < n && values[idx[j + 1]] == values[idx[i]]) j++;
            double avg = (i + j) / 2.0 + 1;
            for (int k = i; k <= j; k++) ranks[idx[k]] = avg;
            i = j + 1;
        }
        return ranks;
    }

    public static double Spearman(double[] x, double[] y)
    {
        if (x.Length < 3) return double.NaN;
        return Pearson(Ranks(x), Ranks(y));
    }

    public static double Pearson(double[] x, double[] y)
    {
        int n = x.Length;
        double sx = 0, sy = 0;
        for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; }
        double mx = sx / n, my = sy / n;
        double cov = 0, vx = 0, vy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            cov += dx * dy; vx += dx * dx; vy += dy * dy;
        }
        if (vx <= 0 || vy <= 0) return double.NaN;
        return cov / Math.Sqrt(vx * vy);
    }

    public static double Mean(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return double.NaN;
        double s = 0; foreach (var x in xs) s += x;
        return s / xs.Count;
    }

    public static double Std(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return double.NaN;
        double m = Mean(xs), s = 0;
        foreach (var x in xs) s += (x - m) * (x - m);
        return Math.Sqrt(s / (xs.Count - 1));
    }

    /// <summary>周期收益序列复利年化。periodDays=每期交易日数。</summary>
    public static double Annualize(IReadOnlyList<double> periodReturns, double periodDays)
    {
        if (periodReturns.Count == 0) return double.NaN;
        double nav = 1;
        foreach (var r in periodReturns) nav *= 1 + r;
        if (nav <= 0) return -1;
        double years = periodReturns.Count * periodDays / Config.TradingDaysPerYear;
        return Math.Pow(nav, 1 / years) - 1;
    }
}
