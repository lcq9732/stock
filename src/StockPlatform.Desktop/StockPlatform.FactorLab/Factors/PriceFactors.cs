using StockPlatform.FactorLab.Core;

namespace StockPlatform.FactorLab.Factors;

/// <summary>动量：过去 N 日涨幅（可剔除最近 skip 日以避开短期反转的污染）。</summary>
public sealed class Momentum(int lookback, int skip = 0) : IFactor
{
    public string Name => skip > 0 ? $"动量{lookback}日(剔近{skip}日)" : $"动量{lookback}日";
    public string Category => "趋势";
    public string Formula => skip > 0
        ? $"close[t-{skip}] / close[t-{lookback}] - 1"
        : $"close[t] / close[t-{lookback}] - 1";
    public string Description =>
        "捕捉价格惯性：过去一段时间强的股票倾向于继续强（行为解释：投资者对信息反应不足、追涨的羊群效应）。" +
        (skip > 0 ? "剔除最近几日是为了避开A股很强的短期反转效应对中期动量的污染。" : "") +
        "A股的中期动量历来弱于美股、且时有失效，属于'预期弱有效'的经典因子。";
    public string Direction => "值越大=过去涨幅越大（动量越强）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, lookback + 1, (s, t) =>
        {
            double now = md.Close[s][t - skip], past = md.Close[s][t - lookback];
            return double.IsNaN(now) || double.IsNaN(past) || past <= 0 ? double.NaN : now / past - 1;
        });
}

/// <summary>短期反转：过去N日涨幅取负。5日版是A股经典强效应，作为框架的阳性对照。</summary>
public sealed class Reversal(int lookback) : IFactor
{
    public string Name => $"反转{lookback}日";
    public string Category => "反转";
    public string Formula => $"-(close[t] / close[t-{lookback}] - 1)";
    public string Description =>
        "捕捉短期超跌反弹/超涨回落：最近跌得多的股票短期倾向于反弹（流动性冲击后的价格恢复，散户追涨杀跌的对手盘）。" +
        (lookback == 5
            ? "短期反转是A股历史上最稳定的截面效应之一，故5日版作为阳性对照——若框架跑不出它的信号，说明框架实现有bug。注意：换手极高，扣费后通常所剩无几，实盘价值有限。"
            : $"{lookback}日版是反转效应的中等窗口变体，与5日版高相关，用于观察反转的衰减速度。");
    public string Direction => $"值越大=最近{lookback}日跌得越多（反弹预期越强）";
    public FactorRole Role => lookback == 5 ? FactorRole.PositiveControl : FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, lookback + 1, (s, t) =>
        {
            double now = md.Close[s][t], past = md.Close[s][t - lookback];
            return double.IsNaN(now) || double.IsNaN(past) || past <= 0 ? double.NaN : -(now / past - 1);
        });
}

/// <summary>低波动：过去20日日收益率标准差取负。</summary>
public sealed class LowVolatility20 : IFactor
{
    public string Name => "低波动20日";
    public string Category => "波动";
    public string Formula => "-std(日收益率, 20日)";
    public string Description =>
        "低波动异象：波动小的股票长期风险调整后收益更高（博彩偏好解释：投资者高估高波动'彩票股'，导致其被系统性高估）。" +
        "A股散户占比高，彩票股偏好强，低波异象有土壤；但在快速拉升的牛市阶段会明显跑输。";
    public string Direction => "值越大=波动越低";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double v = Rolling.WindowRetStd(md.Close[s], t, 20, 15);
            return double.IsNaN(v) ? double.NaN : -v;
        });
}

/// <summary>低振幅：过去20日日内振幅均值取负。低波动的日内版本。</summary>
public sealed class LowAmplitude20 : IFactor
{
    public string Name => "低振幅20日";
    public string Category => "波动";
    public string Formula => "-mean((high-low)/前收, 20日)";
    public string Description =>
        "与低波动同源但用日内高低价：振幅大通常意味着筹码分歧大、投机资金活跃，此类股票后续平均表现差。" +
        "与低波动20日高度相关，二选一即可——放在一起正是为了让相关性矩阵演示因子去重。";
    public string Direction => "值越大=日内振幅越小";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md)
    {
        return Rolling.Apply(md, 21, (s, t) =>
        {
            double sum = 0; int n = 0;
            for (int i = t - 19; i <= t; i++)
            {
                double h = md.High[s][i], l = md.Low[s][i], pc = md.Close[s][i - 1];
                if (double.IsNaN(h) || double.IsNaN(l) || double.IsNaN(pc) || pc <= 0) continue;
                sum += (h - l) / pc; n++;
            }
            return n >= 15 ? -(sum / n) : double.NaN;
        });
    }
}
