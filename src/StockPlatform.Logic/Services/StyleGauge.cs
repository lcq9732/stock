using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>风格温度计里的一条腿（一个风格篮子/指数在观察窗口内的表现）。</summary>
/// <param name="Name">显示名，如"红利100"。</param>
/// <param name="ReturnPct">窗口收益率（%）。</param>
/// <param name="ExcessPct">相对大盘（沪深300）的超额（%）。</param>
/// <param name="Members">实际参与等权计算的只数；指数腿为 0。</param>
public record StyleLeg(string Name, double ReturnPct, double ExcessPct, int Members);

/// <summary>钱在往哪边躲。</summary>
public enum StyleMood
{
    /// <summary>红利/低波明显跑赢 → 防御占优。</summary>
    Defensive,
    /// <summary>成长明显跑赢 → 风险偏好回升。</summary>
    RiskOn,
    /// <summary>没有明显偏向。</summary>
    Neutral,
}

public record StyleGaugeResult(int Days, double MarketPct, StyleLeg? Dividend, StyleLeg? BlueChip, StyleLeg? Growth, StyleMood Mood)
{
    /// <summary>一行文字，直接摆在晨检的"大盘总开关"底下。</summary>
    public string Text
    {
        get
        {
            if (Dividend == null && BlueChip == null && Growth == null)
                return "当前风格：本地缺少指数成分数据，先在抓取程序里跑一次\"拉取指数成分/权重\"";

            var parts = new List<string> { $"沪深300 {Fmt(MarketPct)}" };
            foreach (var leg in new[] { Dividend, BlueChip, Growth })
                if (leg != null)
                    parts.Add($"{leg.Name} {Fmt(leg.ReturnPct)}（超额{Fmt(leg.ExcessPct)}）");

            var verdict = Mood switch
            {
                StyleMood.Defensive => "→ 钱在往红利/低波躲，防御占优",
                StyleMood.RiskOn => "→ 钱在往成长/科技走，风险偏好回升",
                _ => "→ 风格没有明显偏向",
            };
            return $"近{Days}个交易日风格：{string.Join("｜", parts)} {verdict}";
        }
    }

    private static string Fmt(double pct) => $"{(pct >= 0 ? "+" : "")}{pct:F1}%";
}

/// <summary>
/// 风格温度计（2026-08-20新增）——回答"大盘跌的时候钱躲到哪儿去了"。
///
/// 缘起：用本地数据统计过"大盘下跌时蓝筹是不是会涨"（2016-2026，个股后复权）：**绝对上涨基本不发生**
/// （沪深300 跌超3%的37天里，上证50等权篮子上涨占比 0%），**但相对抗跌非常稳定**（同一档跑赢率
/// 92%，超额 +0.68%；红利篮子超额 +1.48%），代价是上涨日跑输。所以有用的不是"蓝筹会不会涨"，
/// 而是"此刻钱在往防御还是成长跑"——这个类就把它量出来，摆在晨检的大盘总开关旁边。
///
/// 两个必须守住的口径：
/// ① **个股一律用复权序列**，优先 <see cref="Granularity.DayAdj"/>、退而求其次 <see cref="Granularity.DayHfq"/>。
///    前复权（减法式）序列里除权跳空会被算成日收益——实测同一个统计用前复权算出"跌超3%档平均 -15.24%"，
///    后复权是 -2.80%，方向都反了。
///    day_hfq 也不是干净的：它是"送转乘、分红加"的混合式，加法项阻尼波动，**恰好把红利篮子这种
///    高分红股压得最狠**（实测收益率÷真实 中位 0.967、5% 分位 0.754）；day_adj 没有这个问题。
///    指数没有除权，用普通日线。
/// ② **只取跟大盘最新交易日对齐的成分股**。停牌股的"最近6根K线"跨的是另一段时间，混进等权平均
///    会把窗口收益算歪。
///
/// 成分名单是"最近一次抓取时"的（见 IIndexConsRepository.GetConsByIndex），算最近几天的强弱没问题，
/// 别拿它回溯很久以前。
/// </summary>
public static class StyleGauge
{
    /// <summary>观察窗口（交易日）。5天≈一周：短到能反映"这几天钱在往哪跑"，又不至于被单日噪音带偏。</summary>
    public const int DefaultDays = 5;

