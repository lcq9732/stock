using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>一段被拿来量回撤的行情波段：从 <see cref="StartIdx"/> 到 <see cref="EndIdx"/>，
/// 端点取的是这段里的最高价和最低价（不是收盘价——回撤位是画在影线上的）。</summary>
public class FibSwing
{
    /// <summary>波段低点在 bars 里的下标。</summary>
    public int LowIdx { get; init; }
    /// <summary>波段高点在 bars 里的下标。</summary>
    public int HighIdx { get; init; }
    public double Low { get; init; }
    public double High { get; init; }
    public DateTime LowDate { get; init; }
    public DateTime HighDate { get; init; }

    /// <summary>上涨波段（低点在前）——回撤位从高点往下量，答的是"回调到哪儿可能止跌"。
    /// false 则是下跌波段（高点在前），回撤位从低点往上量，答的是"反弹到哪儿可能受阻"。</summary>
    public bool IsUpSwing => LowIdx < HighIdx;

    public int StartIdx => Math.Min(LowIdx, HighIdx);
    public int EndIdx => Math.Max(LowIdx, HighIdx);
    public double Range => High - Low;

    /// <summary>波段幅度占低点的百分比——太小的波段画回撤位没有意义（见 <see cref="FibonacciRetracement.MinSwingPct"/>）。</summary>
    public double RangePct => Low > 0 ? Range / Low * 100 : 0;

    public string Describe()
        => IsUpSwing
            ? $"上涨波段 {LowDate:yyyy-MM-dd} 低 {Low:F2} → {HighDate:yyyy-MM-dd} 高 {High:F2}（+{RangePct:F1}%）"
            : $"下跌波段 {HighDate:yyyy-MM-dd} 高 {High:F2} → {LowDate:yyyy-MM-dd} 低 {Low:F2}（−{Range / High * 100:F1}%）";
}

/// <summary>一条回撤/扩展位。<see cref="Ratio"/> 是比例（0.382 等），<see cref="Price"/> 是它对应的价格。</summary>
public readonly record struct FibLevel(double Ratio, double Price, bool IsExtension)
{
    /// <summary>图上和信息栏统一用的标签："38.2%"、"扩展 161.8%"。</summary>
    public string Label => IsExtension ? $"扩展 {Ratio * 100:0.#}%" : $"{Ratio * 100:0.#}%";
}

/// <summary>
/// 斐波那契回撤位（2026-08-17新增）。把一段行情的高低点之间按 23.6/38.2/50/61.8/78.6% 切几条水平线，
/// 用来把"跌得够深了"这种模糊感觉换成具体价位——主要用途是给止损/目标价一个客观的锚（见
/// PositionSizingWindow 的【从K线取值】），以及在K线图上叠加显示。
///
/// 关于有效性，别把它当信号源：0.618 出现在自然界不代表股价该听它的，学术检验里它的独立预测力
/// 基本为零。它之所以还有点用，一是很多人在看同一条线（自我实现），二是"趋势中回调三到六成后
/// 延续"本来就是常态、画不画线都成立。所以本程序把它定位成**刻度尺**：帮你把止损放在一个有依据
/// 的位置，而不是告诉你该不该买。
///
/// 50% 严格说不是斐波那契数（相邻数之比收敛到 0.618），是道氏理论"回吐一半"的习惯，各家软件都
/// 顺手放进来了，这里也保留。
///
/// **数据口径**：喂进来的应该是 <see cref="Granularity.Day"/>（前复权）而不是 day_hfq（后复权）——
/// 回撤位是要拿去跟盘口价格比对、当止损单价用的，必须跟用户在行情软件里看到的价格一致；后复权的
/// 绝对价位（宁波韵升 2026-07 的 107.75 vs 实际 10.00）没法执行。代价是：**如果波段跨过了除权日，
/// 前复权的历史价被减法式调整过，各档比例会失真**（同一段行情两套数据算出来的38.2%位能差3%以上）。
/// 默认的短线60日窗口很少跨除权日，影响有限；真要量一段跨过除权的老行情，图上看看趋势就行，
/// 别拿那几个价位当止损。
/// </summary>
public static class FibonacciRetracement
{
    /// <summary>回撤比例。0 和 1 是波段的两个端点本身，一起画出来才能看出回撤位在整段里的位置。</summary>
    public static readonly double[] Ratios = { 0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0 };

    /// <summary>扩展位——用来估目标价（趋势延续时常见的两个落点）。</summary>
    public static readonly double[] ExtensionRatios = { 1.272, 1.618 };

    /// <summary>自动识别时的回看窗口（交易日）。短线用60（约三个月，一个完整波段的常见长度），
    /// 中线120、长线250。默认短线——本程序主用的是短线法，波段取太长会把止损拉到十几个点开外，
    /// 跟短线法回测出来的 ±10% 纪律完全对不上。</summary>
    public const int ShortLookback = 60;
    public const int MediumLookback = 120;
    public const int LongLookback = 250;

    /// <summary>波段幅度下限（%）——高低点之间还不到这个幅度就说明这段是横盘震荡，
    /// 硬画回撤位只会得到一堆挤在一起、没有意义的线。</summary>
    public const double MinSwingPct = 5;

