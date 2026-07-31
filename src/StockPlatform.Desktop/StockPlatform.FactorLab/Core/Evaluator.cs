namespace StockPlatform.FactorLab.Core;

/// <summary>一个调仓期：T日收盘算因子，T+1开盘建仓，T+1+HoldDays开盘结算。</summary>
public sealed record Period(int T, DateOnly Date, bool InSample);

/// <summary>因子无关的共享评估数据（每期收益、可交易掩码、等权基准），全部因子复用。</summary>
public sealed class SharedEval
{
    public required List<Period> Periods { get; init; }
    /// <summary>[期][股票] 持有期收益（开盘→开盘）。不可交易为 NaN。</summary>
    public required double[][] PeriodRet { get; init; }
    /// <summary>[期][股票] 是否可入池且可买入（池规则+非一字板+当日有开盘价）。</summary>
    public required bool[][] Tradable { get; init; }
    /// <summary>等权基准：每期全体可交易股票的平均收益。</summary>
    public required double[] BenchRet { get; init; }
}

public sealed class IcStats
{
    public double Mean, Std, Icir, WinRate;
    public int N;
    public static IcStats From(IReadOnlyList<double> ics)
    {
        var s = new IcStats { N = ics.Count };
        if (ics.Count == 0)
        {
            // 没有任何有效期（比如基本面因子在财报数据抓取前）——显示为"-"而不是误导性的 0.000
            s.Mean = s.Std = s.Icir = s.WinRate = double.NaN;
            return s;
        }
        s.Mean = Stats.Mean(ics);
        s.Std = Stats.Std(ics);
        s.Icir = s.Std > 0 ? s.Mean / s.Std : double.NaN;
        s.WinRate = ics.Count(x => x > 0) / (double)ics.Count;
        return s;
    }
}

public sealed class FactorResult
{
    public required IFactor Factor { get; init; }
    /// <summary>每期 RankIC（该期截面不足时为 NaN）。</summary>
    public required double[] IcSeries { get; init; }
    /// <summary>每期市值/行业中性化后的 RankIC。</summary>
    public required double[] IcNeuSeries { get; init; }
    public required IcStats IcIn { get; init; }
    public required IcStats IcOut { get; init; }
    public required IcStats IcNeuIn { get; init; }
    public required IcStats IcNeuOut { get; init; }
    public required SortedDictionary<int, double> YearlyIc { get; init; }
    /// <summary>[期][分组] 分组等权收益（组0=因子值最低，组9=最高）。跳过期为 NaN。</summary>
    public required double[][] DecileRet { get; init; }
    public required double[] TopTurnover { get; init; }
    public double LsAnnIn, LsAnnOut, TopNetAnnIn, TopNetAnnOut;
    public double AvgTurnover;
    public required double[] DecileAnnFull { get; init; }
    /// <summary>[期][股票] 因子值在各调仓日的快照，用于因子相关性矩阵。</summary>
    public required double[][] Snapshot { get; init; }
    /// <summary>最后一个交易日的因子值（用于"今日名单"，该日通常不是调仓日）。</summary>
    public required double[] LatestValues { get; init; }
}