    /// <summary>红利代表：中证红利（100只）——A股最常用的红利基准。</summary>
    public const string DividendIndex = "000922";

    /// <summary>大盘蓝筹代表：上证50。</summary>
    public const string BlueChipIndex = "000016";

    /// <summary>判定"明显跑赢"的超额门槛（百分点）——低于这个值就算没有明显偏向，避免天天喊风格切换。</summary>
    private const double MoodThreshold = 0.5;

    /// <summary>窗口收益率（%）：最后一根 vs 往前第 <paramref name="days"/> 根。数据不够返回 null。</summary>
    public static double? WindowReturn(IReadOnlyList<Bar> bars, int days)
    {
        if (bars.Count < days + 1) return null;
        var start = bars[^(days + 1)].Close;
        return start > 0 ? (bars[^1].Close / start - 1) * 100 : null;
    }

    /// <summary>等权篮子的窗口收益（%）+ 实际参与的只数。**要求成分股在窗口的头尾两天都有K线**
    /// （日期精确相等）：只对齐末尾是不够的——窗口中间停牌过的票，它自己"往前数第N根"落在更早的
    /// 日期上，算出来的是另一段时间的收益，混进等权平均会把结论带偏。对不齐的直接跳过。</summary>
    public static (double? Pct, int Members) BasketReturn(IEnumerable<IReadOnlyList<Bar>> members, DateTime windowStart, DateTime windowEnd)
    {
        var rets = new List<double>();
        foreach (var bars in members)
        {
            if (bars.Count < 2 || bars[^1].PeriodStart.Date != windowEnd.Date) continue;
            double? open = null;
            for (int i = bars.Count - 2; i >= 0; i--)
            {
                if (bars[i].PeriodStart.Date != windowStart.Date) continue;
                open = bars[i].Close;
                break;
            }
            if (open is > 0) rets.Add((bars[^1].Close / open.Value - 1) * 100);
        }
        return rets.Count == 0 ? (null, 0) : (rets.Average(), rets.Count);
    }

    /// <param name="marketBars">沪深300 日线（基准）。</param>
    /// <param name="dividendMembers">红利指数成分股的**后复权**日线。</param>
    /// <param name="blueChipMembers">上证50 成分股的**后复权**日线。</param>
    /// <param name="growthBars">创业板指日线（成长代表）。</param>
    public static StyleGaugeResult? Build(
        IReadOnlyList<Bar> marketBars,
        IEnumerable<IReadOnlyList<Bar>> dividendMembers,
        IEnumerable<IReadOnlyList<Bar>> blueChipMembers,
        IReadOnlyList<Bar> growthBars,
        int days = DefaultDays)
    {
        if (WindowReturn(marketBars, days) is not { } market) return null;
        // 窗口的头尾两天都由大盘序列定，成分股必须对得上（见 BasketReturn）。
        var windowStart = marketBars[^(days + 1)].PeriodStart;
        var windowEnd = marketBars[^1].PeriodStart;

        var (divPct, divN) = BasketReturn(dividendMembers, windowStart, windowEnd);
        var (bluePct, blueN) = BasketReturn(blueChipMembers, windowStart, windowEnd);
        var growthPct = WindowReturn(growthBars, days);

        StyleLeg? Leg(string name, double? pct, int n) => pct is { } p ? new StyleLeg(name, p, p - market, n) : null;
        var dividend = Leg($"红利{divN}", divPct, divN);
        var blueChip = Leg($"上证50", bluePct, blueN);
        var growth = Leg("创业板指", growthPct, 0);

        // 防御 vs 成长：看红利和创业板谁的超额更高，且要超过门槛才表态。
        double defensive = dividend?.ExcessPct ?? blueChip?.ExcessPct ?? 0;
        double risky = growth?.ExcessPct ?? 0;
        var mood = defensive - risky > MoodThreshold ? StyleMood.Defensive
                 : risky - defensive > MoodThreshold ? StyleMood.RiskOn
                 : StyleMood.Neutral;

        return new StyleGaugeResult(days, market, dividend, blueChip, growth, mood);
    }
}
