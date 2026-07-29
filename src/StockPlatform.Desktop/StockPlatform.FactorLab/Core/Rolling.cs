namespace StockPlatform.FactorLab.Core;

/// <summary>逐股票的滚动窗口计算辅助。窗口内有效点不足 minValid 时结果为 NaN。</summary>
public static class Rolling
{
    public static double[][] AllocNaN(int nS, int nD)
    {
        var m = new double[nS][];
        for (int i = 0; i < nS; i++) { m[i] = new double[nD]; Array.Fill(m[i], double.NaN); }
        return m;
    }

    /// <summary>对每只股票、每个日期 t 调用 f(row, t) 填充结果矩阵（从 window-1 开始）。</summary>
    public static double[][] Apply(MarketData md, int window, Func<int, int, double> f)
    {
        var res = AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            for (int t = window; t < md.NDays; t++)
                res[s][t] = f(s, t);
        });
        return res;
    }

    /// <summary>窗口 [t-window+1, t] 内 values 的均值。</summary>
    public static double WindowMean(double[] row, int t, int window, int minValid)
    {
        double sum = 0; int n = 0;
        for (int i = t - window + 1; i <= t; i++)
        {
            double v = row[i];
            if (!double.IsNaN(v)) { sum += v; n++; }
        }
        return n >= minValid ? sum / n : double.NaN;
    }

    /// <summary>窗口内日收益率（相邻有效收盘价比值-1）的样本标准差。</summary>
    public static double WindowRetStd(double[] close, int t, int window, int minValid)
    {
        Span<double> rets = stackalloc double[window];
        int n = 0;
        double prev = double.NaN;
        for (int i = t - window; i <= t; i++)
        {
            double c = close[i];
            if (double.IsNaN(c) || c <= 0) continue;
            if (!double.IsNaN(prev)) rets[n++] = c / prev - 1;
            prev = c;
        }
        if (n < minValid) return double.NaN;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += rets[i];
        mean /= n;
        double ss = 0;
        for (int i = 0; i < n; i++) { double d = rets[i] - mean; ss += d * d; }
        return Math.Sqrt(ss / (n - 1));
    }

    /// <summary>窗口内两序列（成对有效）的 Spearman 秩相关。</summary>
    public static double WindowSpearman(double[] x, double[] y, int t, int window, int minValid)
    {
        var xs = new List<double>(window);
        var ys = new List<double>(window);
        for (int i = t - window + 1; i <= t; i++)
        {
            if (double.IsNaN(x[i]) || double.IsNaN(y[i])) continue;
            xs.Add(x[i]); ys.Add(y[i]);
        }
        if (xs.Count < minValid) return double.NaN;
        return Stats.Spearman(xs.ToArray(), ys.ToArray());
    }
}
