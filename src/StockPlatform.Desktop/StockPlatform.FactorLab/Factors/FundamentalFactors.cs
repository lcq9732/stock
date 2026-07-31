using StockPlatform.FactorLab.Core;

namespace StockPlatform.FactorLab.Factors;

// ============ M4 基本面因子（2026-07-31）：为什么必须有它们 ============
// 十年数据证明纯量价因子只会"排雷"（识别最差的10%），顶部没有区分度（D6~D10 挤在一起），
// 做不了正向选股。盈利质量/成长/估值是与量价不相关的信息源，是经典多因子体系里
// "顶部有区分度"的那一半。数据来自新浪三张报表（Fetcher"拉取财务报表"），
// 可用时点按法定披露截止日保守估计（宁可信号晚到、不可前视）。

/// <summary>
/// 基本面因子基类：因子值是季度报表的阶梯函数——每个报告期在其"可用日"（法定披露截止日后的
/// 首个交易日）生效，持续到下一期生效为止；超过 300 个交易日没有新报告（退市/长期停牌）自动失效，
/// 避免用陈年数据打分。子类只需实现"给定该股全部报表历史和当前期下标，算出因子值"。
/// </summary>
public abstract class FundamentalFactorBase : IFactor
{
    public abstract string Name { get; }
    public abstract string Category { get; }
    public abstract string Formula { get; }
    public abstract string Description { get; }
    public abstract string Direction { get; }
    public virtual FactorRole Role => FactorRole.Candidate;

    /// <summary>报表数据过期阈值（交易日）：最后一个报告期生效超过这么久还没有新报告就不再给值。
    /// 一个报告周期最长约130个交易日（年报到一季报间隔），300 已留足缓冲。</summary>
    private const int StaleAfterDays = 300;

    /// <summary>第 i 期的因子值（可访问全部历史做 TTM/同比），NaN=该期算不出。</summary>
    protected abstract double Value(FinQuarter[] h, int i);

    public double[][] Compute(MarketData md)
    {
        var res = Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            var h = md.Financials[s];
            if (h.Length == 0) return;
            var row = res[s];
            for (int i = 0; i < h.Length; i++)
            {
                double v = Value(h, i);
                if (double.IsNaN(v)) continue; // 该期算不出：上一期的值继续沿用（不覆盖）
                int from = h[i].AvailIdx;
                int to = i + 1 < h.Length ? Math.Min(h[i + 1].AvailIdx, from + StaleAfterDays) : Math.Min(md.NDays, from + StaleAfterDays);
                for (int t = from; t < to && t < md.NDays; t++) row[t] = v;
            }
        });
        return res;
    }

    // ---- 子类共用的报表换算工具 ----

    /// <summary>找某年某月的报告期记录，没有返回 null。历史最多几十期，线性扫足够快。</summary>
    protected static FinQuarter? Find(FinQuarter[] h, int year, int month)
    {
        foreach (var q in h)
            if (q.Year == year && q.Month == month) return q;
        return null;
    }

    /// <summary>累计口径转 TTM：年报直接用；其余 = 本期累计 + 上年年报 − 上年同期累计（缺一不可）。</summary>
    protected static double Ttm(FinQuarter[] h, int i, Func<FinQuarter, double> f)
    {
        var cur = h[i];
        double cum = f(cur);
        if (double.IsNaN(cum)) return double.NaN;
        if (cur.Month == 12) return cum;
        double prevFy = Find(h, cur.Year - 1, 12) is { } fy ? f(fy) : double.NaN;
        double prevSame = Find(h, cur.Year - 1, cur.Month) is { } ps ? f(ps) : double.NaN;
        if (double.IsNaN(prevFy) || double.IsNaN(prevSame)) return double.NaN;
        return cum + prevFy - prevSame;
    }

    /// <summary>归母净利润，缺失退回净利润（早年部分公司只披露合并净利润）。</summary>
    protected static double Profit(FinQuarter q) => !double.IsNaN(q.NpParent) ? q.NpParent : q.NetProfit;

    /// <summary>同比：本期累计 / 上年同期累计 − 1。上年同期缺失或 ≤0（亏损转盈利没有有意义的比率）为 NaN。</summary>
    protected static double YoY(FinQuarter[] h, int i, Func<FinQuarter, double> f)
    {
        double cur = f(h[i]);
        if (double.IsNaN(cur)) return double.NaN;
        double prev = Find(h, h[i].Year - 1, h[i].Month) is { } p ? f(p) : double.NaN;
        if (double.IsNaN(prev) || prev <= 0) return double.NaN;
        return cur / prev - 1;
    }
}

