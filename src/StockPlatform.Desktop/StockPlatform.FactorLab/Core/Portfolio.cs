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
///
/// 2026-07-31 增加流动性过滤变体：十年数据显示原始 Top50 跑输等权基准，而 D10（前10%≈500只）年化 ~+10%，
/// 怀疑合成得分最极端的尾巴集中了"冷落到没有流动性的死票"（本因子库以缩量/低波/冷落为主，越极端越危险）。
/// 变体先剔除近20日均成交额低于截面中位数的一半，再取Top——对照两条曲线即可验证/证伪这个假设。
/// </summary>
public static class Portfolio
{
    /// <summary>一个策略变体：从合成得分排序里取前 N，可选先剔除流动性差的一半。</summary>
    private sealed record Variant(string Name, int TopN, bool LiquidityFilter, bool Timing);

    public static PortfolioResult Run(FactorResult factor, MarketData md, SharedEval sh)
    {
        int nP = sh.Periods.Count, nS = md.NStocks;
        var variants = new Variant[]
        {
            new($"Top{Config.TopN}", Config.TopN, false, false),
            new($"Top{Config.TopN}·流动性过滤", Config.TopN, true, false),
            new($"Top100·流动性过滤", 100, true, false),
            new($"Top100·流动性过滤+MA{Config.TimingMaWindow}择时", 100, true, true),
        };
        var rets = new double[variants.Length][];
        for (int v = 0; v < variants.Length; v++) rets[v] = new double[nP];
        var prevHolds = new HashSet<int>[variants.Length];
        var prevInvested = new bool[variants.Length];
        for (int v = 0; v < variants.Length; v++) { prevHolds[v] = new HashSet<int>(); prevInvested[v] = true; }

        var benchRet = new double[nP];
        var benchTimedRet = new double[nP];
        var timingLong = new bool[nP];

        for (int p = 0; p < nP; p++)
        {
            int t = sh.Periods[p].T;
            timingLong[p] = IsIndexAboveMa(md, t);

            // 选股池：可交易 且 因子有值
            var valid = new List<int>(nS);
            for (int s = 0; s < nS; s++)
                if (sh.Tradable[p][s] && !double.IsNaN(factor.Snapshot[p][s])) valid.Add(s);

            benchRet[p] = sh.BenchRet[p];
            benchTimedRet[p] = timingLong[p] ? sh.BenchRet[p] : 0; // 基准择时不计成本，作信息参照

            // 流动性过滤池：剔除近20日均成交额低于截面中位数的一半（只在需要的变体上用）
            List<int>? liquid = null;
            if (valid.Count >= Config.TopN * 2)
            {
                var avgAmt = valid.ToDictionary(s => s, s => AvgAmount(md, s, t));
                var sorted = avgAmt.Values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
                if (sorted.Length > 0)
                {
                    double median = sorted[sorted.Length / 2];
                    liquid = valid.Where(s => !double.IsNaN(avgAmt[s]) && avgAmt[s] >= median).ToList();
                }
            }

            for (int v = 0; v < variants.Length; v++)
            {
                var pool = variants[v].LiquidityFilter ? liquid : valid;
                if (pool == null || pool.Count < variants[v].TopN * 2)
                {
                    rets[v][p] = double.NaN;
                    prevHolds[v].Clear();
                    continue;
                }
                var top = pool.OrderByDescending(s => factor.Snapshot[p][s]).Take(variants[v].TopN).ToList();
                double gross = top.Average(s => sh.PeriodRet[p][s]);
                var cur = new HashSet<int>(top);
                double turnover = prevHolds[v].Count == 0 ? 1.0 : 1.0 - cur.Count(prevHolds[v].Contains) / (double)cur.Count;

                if (!variants[v].Timing)
                {
                    rets[v][p] = gross - turnover * Config.RoundTripCost;
                }
                else if (timingLong[p])
                {
                    double timedTurnover = prevInvested[v] ? turnover : 1.0;
                    rets[v][p] = gross - timedTurnover * Config.RoundTripCost;
                    prevInvested[v] = true;
                }
                else
                {
                    rets[v][p] = 0;
                    prevInvested[v] = false;
                }
                prevHolds[v] = cur;
            }
        }

        var curves = new List<PortfolioCurve>();
        for (int v = 0; v < variants.Length; v++)
            curves.Add(PortfolioCurve.Finish(variants[v].Name, rets[v], sh));
        curves.Add(PortfolioCurve.Finish("等权基准", benchRet, sh));
        curves.Add(PortfolioCurve.Finish($"等权基准+MA{Config.TimingMaWindow}择时", benchTimedRet, sh));

        return new PortfolioResult
        {
            FactorName = factor.Factor.Name,
            TimingLong = timingLong,
            Curves = curves,
        };
    }

    /// <summary>近20日均成交额（元），有效天数不足10天算 NaN（长期停牌/刚复牌的没法判断流动性）。</summary>
    static double AvgAmount(MarketData md, int s, int t)
    {
        double sum = 0; int n = 0;
        for (int i = Math.Max(0, t - 19); i <= t; i++)
        {
            double a = md.Amount[s][i];
            if (!double.IsNaN(a) && a > 0) { sum += a; n++; }
        }
        return n >= 10 ? sum / n : double.NaN;
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
