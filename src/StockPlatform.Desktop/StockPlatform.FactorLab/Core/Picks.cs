namespace StockPlatform.FactorLab.Core;

/// <summary>最新名单里的一只股票（纯数据，控制台CSV和 Analyzer 界面共用）。</summary>
public sealed class PickRow
{
    public required int Rank { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    /// <summary>合成因子得分。</summary>
    public required double Score { get; init; }
    /// <summary>各成分因子的加权秩分（按合成同一算法），降序。</summary>
    public required List<(string Factor, double Score)> Contribs { get; init; }
    public required DateOnly DataDate { get; init; }
    public required double LatestClose { get; init; }
    /// <summary>证监会行业（大类优先、门类兜底）。库里没抓过行业分类时为空字符串。</summary>
    public required string Industry { get; init; }
    /// <summary>融资余额占流通市值比（0.08=8%）。非两融标的为 NaN。
    /// 2026-08-04 实测：这个比例越高，波动/最差单期/Beta 越大，暴跌时跑输指数越多——
    /// 它不预测收益（各档平均收益持平），但能用来过滤掉暴跌时容易多亏3~4个点的品种。</summary>
    public required double MarginRatio { get; init; }

    public string TopContribsText => string.Join("、", Contribs.Where(c => !double.IsNaN(c.Score)).Take(3).Select(c => c.Factor));
}

/// <summary>由合成因子生成最新交易日的 Top 名单。</summary>
public static class Picks
{
    public static List<PickRow> Build(MarketData md, CompositeFactor comp, FactorResult compResult,
        IReadOnlyList<FactorResult> results, int topN)
    {
        int lastT = md.NDays - 1, nS = md.NStocks;
        var byName = results.ToDictionary(r => r.Factor.Name);

        // 每个成分因子在最后交易日的加权秩分（与合成构造同一算法），用于展示贡献来源
        var compScores = new Dictionary<string, double[]>();
        foreach (var (f, sign, w) in comp.Components)
        {
            var vals = byName[f.Name].LatestValues;
            var idx = new List<int>(nS);
            for (int s = 0; s < nS; s++) if (!double.IsNaN(vals[s])) idx.Add(s);
            var scores = new double[nS];
            Array.Fill(scores, double.NaN);
            if (idx.Count >= 2)
            {
                var ranks = Stats.Ranks(idx.Select(s => vals[s]).ToArray());
                for (int i = 0; i < idx.Count; i++)
                    scores[idx[i]] = sign * w * (2.0 * ranks[i] / (idx.Count + 1) - 1);
            }
            compScores[f.Name] = scores;
        }

        var eligible = new List<int>(nS);
        for (int s = 0; s < nS; s++)
            if (Evaluator.IsEligible(md, s, lastT) && !double.IsNaN(compResult.LatestValues[s])) eligible.Add(s);

        var rows = new List<PickRow>();
        int rank = 0;
        foreach (int s in eligible.OrderByDescending(s => compResult.LatestValues[s]).Take(topN))
        {
            rows.Add(new PickRow
            {
                Rank = ++rank,
                Code = md.Codes[s],
                Name = md.Names[s],
                Score = compResult.LatestValues[s],
                Contribs = comp.Components
                    .Select(c => (c.Factor.Name, compScores[c.Factor.Name][s]))
                    .OrderByDescending(x => double.IsNaN(x.Item2) ? double.MinValue : x.Item2)
                    .ToList(),
                DataDate = md.Dates[lastT],
                // 展示真实成交价（前复权末日=真实价），不是回测用的后复权价——否则名单里会出现"茅台 7287 元"
                LatestClose = md.DisplayClose[s][lastT],
                Industry = md.IndustryName[s],
                MarginRatio = MarginRatioAt(md, s, lastT),
            });
        }
        return rows;
    }

    /// <summary>某日的融资余额占流通市值比。融资数据按交易日发布、偶有延迟，往前找最多5个交易日；
    /// 非两融标的返回 NaN。</summary>
    static double MarginRatioAt(MarketData md, int s, int t)
    {
        double sh = md.FloatShares[s], c = md.Close[s][t];
        if (double.IsNaN(sh) || double.IsNaN(c) || c <= 0) return double.NaN;
        double cap = sh * c;
        if (cap <= 0) return double.NaN;
        for (int i = t; i >= Math.Max(0, t - 5); i--)
        {
            double bal = md.MarginBalance[s][i];
            if (!double.IsNaN(bal) && bal > 0) return bal / cap;
        }
        return double.NaN;
    }
}
