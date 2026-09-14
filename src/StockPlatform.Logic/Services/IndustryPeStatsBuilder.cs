using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 从全市场样本算出每只票所属行业的 PE 分位（2026-09-14）。纯计算、不碰数据库——
/// 取数在调用方（Analyzer 的 <c>LoadIndustryPe</c>）。
///
/// ════ 两个口径纪律 ════
/// ① **PE 必须跟个股那一行同口径**：<c>收盘价 × 总股本 ÷ TTM归母净利</c>，用
///    <see cref="FinancialAnalyzer.TtmFromCumulative"/> 算 TTM、用
///    <c>MetricKeys.TotalShares</c> 当股数。两边口径不同的话，界面上的数和参考分位
///    就不是一把尺子，比了等于没比。
///    ⚠ 尤其**不能用财报 share_capital 当股数**——那是实收资本（金额），
///    面值不是 1 元的票会错到离谱（中芯国际差 35 倍），分位和落点全歪。
/// ② **只统计盈利股**：亏损股 PE 为负，混进来把分位算歪；而且那些票的 PE 行本来就不显示。
///
/// ════ 为什么一次把两级都算出来 ════
/// 回退需要"二级不够就看一级"，所以两级的分位都得在手上。而且成本一样——
/// 一次扫描把 31 + 127 个行业的分位一起算出来，跟只算一个全市场中位没有区别。
/// </summary>
public static class IndustryPeStatsBuilder
{
    /// <summary>
    /// 算出每只票适用的行业分位（**已经做好回退**：二级样本不够就换一级，再不够就没有）。
    /// 返回的字典里查不到某只票，意味着它没有行业归属、或所属行业样本太少——
    /// 调用方这时只显示全市场那半句。
    /// </summary>
    /// <param name="pes">全市场盈利股的 PE，口径见类注释。</param>
    /// <param name="industries">code → (一级行业名, 二级行业名)，任一级可为 null。</param>
    public static Dictionary<string, IndustryPeStats> Build(
        IReadOnlyDictionary<string, double> pes,
        IReadOnlyDictionary<string, (string? Level1, string? Level2)> industries)
    {
        // 先把样本按行业归堆（两级各一份）
        var buckets = new Dictionary<(int Level, string Name), List<double>>();
        foreach (var (code, pe) in pes)
        {
            if (pe <= 0) continue;                       // 纪律②：只统计盈利股
            if (!industries.TryGetValue(code, out var ind)) continue;
            if (ind.Level1 is { Length: > 0 } l1)
                Add(buckets, (1, l1), pe);
            if (ind.Level2 is { Length: > 0 } l2)
                Add(buckets, (2, l2), pe);
        }

        var stats = buckets
            .Where(b => b.Value.Count >= IndustryPeStats.MinSample)
            .ToDictionary(b => b.Key, b => Summarize(b.Key.Level, b.Key.Name, b.Value));

        // 再给每只票挑一级：二级够就用二级，否则退一级，都不够就不给
        var result = new Dictionary<string, IndustryPeStats>(StringComparer.Ordinal);
        foreach (var code in pes.Keys)
        {
            if (!industries.TryGetValue(code, out var ind)) continue;
            IndustryPeStats? pick = null;
            if (ind.Level2 is { Length: > 0 } l2 && stats.TryGetValue((2, l2), out var s2)) pick = s2;
            else if (ind.Level1 is { Length: > 0 } l1 && stats.TryGetValue((1, l1), out var s1)) pick = s1;
            if (pick != null) result[code] = pick;
        }
        return result;
    }

    private static void Add(Dictionary<(int, string), List<double>> buckets, (int, string) key, double pe)
    {
        if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<double>();
        list.Add(pe);
    }

    private static IndustryPeStats Summarize(int level, string name, List<double> values)
    {
        values.Sort();
        return new IndustryPeStats
        {
            Name = name,
            Level = level,
            SampleSize = values.Count,
            P10 = Percentile(values, 0.10),
            P25 = Percentile(values, 0.25),
            Median = Percentile(values, 0.50),
            P75 = Percentile(values, 0.75),
            P90 = Percentile(values, 0.90),
        };
    }

    /// <summary>
    /// 最近秩取值（不插值）。跟 <see cref="MarketPeStats"/> 那边"按档说、不插值"是一个态度：
    /// 小样本里插值出来的小数点纯属虚构。
    /// </summary>
    private static double Percentile(List<double> sorted, double p)
        => sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];
}
