using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>晨检里一条纪律动作。Severity 跟 MorningStockRowViewModel 的分档同义：
/// 0=止损级（必须走）1=止盈/卖点 2=警示 3=提示（加仓之类）。</summary>
public sealed record DisciplineAction(string Text, int Severity);

/// <summary>
/// "陈峰法"的量能判据与持仓纪律（2026-11-16）。**纯计算，不碰库**——选股引擎
/// <see cref="ProfitableSmallCapAnalysisEngine"/> 和每日晨检共用这里的缩量/放量口径，
/// 两边各写一遍迟早会对不上。
///
/// ════ 口径：按**成交额**算，不按换手率（用户 2026-11-16 定）════
/// 回测用的是换手率（≈成交量）。换成成交额后会把价格变化也算进去：连跌几天、量不变，成交额也会
/// 变小、更容易判成缩量——对"挑低位"的选股是顺方向的，但跟回测数字不是一一对应。
/// 成交额为 0 / 缺失的日子既不判缩量也不进均值（库里少量行量额本身就不全）。
///
/// ════ 回测依据（2016-01 ~ 2026-11，流通 5~50 亿盈利股，含退市股，双边 0.3%）════
/// · 放量(当日 &gt; 前20日均×2)且收涨之后 20 日，同池超额 −1.6%（两段都成立，样本每天约 60 个）——卖的那半有效；
/// · 缩量(&lt; 0.6 倍)之后 20 日只 +0.12%——买的那半几乎没有信息，作用是"不追高"；
/// · "放量就减"叠加到价格版加减仓规则上：年化 +8.3% → +9.3%，最大回撤 −40% → −31%；
///   熊市/震荡年护盘（2018、2022、2026），牛市主升浪里会卖早（2019 从 +56% 掉到 +15%）。
/// </summary>
public static class VolumeExitDiscipline
{
    /// <summary>算均额用的回看天数（不含当天）。</summary>
    public const int AvgDays = 20;
    /// <summary>均额至少要有这么多个有效交易日，否则不判。</summary>
    public const int MinValidDays = 15;
    /// <summary>当日成交额 &lt; 前20日均额 × 这个数 = 缩量。</summary>
    public const double ShrinkRatio = 0.6;
    /// <summary>当日成交额 &gt; 前20日均额 × 这个数 = 放量。回测里 1.5 倍太频繁（年化掉到 +3.7%）、3 倍偏少。</summary>
    public const double SpikeRatio = 2.0;

    /// <summary>第 <paramref name="index"/> 根的成交额 ÷ 它前 <see cref="AvgDays"/> 根的平均成交额；
    /// 当天额缺失或有效天数不足返回 null。</summary>
    public static double? AmountRatio(IReadOnlyList<Bar> bars, int index)
    {
        if (index < 0 || index >= bars.Count) return null;
        double today = bars[index].Amount;
        if (!(today > 0)) return null;
        double sum = 0; int n = 0;
        for (int t = Math.Max(0, index - AvgDays); t < index; t++)
        {
            double a = bars[t].Amount;
            if (a > 0) { sum += a; n++; }
        }
        if (n < MinValidDays) return null;
        return today / (sum / n);
    }

    /// <summary>当天是否"放量收涨"（成交额 &gt; 前20日均×2 且收盘高于前一天）。</summary>
    public static bool IsSpikeUp(IReadOnlyList<Bar> bars, int index, out double? ratio)
    {
        ratio = AmountRatio(bars, index);
        return ratio > SpikeRatio && index >= 1 && bars[index].Close > bars[index - 1].Close;
    }

    /// <summary>收盘价在最近 <paramref name="days"/> 根 [最低收盘, 最高收盘] 区间里的位置（0=最低 1=最高）。
    /// 有效根数不到一半返回 null。</summary>
    public static double? Position(IReadOnlyList<Bar> bars, int days)
    {
        if (bars.Count == 0) return null;
        int from = Math.Max(0, bars.Count - days);
        if (bars.Count - from < days / 2) return null;
        double hi = double.MinValue, lo = double.MaxValue;
        for (int t = from; t < bars.Count; t++)
        {
            hi = Math.Max(hi, bars[t].Close);
            lo = Math.Min(lo, bars[t].Close);
        }
        return hi > lo ? (bars[^1].Close - lo) / (hi - lo) : 0.5;
    }

