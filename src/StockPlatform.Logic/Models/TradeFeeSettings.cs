namespace StockPlatform.Logic.Models;

/// <summary>
/// A股交易的费率设置（2026-08-11新增）——用户在"主动仓"页顶部自己填，因为各家券商佣金不一样。
/// 默认值就是用户现在这家的：佣金万分之1.2（不足5元按5元收）、过户费万分之0.1，两者买卖都收；
/// 印花税万分之5，只在卖出时收。费率一律用"万分之几"存（<see cref="CommissionRateBp"/> = 1.2 就是
/// 万分之1.2）——券商和交易所都是这么报价的，界面上填 1.2 比填 0.00012 不容易错。
/// </summary>
public class TradeFeeSettings
{
    /// <summary>佣金费率，单位"万分之"。买卖都收。</summary>
    public double CommissionRateBp { get; set; } = 1.2;

    /// <summary>单笔最低佣金（元）——算出来不足这个数就按这个数收。填0表示不设下限。</summary>
    public double MinCommission { get; set; } = 5;

    /// <summary>过户费费率，单位"万分之"。买卖都收。</summary>
    public double TransferRateBp { get; set; } = 0.1;

    /// <summary>印花税费率，单位"万分之"。**只有卖出收**。</summary>
    public double StampDutyRateBp { get; set; } = 5;

    /// <summary>某一笔的佣金：金额×费率，不足最低佣金按最低佣金。金额为0（老数据没填股数）时不收——
    /// 那不是一笔真实成交，收个最低佣金5元反而把盈亏算歪。</summary>
    public double Commission(double amount)
        => amount <= 0 ? 0 : Math.Max(amount * CommissionRateBp / 10000, MinCommission);

    public double TransferFee(double amount) => amount <= 0 ? 0 : amount * TransferRateBp / 10000;

    public double StampDuty(double amount, TradeSide side)
        => amount <= 0 || side != TradeSide.Sell ? 0 : amount * StampDutyRateBp / 10000;

    /// <summary>一笔成交的全部费用（佣金＋过户费＋卖出印花税）。</summary>
    public double FeeFor(double amount, TradeSide side)
        => Commission(amount) + TransferFee(amount) + StampDuty(amount, side);

    public double FeeFor(TradeLot lot) => FeeFor(lot.Price * lot.Shares, lot.Side);

    /// <summary>界面上用来一眼确认当前费率的说明串。</summary>
    public string Describe()
        => $"佣金万分之{CommissionRateBp:0.###}（最低{MinCommission:0.##}元）、过户费万分之{TransferRateBp:0.###}，买卖都收；"
         + $"印花税万分之{StampDutyRateBp:0.###}，仅卖出";

    public TradeFeeSettings Clone() => (TradeFeeSettings)MemberwiseClone();
}