public static class Evaluator
{
    public static SharedEval BuildShared(MarketData md)
    {
        int nD = md.NDays, nS = md.NStocks;
        var periods = new List<Period>();
        for (int t = Config.WarmupDays; t + Config.HoldDays + 1 < nD; t += Config.HoldDays)
            periods.Add(new Period(t, md.Dates[t], md.Dates[t] < Config.SplitDate));

        int nP = periods.Count;
        var ret = new double[nP][];
        var tradable = new bool[nP][];
        var bench = new double[nP];

        Parallel.For(0, nP, p =>
        {
            var (t, _, _) = (periods[p].T, 0, 0);
            int buyIdx = t + 1, sellIdx = t + Config.HoldDays + 1;
            var r = new double[nS];
            var el = new bool[nS];
            Array.Fill(r, double.NaN);
            double sum = 0; int n = 0;
            for (int s = 0; s < nS; s++)
            {
                if (!IsEligible(md, s, t)) continue;
                double buy = md.Open[s][buyIdx];
                if (double.IsNaN(buy) || buy <= 0) continue;
                if (buy >= LimitUpPrice(md, s, t)) continue; // 一字板开盘买不进
                double sell = ResolveSell(md, s, buyIdx, sellIdx);
                el[s] = true;
                r[s] = sell / buy - 1;
                sum += r[s]; n++;
            }
            ret[p] = r; tradable[p] = el;
            bench[p] = n > 0 ? sum / n : double.NaN;
        });

        return new SharedEval { Periods = periods, PeriodRet = ret, Tradable = tradable, BenchRet = bench };
    }

    /// <summary>池规则：当日有收盘且有成交、非ST（当前名称）、上市满 MinListedDays。
    /// 退市股特殊处理：不做按名剔除（终止时名称几乎都带退/ST，按名剔会把整段历史删掉），
    /// 只剔除临近最后一根K线的 DelistExcludeDays 段（≈退市整理期，当时实盘可从名称/公告获知）。</summary>
    public static bool IsEligible(MarketData md, int s, int t)
    {
        double c = md.Close[s][t], a = md.Amount[s][t];
        if (double.IsNaN(c) || c <= 0 || double.IsNaN(a) || a <= 0) return false;
        if (md.IsDelisted[s])
        {
            if (t > md.LastBarIdx[s] - Config.DelistExcludeDays) return false;
        }
        else if (md.IsSt[s]) return false;
        int first = md.FirstBarIdx[s];
        if (first < 0) return false;
        // 窗口开头几天就有数据的视为老股；窗口中途出现的按上市对待
        if (first > 3 && t < first + Config.MinListedDays) return false;
        return true;
    }

    /// <summary>估算次日涨停价：创业/科创20%，主板ST5%，其余10%（qfq价近似，用于剔除一字板）。</summary>
    static double LimitUpPrice(MarketData md, int s, int t)
    {
        string code = md.Codes[s];
        double pct = code.StartsWith("30") || code.StartsWith("68") ? 0.20 : md.IsSt[s] ? 0.05 : 0.10;
        return Math.Round(md.Close[s][t] * (1 + pct), 2) - 1e-6;
    }

    /// <summary>卖出价：目标日开盘；停牌则顺延最多5日找开盘价，再不行用区间内最后收盘价（退市/长停按最后价清算）。</summary>
    static double ResolveSell(MarketData md, int s, int buyIdx, int sellIdx)
    {
        int last = Math.Min(sellIdx + 5, md.NDays - 1);
        for (int i = sellIdx; i <= last; i++)
        {
            double o = md.Open[s][i];
            if (!double.IsNaN(o) && o > 0) return o;
        }
        for (int i = last; i > buyIdx; i--)
        {
            double c = md.Close[s][i];
            if (!double.IsNaN(c) && c > 0) return c;
        }
        return md.Open[s][buyIdx]; // 完全无后续价：按买入价记平（极端罕见）
    }