    /// <summary>持仓的纪律输入。价格都是实际成交价；K线用前复权日线——前复权的最新收盘就等于实际价，
    /// 所以"最新收盘 vs 买入均价"可以直接比。</summary>
    public sealed record HoldingState(
        double AvgBuyPrice,
        double LastBuyPrice,
        int BuyLotCount,
        bool HasSoldSinceFirstBuy,
        double? TtmNetProfit,
        double? TtmOcf);

    /// <summary>加仓上限（份）：回测里每只最多 3 份。</summary>
    public const int MaxBuyLots = 3;

    /// <summary>
    /// 陈峰法持仓逐日纪律。规则（回测 V2 价格版 + 放量减仓）：
    ///   ① 财报转亏 / 经营现金流转负 → 清仓
    ///   ② 放量收涨：还没减过 → 减半；减过 → 清仓
    ///   ③ 较买入均价 +50% 或 250日位置 &gt; 80% → 清仓
    ///   ④ 较买入均价 +25% 且还没减过 → 减半
    ///   ⑤ 较上次买入价跌 15% 且买入不足 3 笔 → 可加仓
    /// "减过没有"看主动仓里第一笔买入之后有没有卖出记录——用户手动卖了就算减过。
    /// </summary>
    public static List<DisciplineAction> EvaluateHolding(IReadOnlyList<Bar> bars, HoldingState s)
    {
        var actions = new List<DisciplineAction>();
        if (bars.Count == 0 || !(s.AvgBuyPrice > 0)) return actions;
        int i = bars.Count - 1;
        double close = bars[i].Close;
        double gain = close / s.AvgBuyPrice - 1;

        if (s.TtmNetProfit is <= 0)
            actions.Add(new($"基本面：近四季归母净利{s.TtmNetProfit / 1e8:+0.00;-0.00}亿已转亏，按陈峰法清仓", 0));
        else if (s.TtmOcf is <= 0)
            actions.Add(new($"基本面：近四季经营现金流{s.TtmOcf / 1e8:+0.00;-0.00}亿转负，按陈峰法清仓", 0));

        if (IsSpikeUp(bars, i, out var ratio))
            actions.Add(s.HasSoldSinceFirstBuy
                ? new($"放量减仓：成交额为20日均×{ratio:F1}且收涨，已减过一次 → 清仓", 1)
                : new($"放量减仓：成交额为20日均×{ratio:F1}且收涨 → 先减半", 1));

        var pos = Position(bars, 250);
        if (gain >= 0.50)
            actions.Add(new($"止盈：较买入均价+{gain * 100:F0}% → 清仓", 1));
        else if (pos > 0.80)
            actions.Add(new($"高位：250日位置{pos * 100:F0}%（>80%）→ 清仓", 1));
        else if (gain >= 0.25 && !s.HasSoldSinceFirstBuy)
            actions.Add(new($"止盈：较买入均价+{gain * 100:F0}% → 减半", 1));

        if (s.LastBuyPrice > 0 && close <= s.LastBuyPrice * 0.85 && s.BuyLotCount < MaxBuyLots)
            actions.Add(new($"可加仓：较上次买入价{s.LastBuyPrice:F2}跌{(1 - close / s.LastBuyPrice) * 100:F0}%（第{s.BuyLotCount + 1}/{MaxBuyLots}份）", 3));

        return actions;
    }

    /// <summary>陈峰法持仓的参考价（把 ③④⑤ 翻译成价格）。</summary>
    public static string AdviceText(HoldingState s) =>
        $"减半≥{s.AvgBuyPrice * 1.25:F2}，清仓≥{s.AvgBuyPrice * 1.5:F2}" +
        (s.LastBuyPrice > 0 && s.BuyLotCount < MaxBuyLots ? $"，加仓≤{s.LastBuyPrice * 0.85:F2}" : "");
}
