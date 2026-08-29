using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 从本地库的银行样本算出行业分位（2026-08-29 新增），供银行体检表当"参考值"用。
///
/// 纯计算、不碰数据库——取数在调用方（见 Analyzer 的 BankPeerStatsLoader）。
///
/// ════ 两个口径纪律 ════
/// ① **报告期必须对齐**：本地库各家银行的抓取进度不同（实测 42 家里 9 家已有中报、33 家只到
///    一季报），拿混合期算分位等于把半年报和一季报放在一起比。这里取样本里出现最多的那个
///    报告期，只统计对得上的银行；对不上的宁可不算。
/// ② **跟个股同口径**：ROE/ROA 的分母都用期初期末均值，跟
///    <see cref="BankHealthCheckBuilder"/> 里算个股时完全一致。否则个股值和分位不可比——
///    用期末净资产算的分位会系统性偏低，个股一比就显得"优于行业"，是假的。
/// </summary>
public static class BankPeerStatsBuilder
{
    /// <summary>样本少于这个数就不算分位，退回内置基准——十来家算出来的分位没有代表性。</summary>
    public const int MinSample = 10;

    /// <summary>
    /// 从（当期，上一期）样本对算分位。返回 null 表示样本不足，调用方应退回
    /// <see cref="BankPeerStats.Builtin"/>。
    /// </summary>
    public static BankPeerStats? Build(IEnumerable<(FinancialSnapshot Cur, FinancialSnapshot? Prev)> samples)
    {
        var all = samples.Where(s => s.Cur != null).ToList();
        if (all.Count == 0) return null;

        // ① 报告期对齐：取出现最多的那一期。
        var asOf = all.GroupBy(s => s.Cur.ReportDate)
                      .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
                      .First().Key;
        var aligned = all.Where(s => s.Cur.ReportDate == asOf).ToList();
        if (aligned.Count < MinSample) return null;

        var roe = new List<double>();
        var roa = new List<double>();
        var nim = new List<double>();
        var cir = new List<double>();

        foreach (var (cur, prev) in aligned)
        {
            double? eq = cur.Get(FinancialKeys.EquityParent);
            double? eqBeg = prev?.Get(FinancialKeys.EquityParent);
            double? npp = cur.Get(FinancialKeys.NetProfitParent);
            if (npp.HasValue && eq is > 0)
            {
                // ② 与个股同口径：期初期末均值。
                double avgEq = eqBeg is > 0 ? (eq.Value + eqBeg.Value) / 2 : eq.Value;
                roe.Add(cur.AnnualizeCumulative(npp.Value) / avgEq * 100);
            }

            double? assets = cur.Get(FinancialKeys.TotalAssets);
            double? aBeg = prev?.Get(FinancialKeys.TotalAssets);
            double? ni = cur.Get(FinancialKeys.NetProfit);
            if (ni.HasValue && assets is > 0)
            {
                double avgA = aBeg is > 0 ? (assets.Value + aBeg.Value) / 2 : assets.Value;
                roa.Add(cur.AnnualizeCumulative(ni.Value) / avgA * 100);

                // 净息差用"利息净收入÷平均总资产"的近似，跟个股那边同一个近似口径，
                // 所以能直接比；但不能拿去跟年报披露的净利息收益率对齐（分母口径不同）。
                if (cur.Get(FinancialKeys.InterestNet) is > 0 and var ii)
                    nim.Add(cur.AnnualizeCumulative(ii) / avgA * 100);
            }

            double? adm = cur.Get(FinancialKeys.AdminExpense), rev = cur.Get(FinancialKeys.Revenue);
            if (adm is > 0 && rev is > 0) cir.Add(adm.Value / rev.Value * 100);
        }

        if (roe.Count < MinSample) return null;

        return new BankPeerStats
        {
            AsOf = asOf,
            IsLive = true,
            SampleSize = aligned.Count,
            RoeMedian = Percentile(roe, 0.50),
            RoeP75 = Percentile(roe, 0.75),
            RoaMedian = Percentile(roa, 0.50),
            RoaP75 = Percentile(roa, 0.75),
            NimAvg = nim.Count > 0 ? Percentile(nim, 0.50) : BankPeerStats.Builtin.NimAvg,
            CostIncomeMedian = cir.Count > 0 ? Percentile(cir, 0.50) : BankPeerStats.Builtin.CostIncomeMedian,
            // 这三项本地库算不出（在财报 PDF 附注里），沿用内置快照只作参考展示。
            NplRatioAvg = BankPeerStats.Builtin.NplRatioAvg,
            ProvisionCoverageAvg = BankPeerStats.Builtin.ProvisionCoverageAvg,
            CoreTier1Avg = BankPeerStats.Builtin.CoreTier1Avg,
        };
    }

    /// <summary>线性插值分位数。样本量只有几十，不必用更复杂的估计量。</summary>
    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.OrderBy(v => v).ToList();
        if (s.Count == 1) return s[0];
        double pos = (s.Count - 1) * p;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return lo == hi ? s[lo] : s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }
}
