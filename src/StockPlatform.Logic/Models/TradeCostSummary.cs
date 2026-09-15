namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只票全部成交笔的汇总（2026-08-11新增）——把多笔买入/卖出按股数加权成"总股数 + 均价"，
/// 并按 <see cref="TradeFeeSettings"/> 算出**逐笔**的佣金/过户费/印花税（费用是按笔收的，
/// 所以最低5元佣金必须逐笔判断，不能拿总金额算一次）。
///
/// 口径：成本用**含费成本均价**（<see cref="NetAvgCost"/> = 买入总支出÷买入总股数），已实现盈亏
/// 用"卖出净到手 − 已卖股数×含费成本均价"——也就是移动加权平均成本法，跟券商对账单一致。
/// 还没卖的部分一律**按现价全部卖出**推算（<see cref="TotalPnlIfLiquidated"/>、<see cref="BreakEvenPrice"/>），
/// 卖出那头的佣金/过户费/印花税照扣——要看的是"现在兑现，到手到底是赚是亏"。
/// </summary>
public class TradeCostSummary
{
    public int BuyLotCount { get; private init; }
    public int SellLotCount { get; private init; }

    public int BuyShares { get; private init; }
    public int SellShares { get; private init; }
    public int RemainingShares => Math.Max(0, BuyShares - SellShares);

    /// <summary>成交金额（价格×股数）合计，不含费。</summary>
    public double BuyAmount { get; private init; }
    public double SellAmount { get; private init; }

    /// <summary>费用合计（佣金＋过户费＋卖出印花税），逐笔算好再加总。</summary>
    public double BuyFee { get; private init; }
    public double SellFee { get; private init; }
    public double TotalFee => BuyFee + SellFee;

    /// <summary>买入总支出（含费）＝金额＋费用；卖出净到手＝金额−费用。</summary>
    public double NetCost => BuyAmount + BuyFee;
    public double NetProceeds => SellAmount - SellFee;

    /// <summary>加权平均成交价（纯价格，不含费）——没有有效买入/卖出时为 null。</summary>
    public double? AvgBuyPrice { get; private init; }
    public double? AvgSellPrice { get; private init; }

    /// <summary>含费成本均价——**真实的保本价**。股数未知的老数据为 null（没法摊到每股）。</summary>
    public double? NetAvgCost => BuyShares > 0 ? NetCost / BuyShares : null;

    /// <summary>已卖出部分的已实现盈亏（含费，元）；没卖过或股数未知为 null。</summary>
    public double? RealizedNet => SellShares > 0 && NetAvgCost is { } cost
        ? NetProceeds - SellShares * cost
        : null;

    /// <summary>已实现收益率（含费）＝已实现盈亏 ÷ 已卖出部分的成本。</summary>
    public double? RealizedNetPct => RealizedNet is { } pnl && NetAvgCost is { } cost && SellShares > 0 && cost > 0
        ? pnl / (SellShares * cost) * 100
        : null;

    /// <summary>算这份汇总时用的费率——下面几个"按现价全卖"的推算要用，省得每处都传一遍。</summary>
    private TradeFeeSettings _fees = new();

    /// <summary>按 <paramref name="price"/> 卖掉 <paramref name="shares"/> 股，扣完佣金/过户费/印花税后的净到手。</summary>
    public double NetProceedsAt(double price, int shares)
    {
        double amount = price * shares;
        return amount - _fees.FeeFor(amount, TradeSide.Sell);
    }

    /// <summary>**假如现在按 <paramref name="latestClose"/> 把剩下的股票全部卖掉**，这笔交易的总盈亏（元）：
    /// 已经落袋的部分 ＋ 剩余仓位卖出后的净到手 − 剩余仓位的含费成本。买入和卖出的费用全部扣干净，
    /// 也就是"现在割肉/兑现，账户里实际多出或少掉多少钱"。股数未知的老数据为 null。</summary>
    public double? TotalPnlIfLiquidated(double latestClose)
    {
        if (NetAvgCost is not { } cost) return null;
        double remainingProceeds = RemainingShares > 0 ? NetProceedsAt(latestClose, RemainingShares) : 0;
        return (RealizedNet ?? 0) + remainingProceeds - RemainingShares * cost;
    }

