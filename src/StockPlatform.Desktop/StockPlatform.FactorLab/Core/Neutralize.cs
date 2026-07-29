namespace StockPlatform.FactorLab.Core;

/// <summary>
/// 截面中性化：winsorize(1%) → zscore → 行业内去均值 → 对市值(zscore后的对数近似市值)一元回归取残差。
/// 用途：判断因子在剥离规模/行业风格后是否还有独立信息（"纯度"）。分组回测仍用原始因子——实盘交易的是原始值。
/// 行业覆盖率仅约46%，无归属股票归为一个"未知"组一起去均值，效果打折但不损失样本。
/// </summary>
public static class Neutralize
{
    /// <summary>对一个截面（valid 顺序对齐的因子值）做中性化，返回残差。size=对数近似市值（可含NaN，NaN按截面均值处理）。</summary>
    public static double[] Residualize(double[] factor, double[] size, int[] industry)
    {
        int n = factor.Length;
        var f = Winsorize(factor);
        ZscoreInPlace(f);

        var sz = Winsorize(size);
        ZscoreInPlace(sz);
        for (int i = 0; i < n; i++) if (double.IsNaN(sz[i])) sz[i] = 0; // 无市值的按截面均值(0)处理

        // 行业内去均值（-1 未知组也作为一组）
        var sums = new Dictionary<int, (double Sum, int N)>();
        for (int i = 0; i < n; i++)
        {
            var cur = sums.GetValueOrDefault(industry[i]);
            sums[industry[i]] = (cur.Sum + f[i], cur.N + 1);
        }
        for (int i = 0; i < n; i++)
        {
            var (sum, cnt) = sums[industry[i]];
            f[i] -= sum / cnt;
        }

        // 对 size 一元 OLS 取残差
        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += sz[i] * f[i]; sxx += sz[i] * sz[i]; }
        double beta = sxx > 1e-12 ? sxy / sxx : 0;
        for (int i = 0; i < n; i++) f[i] -= beta * sz[i];
        return f;
    }

    /// <summary>双侧1%分位截尾（返回副本）。</summary>
    static double[] Winsorize(double[] xs)
    {
        var copy = (double[])xs.Clone();
        var sorted = xs.Where(x => !double.IsNaN(x)).ToArray();
        if (sorted.Length < 20) return copy;
        Array.Sort(sorted);
        double lo = sorted[(int)(sorted.Length * 0.01)];
        double hi = sorted[(int)(sorted.Length * 0.99)];
        for (int i = 0; i < copy.Length; i++)
            if (!double.IsNaN(copy[i])) copy[i] = Math.Clamp(copy[i], lo, hi);
        return copy;
    }

    static void ZscoreInPlace(double[] xs)
    {
        double sum = 0; int n = 0;
        foreach (var x in xs) if (!double.IsNaN(x)) { sum += x; n++; }
        if (n < 2) return;
        double mean = sum / n, ss = 0;
        foreach (var x in xs) if (!double.IsNaN(x)) ss += (x - mean) * (x - mean);
        double std = Math.Sqrt(ss / (n - 1));
        if (std < 1e-12) return;
        for (int i = 0; i < xs.Length; i++)
            if (!double.IsNaN(xs[i])) xs[i] = (xs[i] - mean) / std;
    }
}