/// <summary>ROE(TTM)：滚动一年归母净利润 / 归母净资产。</summary>
public sealed class RoeTtm : FundamentalFactorBase
{
    public override string Name => "ROE(TTM)";
    public override string Category => "基本面";
    public override string Formula => "归母净利润TTM / 归母股东权益";
    public override string Description =>
        "盈利质量的核心指标：单位净资产的盈利能力。高ROE且可持续的公司是经典'质量溢价'的来源" +
        "（巴菲特式选股的量化版）。与量价因子几乎不相关，是这批基本面因子里最被寄予厚望的正向选股信号。";
    public override string Direction => "值越大=净资产盈利能力越强";

    protected override double Value(FinQuarter[] h, int i)
    {
        double ttm = Ttm(h, i, Profit);
        double eq = h[i].EquityParent;
        return double.IsNaN(ttm) || double.IsNaN(eq) || eq <= 0 ? double.NaN : ttm / eq;
    }
}

/// <summary>净利润同比增长（累计口径）。</summary>
public sealed class ProfitYoy : FundamentalFactorBase
{
    public override string Name => "净利同比增长";
    public override string Category => "基本面";
    public override string Formula => "本期累计归母净利润 / 上年同期 − 1（上年同期≤0为空）";
    public override string Description =>
        "成长因子：盈利加速的公司往往被市场追捧（PEAD盈余惯性）。注意A股的'高增长'常来自低基数或" +
        "一次性损益，故与ROE/现金流质量搭配使用；上年同期亏损的样本没有有意义的增长率，记为空。";
    public override string Direction => "值越大=盈利增长越快";

    protected override double Value(FinQuarter[] h, int i) => YoY(h, i, Profit);
}

/// <summary>营业收入同比增长（累计口径）。</summary>
public sealed class RevenueYoy : FundamentalFactorBase
{
    public override string Name => "营收同比增长";
    public override string Category => "基本面";
    public override string Formula => "本期累计营业(总)收入 / 上年同期 − 1";
    public override string Description =>
        "成长因子的营收版：比净利润更难被会计手段修饰（收入确认造假成本高于利润调节），" +
        "增长的'质量'更硬。与净利同比搭配能区分'真成长'和'挤利润'。";
    public override string Direction => "值越大=营收增长越快";

    protected override double Value(FinQuarter[] h, int i) => YoY(h, i, q => q.Revenue);
}

/// <summary>毛利率同比变化。</summary>
public sealed class GrossMarginDelta : FundamentalFactorBase
{
    public override string Name => "毛利率变化";
    public override string Category => "基本面";
    public override string Formula => "本期毛利率 − 上年同期毛利率；毛利率 = 1 − 营业成本/营业收入（累计口径）";
    public override string Description =>
        "竞争力边际变化：毛利率走阔=提价能力或成本优势在增强，是基本面改善的早期信号，" +
        "常先于净利润兑现。银行/券商没有'营业成本'科目，此因子为空。取同比差值而非水平值，" +
        "避免变成'行业属性'因子（白酒天然高毛利、重资产天然低毛利）。";
    public override string Direction => "值越大=毛利率同比改善越多";

    protected override double Value(FinQuarter[] h, int i)
    {
        static double Gm(FinQuarter q) =>
            double.IsNaN(q.Revenue) || q.Revenue <= 0 || double.IsNaN(q.OperCost) ? double.NaN : 1 - q.OperCost / q.Revenue;
        double cur = Gm(h[i]);
        double prev = Find(h, h[i].Year - 1, h[i].Month) is { } p ? Gm(p) : double.NaN;
        return double.IsNaN(cur) || double.IsNaN(prev) ? double.NaN : cur - prev;
    }
}

/// <summary>现金流质量：经营现金流与净利润的差距（应计项反向指标）。</summary>
public sealed class CashflowQuality : FundamentalFactorBase
{
    public override string Name => "现金流质量";
    public override string Category => "基本面";
    public override string Formula => "(经营现金流TTM − 净利润TTM) / 总资产";
    public override string Description =>
        "应计异象（accruals anomaly）：利润远超经营现金流的公司在'赚纸面利润'（激进确认收入、囤积存货），" +
        "后续业绩变脸和造假的概率显著更高；现金流跟得上甚至超过利润的公司盈利更扎实。" +
        "除以总资产做规模标准化。这是学术上最稳健的财务质量因子之一。";
    public override string Direction => "值越大=利润的现金含量越高";

    protected override double Value(FinQuarter[] h, int i)
    {
        double ocf = Ttm(h, i, q => q.Ocf);
        double np = Ttm(h, i, q => q.NetProfit);
        double assets = h[i].Assets;
        return double.IsNaN(ocf) || double.IsNaN(np) || double.IsNaN(assets) || assets <= 0
            ? double.NaN : (ocf - np) / assets;
    }
}

