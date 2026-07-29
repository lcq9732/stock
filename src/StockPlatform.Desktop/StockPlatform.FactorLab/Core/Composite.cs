namespace StockPlatform.FactorLab.Core;

/// <summary>
/// 多因子合成。纪律：入选（去重保留 且 样本内|ICIR|≥门槛）、方向（样本内IC符号）、权重（等权或样本内|ICIR|）
/// 全部只由样本内数据决定，样本外仅验证。每期截面把各成分因子转成秩分（-1~1），加权平均。
/// 矩阵只在调仓日和最后交易日有值（合成因子只在这些时点被使用）。
/// </summary>
public sealed class CompositeFactor : IFactor
{
    readonly double[][] _matrix;
    public string Name { get; }
    public string Category => "合成";
    public string Formula { get; }
    public string Description { get; }
    public string Direction => "值越大=综合信号越强（预期越好）";
    public FactorRole Role => FactorRole.Composite;
    /// <summary>成分：(因子, 方向符号, 归一化权重)。</summary>
    public IReadOnlyList<(IFactor Factor, int Sign, double Weight)> Components { get; }

    CompositeFactor(string name, string formula, string desc, double[][] matrix,
        List<(IFactor, int, double)> components)
    {
        Name = name; Formula = formula; Description = desc; _matrix = matrix; Components = components;
    }

    public double[][] Compute(MarketData md) => _matrix;

    /// <summary>从已评估的单因子结果构建合成因子。icirWeighted=false 为等权。</summary>
    public static CompositeFactor Build(
        string name, bool icirWeighted,
        IReadOnlyList<FactorResult> results, Dictionary<int, string?> dedup,
        MarketData md, SharedEval sh)
    {
        // 入选：去重保留的候选 且 样本内|ICIR|≥门槛
        var picked = new List<(FactorResult R, int Sign, double Weight)>();
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.Factor.Role != FactorRole.Candidate) continue;
            if (dedup.GetValueOrDefault(i, "dup") is not null) continue; // 只要"保留"的
            if (double.IsNaN(r.IcIn.Icir) || Math.Abs(r.IcIn.Icir) < Config.CompositeIcirMin) continue;
            picked.Add((r, Math.Sign(r.IcIn.Mean), icirWeighted ? Math.Abs(r.IcIn.Icir) : 1.0));
        }
        if (picked.Count == 0) throw new InvalidOperationException("没有因子满足合成入选条件");
        double wSum = picked.Sum(x => x.Weight);
        var comps = picked.Select(x => ((IFactor)x.R.Factor, x.Sign, x.Weight / wSum)).ToList();

        int nS = md.NStocks, nD = md.NDays;
        var matrix = Rolling.AllocNaN(nS, nD);

        // 调仓日：用各因子的 Snapshot；最后交易日：用 LatestValues（供"今日名单"）
        for (int p = 0; p < sh.Periods.Count; p++)
            FillDay(matrix, sh.Periods[p].T, picked, p, nS);
        FillLatest(matrix, nD - 1, picked, nS);

        string formula = $"Σ 方向×权重×秩分(-1~1)，成分{picked.Count}个：" + string.Join("、",
            picked.Select(x => $"{x.R.Factor.Name}({(x.Sign > 0 ? "+" : "-")}{x.Weight / wSum:0.00})"));
        string desc =
            $"{(icirWeighted ? "ICIR加权" : "等权")}合成：去重保留且样本内|ICIR|≥{Config.CompositeIcirMin}的候选因子，" +
            "方向按样本内IC符号锁定（含把先验取向跑反的因子翻转使用，这属于样本内决策，样本外不再改动）。" +
            "每期把每个成分因子转为截面秩分(-1~1)后加权平均，要求至少60%成分有值。合成的意义：单因子信号弱且不稳，" +
            "低相关因子平均后信噪比提升——这是多因子框架的核心假设，其成立与否看本因子样本外表现。";
        return new CompositeFactor(name, formula, desc, matrix, comps);
    }

    static void FillDay(double[][] matrix, int t, List<(FactorResult R, int Sign, double Weight)> picked, int period, int nS)
    {
        var scores = new double[nS];
        var weights = new double[nS];
        foreach (var (r, sign, w) in picked)
        {
            var vals = period >= 0 ? r.Snapshot[period] : r.LatestValues;
            AddRankScores(scores, weights, vals, sign, w, nS);
        }
        double wTotal = picked.Sum(x => x.Weight);
        for (int s = 0; s < nS; s++)
            if (weights[s] >= wTotal * 0.6) matrix[s][t] = scores[s] / weights[s];
    }

    static void FillLatest(double[][] matrix, int t, List<(FactorResult R, int Sign, double Weight)> picked, int nS)
        => FillDay(matrix, t, picked, -1, nS);

    /// <summary>把一个因子的截面值转为秩分(-1~1)，按方向和权重累加到 scores。</summary>
    static void AddRankScores(double[] scores, double[] weights, double[] vals, int sign, double w, int nS)
    {
        var idx = new List<int>(nS);
        for (int s = 0; s < nS; s++)
            if (!double.IsNaN(vals[s])) idx.Add(s);
        if (idx.Count < 2) return;
        var ranks = Stats.Ranks(idx.Select(s => vals[s]).ToArray());
        for (int i = 0; i < idx.Count; i++)
        {
            double score = 2.0 * ranks[i] / (idx.Count + 1) - 1; // -1 ~ 1
            scores[idx[i]] += sign * w * score;
            weights[idx[i]] += w;
        }
    }
}