    public static FactorResult Evaluate(IFactor factor, MarketData md, SharedEval sh)
    {
        var matrix = factor.Compute(md);
        int nP = sh.Periods.Count, nS = md.NStocks, nG = Config.Deciles;

        var snapshot = new double[nP][];
        var icSeries = new double[nP];
        var icNeuSeries = new double[nP];
        var decileRet = new double[nP][];
        var turnover = new double[nP];
        Array.Fill(icSeries, double.NaN);
        Array.Fill(icNeuSeries, double.NaN);

        var prevTop = new HashSet<int>();
        for (int p = 0; p < nP; p++)
        {
            int t = sh.Periods[p].T;
            var snap = new double[nS];
            for (int s = 0; s < nS; s++) snap[s] = matrix[s][t];
            snapshot[p] = snap;

            var valid = new List<int>(nS);
            for (int s = 0; s < nS; s++)
                if (sh.Tradable[p][s] && !double.IsNaN(snap[s])) valid.Add(s);

            var dr = new double[nG];
            Array.Fill(dr, double.NaN);
            decileRet[p] = dr;
            turnover[p] = double.NaN;
            if (valid.Count < Config.MinCrossSection) { prevTop.Clear(); continue; }

            var f = new double[valid.Count];
            var r = new double[valid.Count];
            for (int i = 0; i < valid.Count; i++) { f[i] = snap[valid[i]]; r[i] = sh.PeriodRet[p][valid[i]]; }
            icSeries[p] = Stats.Spearman(f, r);

            // 中性化IC：剥离市值/行业后的残差与收益的秩相关（分组回测仍用原始因子）
            var size = new double[valid.Count];
            var ind = new int[valid.Count];
            for (int i = 0; i < valid.Count; i++)
            {
                int s = valid[i];
                double cap = md.FloatShares[s] * md.Close[s][t];
                size[i] = double.IsNaN(cap) || cap <= 0 ? double.NaN : Math.Log(cap);
                ind[i] = md.Industry[s];
            }
            icNeuSeries[p] = Stats.Spearman(Neutralize.Residualize(f, size, ind), r);

            // 十分组：按因子值升序，组9=因子值最高
            var order = Enumerable.Range(0, valid.Count).ToArray();
            Array.Sort(order, (a, b) => f[a].CompareTo(f[b]));
            var sums = new double[nG];
            var counts = new int[nG];
            var curTop = new HashSet<int>();
            for (int i = 0; i < order.Length; i++)
            {
                int g = (int)((long)i * nG / order.Length);
                sums[g] += r[order[i]];
                counts[g]++;
                if (g == nG - 1) curTop.Add(valid[order[i]]);
            }
            for (int g = 0; g < nG; g++) dr[g] = counts[g] > 0 ? sums[g] / counts[g] : double.NaN;

            turnover[p] = prevTop.Count == 0 ? 1.0 : 1.0 - curTop.Count(prevTop.Contains) / (double)curTop.Count;
            prevTop = curTop;
        }

        // 汇总
        var icIn = new List<double>(); var icOut = new List<double>();
        var icNeuIn = new List<double>(); var icNeuOut = new List<double>();
        var yearly = new Dictionary<int, List<double>>();
        var lsIn = new List<double>(); var lsOut = new List<double>();
        var topNetIn = new List<double>(); var topNetOut = new List<double>();
        var tos = new List<double>();
        var decileFull = Enumerable.Range(0, nG).Select(_ => new List<double>()).ToArray();

        for (int p = 0; p < nP; p++)
        {
            if (double.IsNaN(icSeries[p])) continue;
            var period = sh.Periods[p];
            (period.InSample ? icIn : icOut).Add(icSeries[p]);
            if (!double.IsNaN(icNeuSeries[p])) (period.InSample ? icNeuIn : icNeuOut).Add(icNeuSeries[p]);
            if (!yearly.TryGetValue(period.Date.Year, out var yl)) yearly[period.Date.Year] = yl = [];
            yl.Add(icSeries[p]);

            double top = decileRet[p][nG - 1], bottom = decileRet[p][0];
            if (!double.IsNaN(top) && !double.IsNaN(bottom))
            {
                (period.InSample ? lsIn : lsOut).Add(top - bottom);
                double net = top - (double.IsNaN(turnover[p]) ? 1 : turnover[p]) * Config.RoundTripCost;
                (period.InSample ? topNetIn : topNetOut).Add(net);
            }
            if (!double.IsNaN(turnover[p])) tos.Add(turnover[p]);
            for (int g = 0; g < nG; g++)
                if (!double.IsNaN(decileRet[p][g])) decileFull[g].Add(decileRet[p][g]);
        }

        return new FactorResult
        {
            Factor = factor,
            IcSeries = icSeries,
            IcNeuSeries = icNeuSeries,
            IcIn = IcStats.From(icIn),
            IcOut = IcStats.From(icOut),
            IcNeuIn = IcStats.From(icNeuIn),
            IcNeuOut = IcStats.From(icNeuOut),
            YearlyIc = new SortedDictionary<int, double>(yearly.ToDictionary(kv => kv.Key, kv => Stats.Mean(kv.Value))),
            DecileRet = decileRet,
            TopTurnover = turnover,
            LsAnnIn = Stats.Annualize(lsIn, Config.HoldDays),
            LsAnnOut = Stats.Annualize(lsOut, Config.HoldDays),
            TopNetAnnIn = Stats.Annualize(topNetIn, Config.HoldDays),
            TopNetAnnOut = Stats.Annualize(topNetOut, Config.HoldDays),
            AvgTurnover = Stats.Mean(tos),
            DecileAnnFull = decileFull.Select(l => Stats.Annualize(l, Config.HoldDays)).ToArray(),
            Snapshot = snapshot,
            LatestValues = Enumerable.Range(0, nS).Select(s => matrix[s][md.NDays - 1]).ToArray(),
        };
    }

