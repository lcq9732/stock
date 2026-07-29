namespace StockPlatform.FactorLab.Core;

/// <summary>组合回测的一条净值曲线及其指标。</summary>
public sealed class PortfolioCurve
{
    public required string Name { get; init; }
    /// <summary>每期收益（与 SharedEval.Periods 对齐；跳过期为 NaN 按 0 处理）。</summary>
    public required double[] PeriodRet { get; init; }
    public double AnnFull, AnnIn, AnnOut, MaxDrawdown, Sharpe;

    public static PortfolioCurve Finish(string name, double[] rets, SharedEval sh)
    {
        var c = new PortfolioCurve { Name = name, PeriodRet = rets };
        var all = new List<double>(); var inS = new List<double>(); var outS = new List<double>();
        double nav = 1, peak = 1, mdd = 0;
        for (int p = 0; p < rets.Length; p++)
        {
            double r = double.IsNaN(rets[p]) ? 0 : rets[p];
            all.Add(r);
            (sh.Periods[p].InSample ? inS : outS).Add(r);
            nav *= 1 + r;
            if (nav > peak) peak = nav;
            mdd = Math.Max(mdd, 1 - nav / peak);
        }
        c.AnnFull = Stats.Annualize(all, Config.HoldDays);
        c.AnnIn = Stats.Annualize(inS, Config.HoldDays);
        c.AnnOut = Stats.Annualize(outS, Config.HoldDays);
        c.MaxDrawdown = mdd;
        double mean = Stats.Mean(all), std = Stats.Std(all);
        c.Sharpe = std > 0 ? mean / std * Math.Sqrt(Config.TradingDaysPerYear / Config.HoldDays) : double.NaN;
        return c;
    }
}

public sealed class PortfolioResult
{
    public required string FactorName { get; init; }
    public required List<PortfolioCurve> Curves { get; init; }
    /// <summary>每期择时状态（true=持仓）。</summary>
    public required bool[] TimingLong { get; init; }
}

/// <summary>
/// 组合级回测：每期按合成因子取 Top N 等权持有，扣换手成本；可叠加指数 MA 择时（收盘&lt;MA60 → 空仓）。
/// 与十分组评估的区别：持仓集中（N只 vs 十分之一池）、成本按组合实际换手、输出净值/回撤/夏普等组合指标。
/// </summary>
public static class Portfolio
{
    public static PortfolioResult Run(FactorResult factor, MarketData md, SharedEval sh)
    {
        int nP = sh.Periods.Count, nS = md.NStocks;
        var stratRet = new double[nP];
        var stratTimedRet = new double[nP];
        var benchRet = new double[nP];
        var benchTimedRet = new double[nP];
        var timingLong = new bool[nP];

        var prevHold = new HashSet<int>();
        bool prevInvested = false;
        for (int p = 0; p < nP; p++)
        {
            int t = sh.Periods[p].T;
            timingLong[p] = IsIndexAboveMa(md, t);

            // 选股：可交易 且 因子有值 → Top N
            var valid = new List<int>(nS);
            for (int s = 0; s < nS; s++)
                if (sh.Tradable[p][s] && !double.IsNaN(factor.Snapshot[p][s])) valid.Add(s);

            if (valid.Count < Config.TopN * 2)
            {
                stratRet[p] = double.NaN; stratTimedRet[p] = double.NaN;
                benchRet[p] = sh.BenchRet[p]; benchTimedRet[p] = timingLong[p] ? sh.BenchRet[p] : 0;
                prevHold.Clear(); prevInvested = false;
                continue;
            }

            var top = valid.OrderByDescending(s => factor.Snapshot[p][s]).Take(Config.TopN).ToList();
            double gross = top.Average(s => sh.PeriodRet[p][s]);
            var cur = new HashSet<int>(top);

            // 不择时曲线的换手成本
            double turnover = prevHold.Count == 0 ? 1.0 : 1.0 - cur.Count(prevHold.Contains) / (double)cur.Count;
            stratRet[p] = gross - turnover * Config.RoundTripCost;

            // 择时曲线：空仓期收益0；重新进场付全额建仓成本
            if (timingLong[p])
            {
                double timedTurnover = prevInvested ? turnover : 1.0;
                stratTimedRet[p] = gross - timedTurnover * Config.RoundTripCost;
                prevInvested = true;
            }
            else
            {
                stratTimedRet[p] = 0;
                prevInvested = false;
            }

            benchRet[p] = sh.BenchRet[p];
            benchTimedRet[p] = timingLong[p] ? sh.BenchRet[p] : 0; // 基准择时不计成本，作信息参照
            prevHold = cur;
        }

        return new PortfolioResult
        {
            FactorName = factor.Factor.Name,
            TimingLong = timingLong,
            Curves =
            [
                PortfolioCurve.Finish($"Top{Config.TopN}", stratRet, sh),
                PortfolioCurve.Finish($"Top{Config.TopN}+MA{Config.TimingMaWindow}择时", stratTimedRet, sh),
                PortfolioCurve.Finish("等权基准", benchRet, sh),
                PortfolioCurve.Finish($"等权基准+MA{Config.TimingMaWindow}择时", benchTimedRet, sh),
            ],
        };
    }

    /// <summary>调仓日收盘时上证指数是否在 MA 之上（数据不足按持仓处理）。</summary>
    static bool IsIndexAboveMa(MarketData md, int t)
    {
        if (t < Config.TimingMaWindow) return true;
        double c = md.IndexClose[t];
        if (double.IsNaN(c)) return true;
        double sum = 0; int n = 0;
        for (int i = t - Config.TimingMaWindow + 1; i <= t; i++)
        {
            double v = md.IndexClose[i];
            if (!double.IsNaN(v)) { sum += v; n++; }
        }
        return n < Config.TimingMaWindow / 2 || c >= sum / n;
    }
}
