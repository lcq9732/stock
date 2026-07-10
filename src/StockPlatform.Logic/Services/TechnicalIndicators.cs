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

    // ===================================================================================
    // 2026-07-10 扩充的整套常用副图指标（行情详情"副图"下拉框可选）。全部只依赖已抓取的
    // 开/高/低/收/量，跟上面几个一样：返回数组跟输入序列等长，历史不足处填 NaN。参数默认值
    // 取通达信/同花顺的常见默认，跟这个平台其它地方一样先不做成界面可调（三角收敛法例外）。
    // ===================================================================================

    /// <summary>对可能带 NaN 预热段的派生序列求简单移动平均：窗口内只要有一个 NaN 就整段判 NaN，
    /// 不像 <see cref="SMA"/> 那样会把 NaN 混进求和。用于给 MTM/ROC/DMA 等派生量再叠一层均线。</summary>
    private static double[] MaOf(IReadOnlyList<double> values, int period)
    {
        int n = values.Count;
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (i < period - 1) { r[i] = double.NaN; continue; }
            double sum = 0; bool ok = true;
            for (int j = i - period + 1; j <= i; j++) { if (double.IsNaN(values[j])) { ok = false; break; } sum += values[j]; }
            r[i] = ok ? sum / period : double.NaN;
        }
        return r;
    }

    /// <summary>Wilder 平滑（RMA）：跳过开头的 NaN，从第一个非 NaN 起先取 period 个的算术平均做种子，
    /// 之后 prev*(period-1)/period + cur/period。DMI 用它平滑 TR/DM/DX。</summary>
    private static double[] Wilder(IReadOnlyList<double> values, int period)
    {
        int n = values.Count;
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = double.NaN;
        int start = 0;
        while (start < n && double.IsNaN(values[start])) start++;
        double prev = 0; int count = 0; double sum = 0;
        for (int i = start; i < n; i++)
        {
            double v = double.IsNaN(values[i]) ? 0 : values[i];
            if (count < period)
            {
                sum += v; count++;
                if (count == period) { prev = sum / period; r[i] = prev; }
            }
            else { prev = (prev * (period - 1) + v) / period; r[i] = prev; }
        }
        return r;
    }

    /// <summary>抛物线转向 SAR（加速因子起始/步长0.02，上限0.2）——趋势跟踪/止损点。</summary>
    public static double[] SAR(IReadOnlyList<double> highs, IReadOnlyList<double> lows, double step = 0.02, double max = 0.2)
    {
        int n = highs.Count;
        var sar = new double[n];
        if (n == 0) return sar;
        if (n == 1) { sar[0] = lows[0]; return sar; }
        bool isLong = highs[1] >= highs[0];
        double af = step;
        double ep = isLong ? highs[0] : lows[0];
        sar[0] = isLong ? lows[0] : highs[0];
        for (int i = 1; i < n; i++)
        {
            sar[i] = sar[i - 1] + af * (ep - sar[i - 1]);
            if (isLong)
            {
                double clamp = Math.Min(lows[i - 1], i >= 2 ? lows[i - 2] : lows[i - 1]);
                sar[i] = Math.Min(sar[i], clamp);
                if (lows[i] < sar[i]) { isLong = false; sar[i] = ep; ep = lows[i]; af = step; }
                else if (highs[i] > ep) { ep = highs[i]; af = Math.Min(af + step, max); }
            }
            else
            {
                double clamp = Math.Max(highs[i - 1], i >= 2 ? highs[i - 2] : highs[i - 1]);
                sar[i] = Math.Max(sar[i], clamp);
                if (highs[i] > sar[i]) { isLong = true; sar[i] = ep; ep = highs[i]; af = step; }
                else if (lows[i] < ep) { ep = lows[i]; af = Math.Min(af + step, max); }
            }
        }
        return sar;
    }

    /// <summary>动向指标 DMI：+DI/-DI（N=14）判方向，ADX/ADXR（M=6平滑）判趋势强度。</summary>
    public static (double[] PlusDI, double[] MinusDI, double[] Adx, double[] Adxr) DMI(
        IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, int n = 14, int m = 6)
    {
        int len = highs.Count;
        var tr = new double[len]; var plusDM = new double[len]; var minusDM = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i == 0) { tr[i] = highs[i] - lows[i]; continue; }
            double hl = highs[i] - lows[i];
            tr[i] = Math.Max(hl, Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
            double up = highs[i] - highs[i - 1];
            double dn = lows[i - 1] - lows[i];
            plusDM[i] = (up > dn && up > 0) ? up : 0;
            minusDM[i] = (dn > up && dn > 0) ? dn : 0;
        }
        var trN = Wilder(tr, n); var plusN = Wilder(plusDM, n); var minusN = Wilder(minusDM, n);
        var pdi = new double[len]; var mdi = new double[len]; var dx = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (double.IsNaN(trN[i]) || trN[i] == 0) { pdi[i] = double.NaN; mdi[i] = double.NaN; dx[i] = double.NaN; continue; }
            pdi[i] = 100 * plusN[i] / trN[i];
            mdi[i] = 100 * minusN[i] / trN[i];
            double s = pdi[i] + mdi[i];
            dx[i] = s == 0 ? 0 : 100 * Math.Abs(pdi[i] - mdi[i]) / s;
        }
        var adx = Wilder(dx, m);
        var adxr = new double[len];
        for (int i = 0; i < len; i++)
            adxr[i] = (i >= m && !double.IsNaN(adx[i]) && !double.IsNaN(adx[i - m])) ? (adx[i] + adx[i - m]) / 2 : double.NaN;
        return (pdi, mdi, adx, adxr);
    }

    /// <summary>乖离率 BIAS：收盘价偏离 N 日均线的百分比。BIAS = (close - MA(N)) / MA(N) * 100。</summary>
    public static double[] BIAS(IReadOnlyList<double> closes, int n)
    {
        var ma = SMA(closes, n);
        var r = new double[closes.Count];
        for (int i = 0; i < closes.Count; i++)
            r[i] = (double.IsNaN(ma[i]) || ma[i] == 0) ? double.NaN : (closes[i] - ma[i]) / ma[i] * 100;
        return r;
    }

    /// <summary>顺势指标 CCI（N=14）：TP=(H+L+C)/3，CCI=(TP-MA(TP))/(0.015*平均绝对偏差)。</summary>
    public static double[] CCI(IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, int n = 14)
    {
        int len = closes.Count;
        var tp = new double[len];
        for (int i = 0; i < len; i++) tp[i] = (highs[i] + lows[i] + closes[i]) / 3;
        var maTp = SMA(tp, n);
        var cci = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i < n - 1) { cci[i] = double.NaN; continue; }
            double dev = 0;
            for (int j = i - n + 1; j <= i; j++) dev += Math.Abs(tp[j] - maTp[i]);
            dev /= n;
            cci[i] = dev == 0 ? 0 : (tp[i] - maTp[i]) / (0.015 * dev);
        }
        return cci;
    }

    /// <summary>威廉指标 WR（通达信口径，0~100，0在顶=超买）：WR=(HHV(H,N)-C)/(HHV-LLV)*100。</summary>
    public static double[] WR(IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, int n)
    {
        int len = closes.Count;
        var wr = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i < n - 1) { wr[i] = double.NaN; continue; }
            double hhv = double.MinValue, llv = double.MaxValue;
            for (int j = i - n + 1; j <= i; j++) { hhv = Math.Max(hhv, highs[j]); llv = Math.Min(llv, lows[j]); }
            wr[i] = hhv == llv ? 0 : (hhv - closes[i]) / (hhv - llv) * 100;
        }
        return wr;
    }

    /// <summary>动量 MTM（N=12，均线M=6）：MTM=close-close[N前]。</summary>
    public static (double[] Mtm, double[] MtmMa) MTM(IReadOnlyList<double> closes, int n = 12, int m = 6)
    {
        int len = closes.Count;
        var mtm = new double[len];
        for (int i = 0; i < len; i++) mtm[i] = i >= n ? closes[i] - closes[i - n] : double.NaN;
        return (mtm, MaOf(mtm, m));
    }

    /// <summary>变动率 ROC（N=12，均线M=6）：ROC=(close-close[N前])/close[N前]*100。</summary>
    public static (double[] Roc, double[] RocMa) ROC(IReadOnlyList<double> closes, int n = 12, int m = 6)
    {
        int len = closes.Count;
        var roc = new double[len];
        for (int i = 0; i < len; i++) roc[i] = (i >= n && closes[i - n] != 0) ? (closes[i] - closes[i - n]) / closes[i - n] * 100 : double.NaN;
        return (roc, MaOf(roc, m));
    }

    /// <summary>三重指数平滑 TRIX（N=12，信号线M=9）：对收盘价做三重EMA后取变化率。</summary>
    public static (double[] Trix, double[] TrixMa) TRIX(IReadOnlyList<double> closes, int n = 12, int m = 9)
    {
        var e3 = EMA(EMA(EMA(closes, n), n), n);
        int len = closes.Count;
        var trix = new double[len];
        trix[0] = double.NaN;
        for (int i = 1; i < len; i++) trix[i] = e3[i - 1] != 0 ? (e3[i] - e3[i - 1]) / e3[i - 1] * 100 : double.NaN;
        return (trix, MaOf(trix, m));
    }

    /// <summary>平行线差 DMA（短10/长50，均线M=10）：DMA=MA(close,10)-MA(close,50)。</summary>
    public static (double[] Dma, double[] Ama) DMA(IReadOnlyList<double> closes, int shortN = 10, int longN = 50, int m = 10)
    {
        var s = SMA(closes, shortN); var l = SMA(closes, longN);
        int len = closes.Count;
        var dma = new double[len];
        for (int i = 0; i < len; i++) dma[i] = (double.IsNaN(s[i]) || double.IsNaN(l[i])) ? double.NaN : s[i] - l[i];
        return (dma, MaOf(dma, m));
    }

    /// <summary>能量潮 OBV（均线M=30）：涨日累加成交量、跌日累减，起点0。</summary>
    public static (double[] Obv, double[] ObvMa) OBV(IReadOnlyList<double> closes, IReadOnlyList<double> volumes, int m = 30)
    {
        int len = closes.Count;
        var obv = new double[len];
        double cum = 0;
        for (int i = 0; i < len; i++)
        {
            if (i > 0)
            {
                if (closes[i] > closes[i - 1]) cum += volumes[i];
                else if (closes[i] < closes[i - 1]) cum -= volumes[i];
            }
            obv[i] = cum;
        }
        return (obv, MaOf(obv, m));
    }

    /// <summary>成交量变异率 VR（N=26，均线M=6）：N日内涨日量与跌日量之比（平盘量各计一半）。</summary>
    public static (double[] Vr, double[] VrMa) VR(IReadOnlyList<double> closes, IReadOnlyList<double> volumes, int n = 26, int m = 6)
    {
        int len = closes.Count;
        var vr = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i < n) { vr[i] = double.NaN; continue; }
            double up = 0, down = 0, flat = 0;
            for (int j = i - n + 1; j <= i; j++)
            {
                if (closes[j] > closes[j - 1]) up += volumes[j];
                else if (closes[j] < closes[j - 1]) down += volumes[j];
                else flat += volumes[j];
            }
            double denom = down + flat / 2;
            vr[i] = denom == 0 ? double.NaN : (up + flat / 2) / denom * 100;
        }
        return (vr, MaOf(vr, m));
    }

    /// <summary>资金流量指标 MFI（N=14）：带量的RSI，TP=(H+L+C)/3，MF=TP*量分正负累计。</summary>
    public static double[] MFI(IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, IReadOnlyList<double> volumes, int n = 14)
    {
        int len = closes.Count;
        var tp = new double[len];
        for (int i = 0; i < len; i++) tp[i] = (highs[i] + lows[i] + closes[i]) / 3;
        var mfi = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i < n) { mfi[i] = double.NaN; continue; }
            double pos = 0, neg = 0;
            for (int j = i - n + 1; j <= i; j++)
            {
                double mf = tp[j] * volumes[j];
                if (tp[j] > tp[j - 1]) pos += mf;
                else if (tp[j] < tp[j - 1]) neg += mf;
            }
            mfi[i] = neg == 0 ? 100 : 100 - 100 / (1 + pos / neg);
        }
        return mfi;
    }

    /// <summary>简易波动 EMV（N=14，均线M=9，通达信口径）：价格中点变动结合成交量的轻重。</summary>
    public static (double[] Emv, double[] EmvMa) EMV(IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> volumes, int n = 14, int m = 9)
    {
        int len = highs.Count;
        var volMa = SMA(volumes, n);
        var raw = new double[len];
        for (int i = 0; i < len; i++)
        {
            double hl = highs[i] + lows[i];
            if (i == 0 || double.IsNaN(volMa[i]) || volMa[i] == 0 || hl == 0) { raw[i] = double.NaN; continue; }
            double mid = 100 * (hl - (highs[i - 1] + lows[i - 1])) / hl;
            raw[i] = mid * (highs[i] - lows[i]) / volMa[i];
        }
        var emv = MaOf(raw, n);
        return (emv, MaOf(emv, m));
    }

    /// <summary>心理线 PSY（N=12，均线M=6）：N日内上涨天数占比*100。</summary>
    public static (double[] Psy, double[] PsyMa) PSY(IReadOnlyList<double> closes, int n = 12, int m = 6)
    {
        int len = closes.Count;
        var psy = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i < n) { psy[i] = double.NaN; continue; }
            int up = 0;
            for (int j = i - n + 1; j <= i; j++) if (closes[j] > closes[j - 1]) up++;
            psy[i] = 100.0 * up / n;
        }
        return (psy, MaOf(psy, m));
    }

    /// <summary>人气意愿 ARBR（N=26）：AR=Σ(H-O)/Σ(O-L)*100；BR=Σmax(0,H-Cy)/Σmax(0,Cy-L)*100。</summary>
    public static (double[] Ar, double[] Br) ARBR(
        IReadOnlyList<double> opens, IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, int n = 26)
    {
        int len = closes.Count;
        var ar = new double[len]; var br = new double[len];
        for (int i = 0; i < len; i++)
        {
            if (i >= n - 1)
            {
                double ho = 0, ol = 0;
                for (int j = i - n + 1; j <= i; j++) { ho += highs[j] - opens[j]; ol += opens[j] - lows[j]; }
                ar[i] = ol == 0 ? double.NaN : ho / ol * 100;
            }
            else ar[i] = double.NaN;

            if (i >= n)
            {
                double hcp = 0, cpl = 0;
                for (int j = i - n + 1; j <= i; j++) { hcp += Math.Max(0, highs[j] - closes[j - 1]); cpl += Math.Max(0, closes[j - 1] - lows[j]); }
                br[i] = cpl == 0 ? double.NaN : hcp / cpl * 100;
            }
            else br[i] = double.NaN;
        }
        return (ar, br);
    }

    /// <summary>振动升降指标 ASI（均线M=6，通达信口径）：累计振动值 SI，滤掉盘整假突破。</summary>
    public static (double[] Asi, double[] AsiMa) ASI(
        IReadOnlyList<double> opens, IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, int m = 6)
    {
        int len = closes.Count;
        var asi = new double[len];
        double cum = 0;
        for (int i = 0; i < len; i++)
        {
            if (i == 0) { asi[i] = double.NaN; continue; }
            double lc = closes[i - 1];
            double aa = Math.Abs(highs[i] - lc);
            double bb = Math.Abs(lows[i] - lc);
            double cc = Math.Abs(highs[i] - lows[i - 1]);
            double dd = Math.Abs(lc - opens[i - 1]);
            double r = (aa > bb && aa > cc) ? aa + bb / 2 + dd / 4
                     : (bb > cc && bb > aa) ? bb + aa / 2 + dd / 4
                     : cc + dd / 4;
            double x = (closes[i] - lc) + (closes[i] - opens[i]) / 2 + (lc - opens[i - 1]);
            double si = r == 0 ? 0 : 16 * x / r * Math.Max(aa, bb);
            cum += si;
            asi[i] = cum;
        }
        return (asi, MaOf(asi, m));
    }
}
