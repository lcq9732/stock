using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 给东财源的龙虎榜行补上"对应值"(<see cref="LhbRow.Deviation"/>) 和"成交量"
/// (<see cref="LhbRow.Volume"/>)——东财 <c>RPT_DAILYBILLBOARD_DETAILSNEW</c> 里这两列
/// 根本不存在（只给 CHANGE_RATE 和 TURNOVERRATE），只能靠本地日K补。
///
/// ════ 每一条规则都是拿真值验出来的，不是照条文猜的（2026-09-09）════
/// 验证方法：拿东财 2026-07-10~08-31 共 3000 行的**原文上榜原因**去分类，算出来的值跟本地
/// 库里新浪那份历史的"对应值"比，|差| &lt; 0.05 算命中。为什么能拿新浪当真值：新浪的
/// **数值**是交易所公布的原值，错的是它把上榜原因归并成了粗类（于是同一 reason 下混着不同
/// 口径的值）——按东财原文重新分类之后，这批数值就是可用的对照物。
///
/// <code>
///   类型          取值                          来路   命中率
///   换手率类      东财 TURNOVERRATE             源     98.3% (主板) / 91.4% (非主板)
///   涨跌幅达X%类  东财 CHANGE_RATE              源     85.9%
///   单日偏离值    个股涨跌幅 − 对应指数涨跌幅   派生   91.4% (主板)
///   振幅类        (最高 − 最低) / 最低          派生   93.6%
///   累计偏离值    —— 留空，见下 ——             —      最好也只有 54.9%
/// </code>
///
/// ════ 为什么"连续N日累计偏离值"一律留空 ════
/// 试过所有能想到的算法：N 日累计涨幅之差（54.9%）、N−1 日跨度（38.8%）、逐日偏离求和
/// （1.0%），按东财原文逐类分组也没有哪一类能过 74%。原因不是算法——是**起算日由交易所
/// 判定**（哪三天算"连续三个交易日"是交易所认定异动的窗口），本地复现不出来。
/// 与其写一个一半对一半错、还没人知道哪一半错的值，不如留空：留空是"没有"，写错是"有毒"。
/// 需要的话 change_rate / turnover_rate 两列照样在，分析尽可以用它们。
///
/// ════ 振幅用 (高−低)/**最低**，不是 /前收 ════
/// 反直觉，但实测如此：/最低 命中 93.6%，/前收 是 0.0%，/收盘 是 9.9%。交易所"价格振幅"
/// 的分母就是当日最低价。
/// </summary>
public sealed class LhbDeviationDeriver
{
    /// <summary>对应值该怎么来。</summary>
    public enum DeviationKind
    {
        /// <summary>算不出来，也不该算（退市整理、无涨跌幅限制、融资买入占比、换手率比值倍数…）。</summary>
        None,
        /// <summary>换手率——东财直接给了。</summary>
        Turnover,
        /// <summary>当日涨跌幅——东财直接给了。</summary>
        ChangeRate,
        /// <summary>单日涨跌幅偏离值——本地派生。</summary>
        DailyDeviation,
        /// <summary>价格振幅——本地派生。</summary>
        Amplitude,
        /// <summary>连续N日累计偏离值——**故意不派生**，起算日由交易所判定，见类注释。</summary>
        CumulativeDeviation,
    }

    /// <summary>
    /// 按上榜原因原文分类。**判断顺序不能改**：交易所的原文里"有价格涨跌幅限制的日换手率达到
    /// 20%的前五只证券"同时含"涨跌幅"和"换手率"，"有价格涨跌幅限制的日价格振幅达到15%…"同时
    /// 含"涨跌幅"和"振幅"——先判细的，最后才轮到"涨跌幅达X%"这个最泛的。
    /// </summary>
    public static DeviationKind Classify(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return DeviationKind.None;

        // "日均换手率与前五个交易日的日均换手率的比值达到30倍"——对应值是**倍数**不是换手率，
        // 必须比"换手率"这一条先判，否则会被当成换手率填进去。
        if (reason.Contains("比值")) return DeviationKind.None;

        if (reason.Contains("换手率")) return DeviationKind.Turnover;
        if (reason.Contains("振幅")) return DeviationKind.Amplitude;

        if (reason.Contains("偏离值"))
        {
            // "严重异常期间日收盘价格涨幅偏离值累计达到100%的证券"——起算日更没谱（"严重异常
            // 期间"是交易所划定的一段），连天数都读不出来。
            if (reason.Contains("严重异常")) return DeviationKind.None;
            return reason.Contains("累计") || ParseConsecutiveDays(reason) != null
                ? DeviationKind.CumulativeDeviation
                : DeviationKind.DailyDeviation;
        }

        // "日涨幅达到15%的前5只证券"/"有价格涨跌幅限制的日收盘价格跌幅达到15%的前五只证券"
        if (reason.Contains("涨幅达") || reason.Contains("跌幅达")) return DeviationKind.ChangeRate;

        return DeviationKind.None;
    }