    /// <summary>
    /// 在最近 <paramref name="lookback"/> 根K线里找最显著的一段波段：取这段里的最高价和最低价，
    /// 方向由两者的先后决定（低在前=上涨波段，高在前=下跌波段）。
    ///
    /// 这就是各家看盘软件"自动斐波那契"的通行做法，好处是完全可解释、没有可调参数——找出来的
    /// 端点你自己在图上一眼就能核对。幅度不足 <see cref="MinSwingPct"/> 或数据太少时返回 null，
    /// 宁可不画也不画一堆没意义的线。
    /// </summary>
    public static FibSwing? FindSwing(IReadOnlyList<Bar> bars, int lookback = ShortLookback)
    {
        if (bars.Count < 2) return null;
        int start = Math.Max(0, bars.Count - Math.Max(lookback, 2));
        int hi = start, lo = start;
        for (int i = start; i < bars.Count; i++)
        {
            if (bars[i].High > bars[hi].High) hi = i;
            if (bars[i].Low < bars[lo].Low) lo = i;
        }
        if (hi == lo) return null;

        var swing = Build(bars, lo, hi);
        return swing.RangePct < MinSwingPct ? null : swing;
    }

    /// <summary>手动指定区间（用户在图上点的两个下标）——端点仍取这两点之间的最高/最低价，
    /// 所以点得不那么准也没关系，落在同一段行情上就够了。</summary>
    public static FibSwing? FromRange(IReadOnlyList<Bar> bars, int idxA, int idxB)
    {
        if (bars.Count < 2) return null;
        int a = Math.Clamp(Math.Min(idxA, idxB), 0, bars.Count - 1);
        int b = Math.Clamp(Math.Max(idxA, idxB), 0, bars.Count - 1);
        if (a == b) return null;

        int hi = a, lo = a;
        for (int i = a; i <= b; i++)
        {
            if (bars[i].High > bars[hi].High) hi = i;
            if (bars[i].Low < bars[lo].Low) lo = i;
        }
        return hi == lo ? null : Build(bars, lo, hi);
    }

    private static FibSwing Build(IReadOnlyList<Bar> bars, int lo, int hi) => new()
    {
        LowIdx = lo,
        HighIdx = hi,
        Low = bars[lo].Low,
        High = bars[hi].High,
        LowDate = bars[lo].PeriodStart,
        HighDate = bars[hi].PeriodStart,
    };

    /// <summary>
    /// 算出各档回撤位的价格。上涨波段从高点往下量（0%=高点、100%=低点），下跌波段从低点往上量
    /// （0%=低点、100%=高点）——两种情况下"比例越大=离波段起点越远"的含义一致。
    /// <paramref name="includeExtensions"/> 为真时附上 127.2%/161.8% 两个扩展位（顺势方向的目标价）。
    /// </summary>
    public static List<FibLevel> Levels(FibSwing swing, bool includeExtensions = true)
    {
        var result = new List<FibLevel>();
        foreach (var r in Ratios)
            result.Add(new FibLevel(r, PriceAt(swing, r), false));

        if (includeExtensions)
            foreach (var r in ExtensionRatios)
                result.Add(new FibLevel(r, PriceAt(swing, r), true));

        return result;
    }

    /// <summary>
    /// 某个比例对应的价格。0~1 是**回撤位**，落在波段内部；&gt;1 是**扩展位**，顺着原趋势方向延伸到
    /// 波段之外——上涨波段的 161.8% 在前高**上方**（突破后的目标），不是在低点下方。两者方向相反，
    /// 所以不能用同一个公式一路算下去。
    /// </summary>
    public static double PriceAt(FibSwing swing, double ratio)
    {
        if (ratio <= 1)
            return swing.IsUpSwing
                ? swing.High - swing.Range * ratio      // 上涨波段：从高点往下回撤
                : swing.Low + swing.Range * ratio;      // 下跌波段：从低点往上反弹

        return swing.IsUpSwing
            ? swing.Low + swing.Range * ratio           // = 前高 + (ratio−1)×波段，突破后的目标
            : swing.High - swing.Range * ratio;         // 下跌波段：跌破前低之后的目标
    }

    /// <summary>
    /// 现价在这段波段里的位置——已经回撤了百分之几。上涨波段里 0% 表示还在高点、100% 表示已经跌回
    /// 起涨点；超过 100% 说明整段涨幅都吐光了（趋势已经不成立，别再拿这段的回撤位做文章）。
    /// </summary>
    public static double RetracedPct(FibSwing swing, double price)
    {
        if (swing.Range <= 0) return 0;
        return swing.IsUpSwing
            ? (swing.High - price) / swing.Range * 100
            : (price - swing.Low) / swing.Range * 100;
    }

    /// <summary>
    /// 现价下方最近的那条线（支撑），没有就返回 null。用于把止损放在一个有依据的位置。
    ///
    /// <paramref name="minGapPct"/> 是"离现价至少要有多远"（%）：现价常常正好贴着某一档，那条线
    /// 拿来当止损会被日内噪音直接扫掉，所以做决策时要跳过太近的档位取下一档。纯展示（信息栏里
    /// 报"下方支撑"）用默认的 0，如实显示最近的那条。
    /// </summary>
    public static FibLevel? SupportBelow(FibSwing swing, double price, double minGapPct = 0)
    {
        double ceiling = price * (1 - minGapPct / 100);
        FibLevel? best = null;
        foreach (var lv in Levels(swing))
            if (lv.Price < ceiling && (best is null || lv.Price > best.Value.Price)) best = lv;
        return best;
    }

    /// <summary>现价上方最近的那条线（阻力/目标），没有就返回 null。
    /// <paramref name="minGapPct"/> 同 <see cref="SupportBelow"/>——紧贴现价的那档没有交易价值
    /// （目标位只高1%的话，盈亏比根本不成立），做决策时要跳过。</summary>
    public static FibLevel? ResistanceAbove(FibSwing swing, double price, double minGapPct = 0)
    {
        double floor = price * (1 + minGapPct / 100);
        FibLevel? best = null;
        foreach (var lv in Levels(swing))
            if (lv.Price > floor && (best is null || lv.Price < best.Value.Price)) best = lv;
        return best;
    }
}