    /// <summary>候选因子去重：按样本内|ICIR|降序贪心保留，与已保留因子截面相关|ρ|>0.8 的标记为重复。
    /// 对照/合成因子不参与。返回：因子下标 → null(保留) 或 被哪个因子覆盖。</summary>
    public static Dictionary<int, string?> Deduplicate(IReadOnlyList<FactorResult> results, double[,] corr)
    {
        const double Threshold = 0.8;
        var verdict = new Dictionary<int, string?>();
        var kept = new List<int>();
        var order = Enumerable.Range(0, results.Count)
            .Where(i => results[i].Factor.Role == FactorRole.Candidate)
            .OrderByDescending(i => double.IsNaN(results[i].IcIn.Icir) ? 0 : Math.Abs(results[i].IcIn.Icir));
        foreach (int i in order)
        {
            int dup = kept.FirstOrDefault(k => !double.IsNaN(corr[i, k]) && Math.Abs(corr[i, k]) > Threshold, -1);
            if (dup >= 0) verdict[i] = results[dup].Factor.Name;
            else { verdict[i] = null; kept.Add(i); }
        }
        return verdict;
    }

    /// <summary>因子相关性矩阵：各调仓日截面 Spearman 的时序平均（|ρ|>0.8 视为重复因子）。</summary>
    public static double[,] CorrelationMatrix(IReadOnlyList<FactorResult> results, SharedEval sh)
    {
        int n = results.Count, nP = sh.Periods.Count;
        var corr = new double[n, n];
        for (int i = 0; i < n; i++) corr[i, i] = 1;

        var pairs = new List<(int, int)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++) pairs.Add((i, j));

        Parallel.ForEach(pairs, pair =>
        {
            var (i, j) = pair;
            var vals = new List<double>(nP);
            for (int p = 0; p < nP; p++)
            {
                var a = results[i].Snapshot[p];
                var b = results[j].Snapshot[p];
                var xs = new List<double>(); var ys = new List<double>();
                for (int s = 0; s < a.Length; s++)
                {
                    if (!sh.Tradable[p][s] || double.IsNaN(a[s]) || double.IsNaN(b[s])) continue;
                    xs.Add(a[s]); ys.Add(b[s]);
                }
                if (xs.Count < Config.MinCrossSection) continue;
                double c = Stats.Spearman(xs.ToArray(), ys.ToArray());
                if (!double.IsNaN(c)) vals.Add(c);
            }
            double avg = vals.Count > 0 ? Stats.Mean(vals) : double.NaN;
            corr[i, j] = avg; corr[j, i] = avg;
        });
        return corr;
    }
}