    /// <summary>
    /// 从"连续三个交易日"/"连续3个交易日"里读出天数，读不出返回 null。
    /// 目前只被 <see cref="Classify"/> 用来认出累计类（认出来就是留空），留着是因为哪天
    /// 交易所把起算规则讲清楚了，派生累计值的第一步就是它。
    /// </summary>
    public static int? ParseConsecutiveDays(string reason)
    {
        int at = reason.IndexOf("连续", StringComparison.Ordinal);
        if (at < 0) return null;
        for (int i = at + 2; i < reason.Length && i <= at + 4; i++)
        {
            int? n = reason[i] switch
            {
                '二' => 2, '三' => 3, '四' => 4, '五' => 5,
                >= '2' and <= '9' => reason[i] - '0',
                _ => null,
            };
            if (n != null) return n;
        }
        return null;
    }

    private readonly Func<string, string, IReadOnlyList<Bar>> _loadBars;
    private readonly Dictionary<(string Code, string Gran), Dictionary<DateTime, int>> _indexCache = new();
    private readonly Dictionary<(string Code, string Gran), IReadOnlyList<Bar>> _barCache = new();

    /// <param name="loadBars">按 (代码, 粒度) 取日K，**必须按 period_start 升序**。
    /// 取不到返回空列表即可。</param>
    public LhbDeviationDeriver(Func<string, string, IReadOnlyList<Bar>> loadBars)
    {
        _loadBars = loadBars;
    }

    /// <summary>
    /// 给一批行补 <see cref="LhbRow.Deviation"/> 和 <see cref="LhbRow.DeviationSource"/>。
    /// 补不出来的留 null——**不猜、不填 0**：0 在数值列里是个有意义的值，用它表示"不知道"
    /// 会让所有下游统计悄悄失真。
    ///
    /// ⚠ <see cref="LhbRow.Volume"/> **不在这里补**，理由见 <see cref="WhyVolumeIsNotDerived"/>。
    /// </summary>
    public void Apply(IEnumerable<LhbRow> rows)
    {
        foreach (var r in rows)
        {
            var (dev, src) = DeriveDeviation(r);
            r.Deviation = dev;
            r.DeviationSource = dev == null ? "" : src;
        }
    }

    /// <summary>
    /// ════ 为什么成交量不从本地日K派生（2026-09-09 实测后放弃）════
    ///
    /// 本来打算这么做的：东财不给成交量，本地 Bar 表有，除以 100 换成万股即可——2026-09-08 的
    /// 000560 拿这个算法算出 64,772.41 万股，跟新浪当天给的 64,772.4081 严丝合缝。
    /// 但拿 59 只票全跑一遍，只有 39 只对得上。剩下 20 只暴露出 Bar 表两个独立的毛病：
    ///
    ///   ① <b>单位不一致</b>：全市场 09-08 那天的日K里，主板 3203 只、创业板 1404 只、
    ///      北交所 342 只的 volume 都是**手**（amount ÷ volume ÷ close ≈ 100），
    ///      而**科创板 688/689 的 613 只是"股"**（≈ 1）。跨板块除以 100，科创板就差 100 倍。
    ///   ② <b>部分行的量和额本身就少了</b>：603999 那天 Bar 记 6,914 万元，而东财和新浪都说
    ///      2.13 亿——不是单位问题，是那一行的数据不全（21:02 抓的，不是盘中）。
    ///
    /// 两个毛病都跟龙虎榜无关、也不该在这里绕过去（在派生器里按板块判单位，等于把 Bar 的
    /// 问题抹平在一个角落里，别处照样踩）。所以东财源的成交量一律留空，等 Bar 表修好了再说。
    ///
    /// 影响不大：东财给了成交额（<c>ACCUM_AMOUNT</c>，已换算成万元存进 amount），
    /// 以及换手率、龙虎榜买卖额、占总成交比——分析要的量能信息都在。
    /// 新浪时代已经存进去的历史成交量原样保留，不动。
    /// </summary>
    public const string WhyVolumeIsNotDerived =
        "Bar.volume 单位在科创板上是股、其余板块是手，且部分行的量额本身不全——见本常量的文档注释。";

