using StockPlatform.FactorLab.Core;

namespace StockPlatform.FactorLab.Factors;

// ============ M2 扩充因子：全部只用日线 OHLC/成交额/换手 + 龙虎榜，取向为文献先验，不根据回测调整 ============

/// <summary>距60日新高距离（52周高点动量的短版）。</summary>
public sealed class NewHighDistance60 : IFactor
{
    public string Name => "近60日新高接近度";
    public string Category => "趋势";
    public string Formula => "close[t] / max(close, 60日) - 1（≤0，越接近0越接近新高）";
    public string Description =>
        "George-Hwang 52周高点动量的思想：接近历史高点的股票因'锚定效应'被低估（投资者不敢追新高），突破后继续上行。" +
        "先验取向为接近新高更好，但A股整体呈输家反转格局，此先验偏弱——本因子的意义在于检验'高点锚定'与普通动量在A股是否有区别。";
    public string Direction => "值越大=越接近60日新高";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 61, (s, t) =>
        {
            double c = md.Close[s][t];
            if (double.IsNaN(c)) return double.NaN;
            double max = double.NaN;
            for (int i = t - 59; i <= t; i++)
            {
                double v = md.Close[s][i];
                if (!double.IsNaN(v) && (double.IsNaN(max) || v > max)) max = v;
            }
            return double.IsNaN(max) || max <= 0 ? double.NaN : c / max - 1;
        });
}

/// <summary>均线偏离取负（均值回归）。</summary>
public sealed class MaDeviation(int window) : IFactor
{
    public string Name => $"均线回归{window}日";
    public string Category => "反转";
    public string Formula => $"-(close[t] / MA{window} - 1)";
    public string Description =>
        $"价格显著偏离{window}日均线后倾向回归：远高于均线=短期过热，远低于均线=超跌。与固定窗口反转同族，" +
        "但用均线作参照更平滑、对单日异动不敏感。你在指数上验证过 MA60 择时有效，这里检验同一思想在个股截面上的版本。";
    public string Direction => "值越大=低于均线越多（越超跌）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, window + 1, (s, t) =>
        {
            double c = md.Close[s][t];
            double ma = Rolling.WindowMean(md.Close[s], t, window, window * 3 / 4);
            return double.IsNaN(c) || double.IsNaN(ma) || ma <= 0 ? double.NaN : -(c / ma - 1);
        });
}

/// <summary>低彩票性：过去20日最大单日涨幅取负（Bali 的 MAX 因子）。</summary>
public sealed class LowMax20 : IFactor
{
    public string Name => "低彩票性20日";
    public string Category => "波动";
    public string Formula => "-max(日收益率, 20日)";
    public string Description =>
        "MAX因子（Bali et al.）：近期出现过暴涨的'彩票股'吸引博彩型资金追入，被系统性高估，后续平均收益差。" +
        "A股散户博彩偏好强，该效应有土壤。与低波动相关但捕捉的是尾部而非整体波动。";
    public string Direction => "值越大=近期没有暴涨日（彩票性越低）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double max = double.NaN;
            double prev = double.NaN;
            for (int i = t - 20; i <= t; i++)
            {
                double c = md.Close[s][i];
                if (double.IsNaN(c) || c <= 0) continue;
                if (!double.IsNaN(prev))
                {
                    double r = c / prev - 1;
                    if (double.IsNaN(max) || r > max) max = r;
                }
                prev = c;
            }
            return double.IsNaN(max) ? double.NaN : -max;
        });
}

/// <summary>隔夜反转：过去20日隔夜收益（开盘/前收-1）累计取负。</summary>
public sealed class OvernightReversal20 : IFactor
{
    public string Name => "隔夜反转20日";
    public string Category => "量价";
    public string Formula => "-sum(open[i]/close[i-1] - 1, 20日)";
    public string Description =>
        "把日收益拆成隔夜(前收→开盘)与日内(开盘→收盘)两段：A股文献一致发现隔夜收益高的股票（频繁高开，多为情绪/消息驱动）" +
        "后续表现差，即'隔夜收益负溢价'。与整体反转不同，它只惩罚高开部分，信息更纯。";
    public string Direction => "值越大=近期累计高开越少";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double sum = 0; int n = 0;
            for (int i = t - 19; i <= t; i++)
            {
                double o = md.Open[s][i], pc = md.Close[s][i - 1];
                if (double.IsNaN(o) || double.IsNaN(pc) || pc <= 0) continue;
                sum += o / pc - 1; n++;
            }
            return n >= 15 ? -sum : double.NaN;
        });
}

/// <summary>日内动量：过去20日日内收益（收盘/开盘-1）累计。隔夜反转的镜像。</summary>
public sealed class IntradayMomentum20 : IFactor
{
    public string Name => "日内动量20日";
    public string Category => "量价";
    public string Formula => "sum(close[i]/open[i] - 1, 20日)";
    public string Description =>
        "隔夜反转的镜像：日内(开盘→收盘)走强的股票代表真实买盘（开盘定价后仍被持续买入），而非情绪化高开。" +
        "文献上日内收益对未来收益的预测方向与隔夜相反。与隔夜反转高相关属预期之中，二者信息有重叠。";
    public string Direction => "值越大=近期日内走势越强";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double sum = 0; int n = 0;
            for (int i = t - 19; i <= t; i++)
            {
                double o = md.Open[s][i], c = md.Close[s][i];
                if (double.IsNaN(o) || double.IsNaN(c) || o <= 0) continue;
                sum += c / o - 1; n++;
            }
            return n >= 15 ? sum : double.NaN;
        });
}

