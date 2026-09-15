namespace StockPlatform.Logic.Models;

/// <summary>
/// 【仓位计算器】里那几个"每次都一样"的参数（2026-08-17新增）——可投资总资金、凯利折扣、单票上限，
/// 还有默认的目标涨幅/止损幅度。跟 <see cref="TradeFeeSettings"/> 一样是账户级设置：填一次，
/// 以后每次打开窗口都带出来，免得每回重敲一遍资金数。
///
/// 只涨跌幅这两个是"每只票不一样"的，存在这里只是当默认值用。
/// </summary>
public class PositionSizingSettings
{
    /// <summary>可投资总资金（元）——凯利仓位的分母。0 表示还没填过。</summary>
    public double Capital { get; set; }

    /// <summary>凯利折扣：1=满凯利、0.5=半凯利、0.25=四分之一凯利。默认半凯利。</summary>
    public double KellyFraction { get; set; } = 0.5;

    /// <summary>单票仓位上限（0~1）。默认 25%。</summary>
    public double MaxWeight { get; set; } = 0.25;

    /// <summary>上次填的目标涨幅（%），下次打开带出来。</summary>
    public double GainPct { get; set; } = 20;

    /// <summary>上次填的止损幅度（%）。</summary>
    public double LossPct { get; set; } = 10;
}
