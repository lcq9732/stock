using StockPlatform.FactorLab.Core;

namespace StockPlatform.FactorLab.Factors;

/// <summary>小市值：近似流通市值取负对数。</summary>
public sealed class SmallSize : IFactor
{
    public string Name => "小市值";
    public string Category => "规模";
    public string Formula => "-ln(最新流通股本 × close[t])";
    public string Description =>
        "A股历史上最著名的因子：小盘股长期跑赢大盘股（壳价值、炒作弹性、机构覆盖不足）。但该因子风格性极强——" +
        "2017年、2024年初等阶段发生过深度回撤，且本框架数据不含退市股，幸存者偏差会高估它。" +
        "历史市值用'最新流通股本×前复权价'近似（忽略期间股本变动），排序用途下可接受。";
    public string Direction => "值越大=流通市值越小";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 1, (s, t) =>
        {
            double sh = md.FloatShares[s], c = md.Close[s][t];
            return double.IsNaN(sh) || double.IsNaN(c) || c <= 0 ? double.NaN : -Math.Log(sh * c);
        });
}

/// <summary>融资余额20日变化率。此前已验证截面基本无效，作为阴性对照。</summary>
public sealed class MarginChange20 : IFactor
{
    public string Name => "融资余额20日变化";
    public string Category => "资金";
    public string Formula => "融资余额[t] / 融资余额[t-20] - 1";
    public string Description =>
        "假设：融资盘（杠杆资金）持续流入代表激进资金看多，短期有跟随价值。" +
        "此前的独立检验结论是该截面因子基本无效（见 doc 备忘），故在本框架中作为阴性对照——" +
        "若跑出显著正IC，优先怀疑框架存在前视偏差或实现错误，而不是相信该因子复活了。";
    public string Direction => "值越大=融资盘流入越多";
    public FactorRole Role => FactorRole.NegativeControl;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double now = md.MarginBalance[s][t], past = md.MarginBalance[s][t - 20];
            return double.IsNaN(now) || double.IsNaN(past) || past <= 0 ? double.NaN : now / past - 1;
        });
}

/// <summary>股东户数环比下降。此前已验证截面基本无效，作为阴性对照。</summary>
public sealed class HolderShrink : IFactor
{
    public string Name => "股东户数环比降";
    public string Category => "筹码";
    public string Formula => "-(最新户数 / 上期户数 - 1)，报告基准日后滞后10个交易日生效";
    public string Description =>
        "假设：股东户数下降=筹码向少数人集中=有主力吸筹（彬哥法第11条的来源）。" +
        "此前的独立检验结论是该截面因子基本无效，故作为阴性对照。库中只有报告基准日没有公告日，" +
        "统一按滞后10个交易日估计可用时点——若跑出显著信号，先怀疑披露滞后假设导致的前视。";
    public string Direction => "值越大=户数降幅越大（筹码越集中）";
    public FactorRole Role => FactorRole.NegativeControl;

    public double[][] Compute(MarketData md)
    {
        var res = Core.Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            var seq = md.HolderChg[s];
            if (seq.Length == 0) return;
            int p = 0;
            double cur = double.NaN;
            for (int t = 0; t < md.NDays; t++)
            {
                while (p < seq.Length && seq[p].AvailIdx <= t) { cur = seq[p].Value; p++; }
                res[s][t] = cur;
            }
        });
        return res;
    }
}