    private (double? Value, string Source) DeriveDeviation(LhbRow r)
    {
        switch (Classify(r.Reason))
        {
            case DeviationKind.Turnover:
                return (r.TurnoverRate, LhbDeviationSources.FromSource);

            case DeviationKind.ChangeRate:
                return (r.ChangeRate, LhbDeviationSources.FromSource);

            case DeviationKind.Amplitude:
            {
                var bar = FindBar(r.StockCode, r.TradeDate);
                if (bar == null || bar.Low <= 0) return (null, "");
                return ((bar.High - bar.Low) / bar.Low * 100, VerifiedTag(r.StockCode));
            }

            case DeviationKind.DailyDeviation:
            {
                var stock = ChangeRateOn(r.StockCode, r.TradeDate);
                var benchmark = MarketIndexCatalog.DeviationBenchmarkFor(r.StockCode);
                if (stock == null || benchmark == null) return (null, "");

                // 指数只抓了不复权的 day 一种粒度，个股则优先用 day_raw（见 ChangeRateOn）
                var index = ChangeRateOn(benchmark, r.TradeDate, Granularity.Day);
                if (index == null) return (null, "");
                return (stock.Value - index.Value, VerifiedTag(r.StockCode));
            }

            // 累计类：故意不算，理由见类注释
            default:
                return (null, "");
        }
    }

    /// <summary>只有沪深主板的派生规则拿真值验过，其余标"未核验"——见
    /// <see cref="MarketIndexCatalog.IsDeviationRuleVerified"/>。</summary>
    private static string VerifiedTag(string code) =>
        MarketIndexCatalog.IsDeviationRuleVerified(code)
            ? LhbDeviationSources.Derived
            : LhbDeviationSources.DerivedUnverified;

    /// <summary>某天相对前一个交易日的涨跌幅(%)；没有当天或没有前一天的K线返回 null。</summary>
    private double? ChangeRateOn(string code, DateTime date, string? granularity = null)
    {
        foreach (var gran in granularity != null ? [granularity] : PreferredGranularities)
        {
            var (bars, index) = Load(code, gran);
            if (!index.TryGetValue(date.Date, out int i) || i < 1) continue;
            double prev = bars[i - 1].Close;
            if (prev <= 0) continue;
            return (bars[i].Close / prev - 1) * 100;
        }
        return null;
    }

    private Bar? FindBar(string code, DateTime date)
    {
        foreach (var gran in PreferredGranularities)
        {
            var (bars, index) = Load(code, gran);
            if (index.TryGetValue(date.Date, out int i)) return bars[i];
        }
        return null;
    }

    /// <summary>
    /// 个股优先用**不复权**日线：交易所算的是当天的真实成交价，而 <see cref="Granularity.Day"/>
    /// 是减法式前复权，跨除权日时比值会失真。day_raw 是 2026-08-31 才开始抓的，早年数据不全，
    /// 所以取不到就回退 day——单日涨跌幅只有当天恰好除权才有差别，回退的代价极小。
    /// </summary>
    private static readonly string[] PreferredGranularities = [Granularity.DayRaw, Granularity.Day];

    private (IReadOnlyList<Bar> Bars, Dictionary<DateTime, int> Index) Load(string code, string gran)
    {
        var key = (code, gran);
        if (!_barCache.TryGetValue(key, out var bars))
        {
            bars = _loadBars(code, gran) ?? [];
            _barCache[key] = bars;
            var idx = new Dictionary<DateTime, int>(bars.Count);
            for (int i = 0; i < bars.Count; i++) idx[bars[i].PeriodStart.Date] = i;
            _indexCache[key] = idx;
        }
        return (bars, _indexCache[key]);
    }
}