/// <summary>低上影线：过去20日上影线占比均值取负。</summary>
public sealed class LowUpperShadow20 : IFactor
{
    public string Name => "低上影线20日";
    public string Category => "量价";
    public string Formula => "-mean((high - max(open,close)) / 前收, 20日)";
    public string Description =>
        "上影线=盘中冲高被砸回，代表上方抛压重、拉高出货或跟风盘不济。近期上影线累积多的股票平均后续弱。" +
        "K线形态类因子的代表，检验'砸盘痕迹'是否有截面预测力。";
    public string Direction => "值越大=近期上影线越少（抛压越轻）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double sum = 0; int n = 0;
            for (int i = t - 19; i <= t; i++)
            {
                double h = md.High[s][i], o = md.Open[s][i], c = md.Close[s][i], pc = md.Close[s][i - 1];
                if (double.IsNaN(h) || double.IsNaN(o) || double.IsNaN(c) || double.IsNaN(pc) || pc <= 0) continue;
                sum += (h - Math.Max(o, c)) / pc; n++;
            }
            return n >= 15 ? -(sum / n) : double.NaN;
        });
}

/// <summary>换手稳定：过去20日换手率标准差取负。</summary>
public sealed class StableTurnover20 : IFactor
{
    public string Name => "换手稳定20日";
    public string Category => "量价";
    public string Formula => "-std(换手率, 20日)";
    public string Description =>
        "换手率忽高忽低=关注度脉冲式波动，多为题材炒作特征；换手平稳=持仓结构稳定。" +
        "与低换手水平因子相关但捕捉的是二阶信息（波动而非水平）。";
    public string Direction => "值越大=换手越平稳";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            var xs = new List<double>(20);
            for (int i = t - 19; i <= t; i++)
                if (!double.IsNaN(md.Turnover[s][i])) xs.Add(md.Turnover[s][i]);
            return xs.Count >= 15 ? -Stats.Std(xs) : double.NaN;
        });
}

/// <summary>Amihud 非流动性：|日收益| / 成交额 的20日均值（放大1e9倍便于阅读）。</summary>
public sealed class Amihud20 : IFactor
{
    public string Name => "非流动性20日";
    public string Category => "量价";
    public string Formula => "mean(|日收益| / 成交额, 20日) × 1e9";
    public string Description =>
        "Amihud 非流动性：单位成交额能推动的价格变化。越大=越缺流动性=持有人要求流动性补偿溢价。" +
        "经典因子，但与小市值高度相关（小盘股天然缺流动性），中性化后的IC才反映其独立信息。";
    public string Direction => "值越大=流动性越差（溢价预期越高）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double sum = 0; int n = 0;
            double prev = double.NaN;
            for (int i = t - 20; i <= t; i++)
            {
                double c = md.Close[s][i], a = md.Amount[s][i];
                if (double.IsNaN(c) || c <= 0) { continue; }
                if (!double.IsNaN(prev) && !double.IsNaN(a) && a > 0)
                {
                    sum += Math.Abs(c / prev - 1) / a * 1e9; n++;
                }
                prev = c;
            }
            return n >= 15 ? sum / n : double.NaN;
        });
}

/// <summary>振幅收缩比：近5日振幅均值 / 近60日振幅均值，取负。</summary>
public sealed class AmplitudeShrink : IFactor
{
    public string Name => "振幅收缩(5/60日)";
    public string Category => "波动";
    public string Formula => "-(mean(振幅,5日) / mean(振幅,60日))";
    public string Description =>
        "波动状态因子：近期振幅相对自身长期水平收缩，代表分歧收敛、筹码沉淀（常见于横盘蓄势末端）；" +
        "振幅放大多伴随情绪高潮。与缩量比是量、价两个维度的同一思想。";
    public string Direction => "值越大=振幅相对自身越收缩";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md)
    {
        // 先算逐日振幅，再做两窗口均值比
        var amp = Rolling.AllocNaN(md.NStocks, md.NDays);
        Parallel.For(0, md.NStocks, s =>
        {
            for (int t = 1; t < md.NDays; t++)
            {
                double h = md.High[s][t], l = md.Low[s][t], pc = md.Close[s][t - 1];
                if (!double.IsNaN(h) && !double.IsNaN(l) && !double.IsNaN(pc) && pc > 0)
                    amp[s][t] = (h - l) / pc;
            }
        });
        return Rolling.Apply(md, 61, (s, t) =>
        {
            double shortM = Rolling.WindowMean(amp[s], t, 5, 4);
            double longM = Rolling.WindowMean(amp[s], t, 60, 45);
            return double.IsNaN(shortM) || double.IsNaN(longM) || longM <= 0 ? double.NaN : -(shortM / longM);
        });
    }
}

/// <summary>龙虎榜冷落：近20日上榜次数取负。</summary>
public sealed class LhbCold20 : IFactor
{
    public string Name => "龙虎榜冷落20日";
    public string Category => "资金";
    public string Formula => "-count(近20日龙虎榜上榜次数)";
    public string Description =>
        "上龙虎榜=异动被交易所点名=游资炒作高潮的标志，此类股票短期后续平均回落（关注度峰值即价格峰值）。" +
        "取负后'没上过榜的冷落股'因子值最高。注意：约96%的股票任意时点都是0次，因子值高度离散，IC解读要看分组收益。";
    public string Direction => "值越大=近期越没上过龙虎榜";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            int cnt = 0;
            for (int i = t - 19; i <= t; i++)
                if (md.LhbFlag[s][i]) cnt++;
            // 当日无行情数据的股票不给值，避免停牌期间被当作"冷落"
            return double.IsNaN(md.Close[s][t]) ? double.NaN : -cnt;
        });
}
