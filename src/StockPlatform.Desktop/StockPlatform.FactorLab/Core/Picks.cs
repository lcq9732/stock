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
            });
        }
        return rows;
    }
}