    /// <summary>同上，按买入总支出算的收益率（%）。</summary>
    public double? TotalPnlPctIfLiquidated(double latestClose)
        => NetCost > 0 && TotalPnlIfLiquidated(latestClose) is { } pnl ? pnl / NetCost * 100 : null;

    /// <summary>
    /// 止亏价——**剩下的股票卖到这个价，整笔交易刚好不赚不亏**（跌破它就是真亏钱）。
    /// 把两头的费用全算进去了：买入的佣金/过户费已经在含费成本里，卖出的佣金/过户费/印花税按这个
    /// 价格再扣一遍；已经分批卖出落袋的盈亏也算进来（前面卖赚了，剩下的止亏价就低一些）。
    ///
    /// 解法：净到手 = 金额 − max(佣金率×金额, 最低佣金) − 过户费 − 印花税，是两条直线取小；令它等于
    /// "剩余成本 − 已落袋"，两条直线各解一个价格，取**较大**的那个就是真正的解（min 的反函数取 max）。
    /// 剩余0股（已清仓）返回 null；已落袋的钱已经覆盖了剩余成本时返回 0（白送都不亏）。
    /// </summary>
    public double? BreakEvenPrice()
    {
        if (RemainingShares <= 0 || NetAvgCost is not { } cost) return null;

        double target = RemainingShares * cost - (RealizedNet ?? 0);
        if (target <= 0) return 0;

        double c = _fees.CommissionRateBp / 10000, t = _fees.TransferRateBp / 10000, s = _fees.StampDutyRateBp / 10000;
        double byRate = 1 - c - t - s, byMin = 1 - t - s;
        double p1 = byRate > 0 ? target / (RemainingShares * byRate) : double.NegativeInfinity;
        double p2 = byMin > 0 ? (target + _fees.MinCommission) / (RemainingShares * byMin) : double.NegativeInfinity;
        double p = Math.Max(p1, p2);
        return double.IsFinite(p) && p > 0 ? p : null;   // 费率之和≥100%这种离谱设置就不显示
    }

    public static TradeCostSummary For(IEnumerable<TradeLot> lots, TradeFeeSettings fees)
    {
        var valid = lots.Where(l => l.Price > 0).ToList();
        var buys = valid.Where(l => l.Side == TradeSide.Buy).ToList();
        var sells = valid.Where(l => l.Side == TradeSide.Sell).ToList();

        return new TradeCostSummary
        {
            _fees = fees,
            BuyLotCount = buys.Count,
            SellLotCount = sells.Count,
            BuyShares = buys.Sum(l => Math.Max(0, l.Shares)),
            SellShares = sells.Sum(l => Math.Max(0, l.Shares)),
            BuyAmount = buys.Sum(l => l.Price * Math.Max(0, l.Shares)),
            SellAmount = sells.Sum(l => l.Price * Math.Max(0, l.Shares)),
            BuyFee = buys.Sum(fees.FeeFor),
            SellFee = sells.Sum(fees.FeeFor),
            AvgBuyPrice = Avg(buys),
            AvgSellPrice = Avg(sells),
        };
    }

    /// <summary>按股数加权的均价；全部笔都没填股数（老数据）时退化成简单平均。</summary>
    private static double? Avg(List<TradeLot> lots)
    {
        if (lots.Count == 0) return null;
        int shares = lots.Sum(l => Math.Max(0, l.Shares));
        return shares > 0
            ? lots.Sum(l => l.Price * Math.Max(0, l.Shares)) / shares
            : lots.Average(l => l.Price);
    }
}
