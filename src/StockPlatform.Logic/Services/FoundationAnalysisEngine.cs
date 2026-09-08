using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "峰哥法"（类名沿用 Foundation——描述算法本身，不随人名/规则变化而改类名）。
///
/// 【2026-09-07 规则整体替换】按用户描述换成"一根K线从最低到最高、从头到尾穿破三根均线"：
///   C1 单根K线贯穿 MA5/MA10/MA20：Low &lt; min(MA5,MA10,MA20) 且 High &gt; max(MA5,MA10,MA20)。
///      用最高/最低价（含影线），因为用户说的是"从最低到最高"；**不限阴阳**（用户确认）。
///   C2 三线粘合：命中日 (maxMA-minMA)/收盘 ≤ 间距上限（默认1.5%）。
///   C3 低位：命中日收盘价位于近60日 [最低,最高] 区间的下 X%（默认20%）。
/// 上一版是"近 N 个交易日内出现过涨停"（单条），涨停口径在金叉法/短线法里仍有保留。
///
/// 为什么 C2/C3 不是我加的花样，而是必需的（2026-09-07 实测，全市场 type=stock、
/// 2025-09-01~2026-09-04 共246个交易日、day_adj 前复权口径）：
///   · 只用 C1：每日命中 **681 只**（全市场 12%），5/10/20日前瞻收益 +0.02%/+0.11%/+0.00%，
///     胜率 47.1%/46.9%/44.8% —— 跟同期全市场基准(+0.16%/+0.26%/+0.46%，胜率47.4%/46.7%/45.8%)
///     没有区别，后半年（2026-03起的下跌市）反而明显跑输基准(10日 -1.24% vs 基准 -0.57%)。
///     原因：三根均线多数时候间距只有1.5%，而A股日振幅中位4.3%，随便一根K线就"包住"了。
///   · C1+C2+C3（间距≤1.5%、位置≤20%）：每日 84 只，10日 +1.28% 胜率56.5%（基准+0.26%/46.7%）。
///     前后半段各自都跑赢同期基准（前半段10日 +1.98% vs +1.12%；后半段 +0.40% vs -0.57%），
///     两段方向一致，不是阈值过拟合。
///   · 实测**否掉**的几个想当然的加强条件，都不要再加回来：放量≥5日均量1.5倍（5日 -0.16%）、
///     振幅≥6%（5日 -0.60%）、"贯穿余量"最大的前20只（5日 -0.29% 胜43.1%，是全表最差的一档）
///     —— 穿得越猛越差，跟"不追高"一致。所以 SortScore 用的是"低位分"不是贯穿幅度。
/// 固定日线（均线穿破是日线概念）。条件详情图用同一套判据自行标注命中K线，图文一致
/// （见 FoundationChartBuilder）。
/// </summary>
public class FoundationAnalysisEngine
{
    /// <summary>判断"低位"用的回看窗口（交易日）——固定60日，跟结果表"60日位置"那列同一个口径。</summary>
    public const int PositionWindow = 60;

    /// <summary>三线间距上限（%）默认值：命中日 (maxMA-minMA)/收盘。</summary>
    public const double DefaultMaxSpreadPct = 1.5;

    /// <summary>低位上限（%）默认值：命中日收盘在近60日区间里的相对位置，0%=最低、100%=最高。</summary>
    public const double DefaultMaxLowPositionPct = 20;

    private readonly IBarRepository _barRepository;

    public FoundationAnalysisEngine(IBarRepository barRepository)
    {
        _barRepository = barRepository;
    }