/// <summary>低杠杆：资产负债率取负。</summary>
public sealed class LowLeverage : FundamentalFactorBase
{
    public override string Name => "低杠杆";
    public override string Category => "基本面";
    public override string Formula => "-(负债合计 / 资产总计)";
    public override string Description =>
        "财务安全性：高杠杆公司在信用收缩期（2018年去杠杆）死得最快，退市股名单里高负债占比极高。" +
        "作为防御性因子与成长类互补。注意银行天然高杠杆（>90%），行业中性化后才公平，" +
        "原始IC里含较强行业属性。";
    public override string Direction => "值越大=负债率越低（越安全）";

    protected override double Value(FinQuarter[] h, int i)
    {
        double liab = h[i].Liab, assets = h[i].Assets;
        return double.IsNaN(liab) || double.IsNaN(assets) || assets <= 0 ? double.NaN : -(liab / assets);
    }
}

/// <summary>盈利收益率 EP：净利润TTM / 市值（PE 的倒数，估值因子）。市值随价格逐日变化，单独实现 Compute。</summary>
public sealed class EarningsYield : IFactor
{
    public string Name => "盈利收益率EP";
    public string Category => "基本面";
    public string Formula => "归母净利润TTM / 近似流通市值（=PE倒数；市值为近似值，见手册局限说明）";
    public string Description =>
        "价值因子：单位市值买到多少利润。用EP而不是PE是因为EP对亏损股连续（亏损=负EP排在最后），" +
        "不会像PE那样在零附近爆炸。A股价值因子长期弱于小盘/反转，但在2017、2022~2024价值回归期表现突出，" +
        "与'冷落蓄势'族相关性低，是分散风格的关键成分。";
    public string Direction => "值越大=越便宜（估值越低）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        FundamentalDaily.Compute(md, FundamentalDaily.TtmProfit, invert: false);
}

/// <summary>净资产收益率 BP：归母权益 / 市值（PB 的倒数，估值因子）。</summary>
public sealed class BookToPrice : IFactor
{
    public string Name => "净资产收益率BP";
    public string Category => "基本面";
    public string Formula => "归母股东权益 / 近似流通市值（=PB倒数）";
    public string Description =>
        "经典价值因子（Fama-French HML 的分子分母倒过来）：单位市值买到多少净资产。比EP更稳定" +
        "（净资产不像利润那样季度间大幅波动），但对轻资产/科技公司系统性低估其价值。" +
        "破净股（BP>1）多出现在银行地产，行业中性化后看更公平。";
    public string Direction => "值越大=越便宜（市净率越低）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        FundamentalDaily.Compute(md, (h, i) => h[i].EquityParent, invert: false);
}

/// <summary>估值类因子的逐日计算：报表值（阶梯函数）÷ 当日近似市值。</summary>
internal static class FundamentalDaily
{
    /// <summary>TTM 归母净利润（缺归母退净利润），给 EP 用——放这里避免基类方法在非继承类里不可用。</summary>
    public static double TtmProfit(FinQuarter[] h, int i)
    {
        static double P(FinQuarter q) => !double.IsNaN(q.NpParent) ? q.NpParent : q.NetProfit;
        var cur = h[i];
        double cum = P(cur);
        if (double.IsNaN(cum)) return double.NaN;
        if (cur.Month == 12) return cum;
        FinQuarter? fy = null, ps = null;
        foreach (var q in h)
        {
            if (q.Year == cur.Year - 1 && q.Month == 12) fy = q;
            if (q.Year == cur.Year - 1 && q.Month == cur.Month) ps = q;
        }
        if (fy is not { } f || ps is not { } p) return double.NaN;
        double a = P(f), b = P(p);
        return double.IsNaN(a) || double.IsNaN(b) ? double.NaN : cum + a - b;
    }

    private const int StaleAfterDays = 300;

    public static double[][] Compute(MarketData md, Func<FinQuarter[], int, double> numerator, bool invert)
    {
        var res = Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            var h = md.Financials[s];
            double shares = md.FloatShares[s];
            if (h.Length == 0 || double.IsNaN(shares) || shares <= 0) return;
            var row = res[s];
            for (int i = 0; i < h.Length; i++)
            {
                double num = numerator(h, i);
                if (double.IsNaN(num)) continue;
                int from = h[i].AvailIdx;
                int to = i + 1 < h.Length ? Math.Min(h[i + 1].AvailIdx, from + StaleAfterDays) : Math.Min(md.NDays, from + StaleAfterDays);
                for (int t = from; t < to && t < md.NDays; t++)
                {
                    double cap = shares * md.Close[s][t];
                    if (double.IsNaN(cap) || cap <= 0) continue;
                    row[t] = invert ? cap / num : num / cap;
                }
            }
        });
        return res;
    }
}
