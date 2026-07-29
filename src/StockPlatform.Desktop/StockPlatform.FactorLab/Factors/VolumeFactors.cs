using StockPlatform.FactorLab.Core;

namespace StockPlatform.FactorLab.Factors;

/// <summary>低换手：过去20日换手率均值取负。</summary>
public sealed class LowTurnover20 : IFactor
{
    public string Name => "低换手20日";
    public string Category => "量价";
    public string Formula => "-mean(换手率, 20日)";
    public string Description =>
        "换手率是A股最强的截面变量族之一：高换手=投机关注度高、筹码不稳，此类股票平均后续收益差；" +
        "低换手=被冷落、锁仓充分，平均后续收益好。本质接近'流动性溢价+博彩偏好'的混合。" +
        "注意它与小市值、低波动都有相关性，中性化之前的原始信号里混着规模效应。";
    public string Direction => "值越大=换手越低（越被冷落）";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, 21, (s, t) =>
        {
            double v = Rolling.WindowMean(md.Turnover[s], t, 20, 15);
            return double.IsNaN(v) ? double.NaN : -v;
        });
}

/// <summary>缩量比：近期均换手 / 长期均换手，取负（缩量为好）。</summary>
public sealed class VolumeShrink(int shortWin = 5, int longWin = 60) : IFactor
{
    public string Name => $"缩量比({shortWin}/{longWin}日)";
    public string Category => "量价";
    public string Formula => $"-(mean(换手,{shortWin}日) / mean(换手,{longWin}日))";
    public string Description =>
        "捕捉短期关注度退潮：近期换手相对自身长期水平显著缩量，说明炒作资金撤离完毕、抛压衰竭，" +
        "常见于回调后的底部区域；反之近期放量（比值大）多为情绪过热。与'低换手'的区别是做了自身历史标准化，" +
        "更接近'状态'而非'属性'，与规模的相关性更低。";
    public string Direction => "值越大=相对自身越缩量";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, longWin + 1, (s, t) =>
        {
            double shortM = Rolling.WindowMean(md.Turnover[s], t, shortWin, Math.Max(2, shortWin * 3 / 4));
            double longM = Rolling.WindowMean(md.Turnover[s], t, longWin, longWin * 3 / 4);
            return double.IsNaN(shortM) || double.IsNaN(longM) || longM <= 0 ? double.NaN : -(shortM / longM);
        });
}

/// <summary>量价背离：过去N日收盘价与换手率的秩相关取负。</summary>
public sealed class PriceVolumeDiverge(int window = 20) : IFactor
{
    public string Name => $"量价背离{window}日";
    public string Category => "量价";
    public string Formula => $"-SpearmanCorr(close, 换手率, {window}日)";
    public string Description =>
        "价量同涨（相关为正）说明上涨靠情绪放量推动，往往不可持续；价涨量缩/价跌量增（相关为负）的'背离'状态" +
        "反而平均后续更好。这是 Alpha191 中反复出现的构造母题（如 alpha#25 一族），取其思想做的日线简化版。";
    public string Direction => "值越大=量价背离越明显";
    public FactorRole Role => FactorRole.Candidate;

    public double[][] Compute(MarketData md) =>
        Rolling.Apply(md, window + 1, (s, t) =>
        {
            double v = Rolling.WindowSpearman(md.Close[s], md.Turnover[s], t, window, window * 3 / 4);
            return double.IsNaN(v) ? double.NaN : -v;
        });
}