    /// <param name="lookbackDays">回看窗口 N：最近 N 根K线里出现过贯穿就算。**默认1 = 只看今天
    /// 这根**（今天收盘后跑、明天开盘前出名单）；调大只是为了回看最近几天、方便核对形态。</param>
    /// <param name="maxSpreadPct">三线间距上限（%），见 <see cref="DefaultMaxSpreadPct"/>。</param>
    /// <param name="maxLowPositionPct">低位上限（%），见 <see cref="DefaultMaxLowPositionPct"/>。</param>
    public StockScreenResult Analyze(string code, string name, int lookbackDays,
        double maxSpreadPct = DefaultMaxSpreadPct, double maxLowPositionPct = DefaultMaxLowPositionPct)
    {
        var bars = _barRepository.Query(code, Granularity.Day);
        // 只需要凑齐一个 60 日位置窗口就能判最新那根：回看窗口的起点在下面被 clamp 到
        // PositionWindow-1，所以 N 调大**不会**再多要历史（否则一只刚好只有60根历史的新股
        // 会在 N=5 时被整只跳过，而它最新那根其实是算得出来的）。
        int minRequired = PositionWindow;
        if (bars.Count < minRequired)
            return new StockScreenResult
            {
                Code = code,
                Name = name,
                Granularity = Granularity.Day,
                Error = $"日线历史数据不足（仅 {bars.Count} 条），至少需要 {minRequired} 条",
            };

        var closes = bars.Select(b => b.Close).ToList();
        int i = bars.Count - 1;

        double Ma(int t, int period)
        {
            double sum = 0;
            for (int k = t - period + 1; k <= t; k++) sum += closes[k];
            return sum / period;
        }

        // C1：从今天往前找**最近一根**贯穿三线的K线（Low 在三线之下、High 在三线之上）。
        // 窗口起点同时受"MA20 要算得出"和"60日位置窗口要凑得齐"约束，取 PositionWindow-1。
        int windowStart = Math.Max(PositionWindow - 1, i - lookbackDays + 1);
        int hit = -1;
        double hitLoMa = 0, hitHiMa = 0;
        for (int t = i; t >= windowStart; t--)
        {
            double loMa = Math.Min(Ma(t, 5), Math.Min(Ma(t, 10), Ma(t, 20)));
            double hiMa = Math.Max(Ma(t, 5), Math.Max(Ma(t, 10), Ma(t, 20)));
            if (bars[t].Low < loMa && bars[t].High > hiMa)
            {
                hit = t;
                hitLoMa = loMa;
                hitHiMa = hiMa;
                break;
            }
        }

        string windowText = lookbackDays <= 1 ? "最新交易日" : $"最近{lookbackDays}个交易日";
        var criteria = new List<CriterionResult>();
        double? spreadPct = null, positionPct = null, ampPct = null;
        bool isYang = false, closeAboveTop = false;

        if (hit < 0)
        {
            criteria.Add(new CriterionResult
            {
                Name = "单根K线贯穿MA5/MA10/MA20",
                Satisfied = false,
                Basis = $"{windowText}没有出现最低价低于三线、最高价高于三线的K线",
            });
            criteria.Add(new CriterionResult { Name = $"三根均线粘合（间距≤{maxSpreadPct:F1}%）", Satisfied = false, Basis = "没有命中的K线，未评估" });
            criteria.Add(new CriterionResult { Name = $"处于低位（近{PositionWindow}日区间下{maxLowPositionPct:F0}%）", Satisfied = false, Basis = "没有命中的K线，未评估" });
        }
        else
        {
            var b = bars[hit];
            isYang = b.Close >= b.Open;
            closeAboveTop = b.Close > hitHiMa;
            ampPct = b.Close > 0 ? (b.High - b.Low) / b.Close * 100 : 0;
            spreadPct = b.Close > 0 ? (hitHiMa - hitLoMa) / b.Close * 100 : 0;

            double winLow = double.MaxValue, winHigh = double.MinValue;
            for (int k = hit - PositionWindow + 1; k <= hit; k++)
            {
                winLow = Math.Min(winLow, bars[k].Low);
                winHigh = Math.Max(winHigh, bars[k].High);
            }
            positionPct = winHigh > winLow ? (b.Close - winLow) / (winHigh - winLow) * 100 : 50;

            criteria.Add(new CriterionResult
            {
                Name = "单根K线贯穿MA5/MA10/MA20",
                Satisfied = true,
                Basis = $"{b.PeriodStart:yyyy-MM-dd} 的{(isYang ? "阳" : "阴")}线：最低 {b.Low:F2} < 三线最低 {hitLoMa:F2}，" +
                        $"最高 {b.High:F2} > 三线最高 {hitHiMa:F2}（振幅 {ampPct:F2}%，收盘 {b.Close:F2} " +
                        $"{(closeAboveTop ? "站上三线" : "仍在三线之间或之下")}）",
            });
            criteria.Add(new CriterionResult
            {
                Name = $"三根均线粘合（间距≤{maxSpreadPct:F1}%）",
                Satisfied = spreadPct <= maxSpreadPct,
                Basis = $"命中日三线间距 {spreadPct:F2}%（(最高均线{hitHiMa:F2} − 最低均线{hitLoMa:F2}) ÷ 收盘{b.Close:F2}），上限 {maxSpreadPct:F1}%",
            });
            criteria.Add(new CriterionResult
            {
                Name = $"处于低位（近{PositionWindow}日区间下{maxLowPositionPct:F0}%）",
                Satisfied = positionPct <= maxLowPositionPct,
                Basis = $"命中日收盘 {b.Close:F2} 位于近{PositionWindow}日区间 [{winLow:F2}, {winHigh:F2}] 的 {positionPct:F0}% 位置，上限 {maxLowPositionPct:F0}%",
            });
        }

        var result = new StockScreenResult
        {
            Code = code,
            Name = name,
            Granularity = Granularity.Day,
            DataDate = bars[i].PeriodStart,
            LastClose = closes[i],
            // 排序分 = 低位程度（越靠近60日区间底部分越高）。实测排序维度里只有这个方向是对的，
            // 按贯穿幅度排反而最差（见类注释）。注意它只是优先级参考，组内区分度并不强。
            SortScore = positionPct.HasValue ? 100 - positionPct.Value : null,
            Category = hit < 0 ? "" : (isYang ? "阳线" : "阴线"),
            PatternNote = hit < 0
                ? ""
                : $"{bars[hit].PeriodStart:MM-dd}｜位置{positionPct:F0}%｜间距{spreadPct:F2}%｜振幅{ampPct:F2}%｜{(closeAboveTop ? "站上三线" : "未站上")}",
            Criteria = criteria,
        };
        result.Passed = result.Criteria.AllSatisfiedIgnoringMissingData();
        return result;
    }
}
