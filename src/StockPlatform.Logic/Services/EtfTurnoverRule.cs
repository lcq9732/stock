namespace StockPlatform.Logic.Services;

/// <summary>一行 ETF 换手率的裁判结果。</summary>
public enum EtfTurnoverVerdict
{
    /// <summary>当天没有成交（volume 为 0 或空）：换手率就该是 0，不参与裁判。</summary>
    NotApplicable,
    /// <summary>跟官方份额算出来的一致（差 ≤ 0.01）。</summary>
    Consistent,
    /// <summary>库里有值，但跟官方份额算出来的对不上。</summary>
    Wrong,
    /// <summary>有成交、库里换手率却是 0 或 NULL。</summary>
    Missing,
    /// <summary>没有前一交易日的官方份额（交易所开始有数据以前、上市首日、交易所那天没列这只），判不了。</summary>
    Unjudgeable,
}

/// <summary>
/// ETF 换手率的判据（2026-09-23，见 doc/etf-turnover-recalc-design.md）。纯计算、无 IO。
///
/// ════ 算法：成交量 ÷ **前一交易日**官方份额（2026-09-23 用户定）════
/// 腾讯自己的口径**不统一**（2026-09-23 拿上交所 2012 年起全部份额逐行复算沪市 ETF 查实）：
///   · 2024-10 以前几乎全是 ÷T-1；
///   · 2024-11 起多数改成 ÷T（当天份额），但 2025-05、2026-07/08 仍有成片的 ÷T-1；
///   · 2022-09-09/13/14、10-12 这四天用的份额比实际还旧，哪种都对不上（539 行）；
///   · 同一天两个接口还可能不一样：510150 在 2024-09-30，前复权 83.63 = ÷T-1、不复权 48.38 = ÷T。
/// 跟我们的抓取时刻无关（抓取滞后 7 天以上的行里照样有近万行 ÷T-1），同一时刻重抓也还是那个数，
/// 所以【重新拉取失败】修不好，只能按一个统一的算法重算。
/// 深市 ETF（同日拿深交所 2016-09-26 起的份额复算）腾讯**一直是 ÷T-1**，2022–2026 只对得上 ÷T 的仅 55 行，
/// 对不上的也集中在上面那几个故障日——统一成 T-1 对深市几乎不改现有值。
///
/// 为什么选 T-1 而不是 T：① 收盘后马上就能算——当天份额要等上交所发布（常常第二天才有）；
/// ② 是开盘前就知道的份额，回测里不会用到当天收盘后才公布的数据。
/// 代价是 2024-11 以后腾讯按 ÷T 给的那十几万行会被改掉，这是有意的。
///
/// ════ 单位 ════
/// Bar.volume 在 ETF 上是**手**（1 手 = 100 份），份额统一存**万份**（上交所原样、深交所导出是份、provider 里换算），于是
/// 换手率(%) = 手 × 100 ÷ (万份 × 10⁴) × 100 = 手 ÷ 万份。
/// 保留两位小数跟腾讯一致。
///
/// ⚠ 不拿东财/搜狐当裁判：东财 f61 用的是**当前**份额去除历史成交量（510150 在 2024-09-30 给 60.40），
/// 搜狐给 96.95，分母对不上任何一天的官方份额。
/// </summary>
public static class EtfTurnoverRule
{
    /// <summary>腾讯给两位小数，差一个末位算一致。1e-9 只吃浮点噪声。</summary>
    public const double Tolerance = 0.01 + 1e-9;

    /// <summary>
    /// 期望换手率(%)。<paramref name="prevShareWan"/> 缺失或不是正数时返回 null（判不了）。
    /// </summary>
    /// <param name="volumeLots">当天成交量（手）。</param>
    /// <param name="prevShareWan">前一交易日官方份额（万份）。</param>
    public static double? Expected(double volumeLots, double? prevShareWan)
        => prevShareWan is > 0
            ? Math.Round(volumeLots / prevShareWan.Value, 2, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>裁判一行。</summary>
    /// <param name="volumeLots">库里的成交量（手）。</param>
    /// <param name="stored">库里的换手率。</param>
    /// <param name="prevShareWan">前一交易日官方份额（万份），没有就是 null。</param>
    public static EtfTurnoverVerdict Judge(double? volumeLots, double? stored, double? prevShareWan)
    {
        if (volumeLots is not > 0) return EtfTurnoverVerdict.NotApplicable;
        if (Expected(volumeLots.Value, prevShareWan) is not { } expected) return EtfTurnoverVerdict.Unjudgeable;
        // 成交极小时期望值本身会四舍五入成 0.00，那时库里的 0 是对的，不算缺
        if (stored is null or 0)
            return expected == 0 ? EtfTurnoverVerdict.Consistent : EtfTurnoverVerdict.Missing;
        return Math.Abs(stored.Value - expected) <= Tolerance
            ? EtfTurnoverVerdict.Consistent
            : EtfTurnoverVerdict.Wrong;
    }

    /// <summary>
    /// Bar 里一只代码像不像沪深 ETF（sh5 / sz15 开头 + 6 位）。
    ///
    /// 只给**提示文案**用（【重新拉取失败】复查后提醒去跑【ETF换手率校正】），不参与任何数据判断——
    /// 校正任务的名单来自交易所份额表本身，不猜代码规则。
    /// </summary>
    public static bool LooksLikeEtfBarCode(string code)
        => code.Length == 8 && (code.StartsWith("sh5", StringComparison.Ordinal)
                                || code.StartsWith("sz15", StringComparison.Ordinal));
}
