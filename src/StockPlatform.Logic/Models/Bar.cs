namespace StockPlatform.Logic.Models;

/// <summary>
/// Supported bar granularities. New periods can be added here without touching the
/// Bar table schema — see doc/data-platform-design.md section 4.
/// </summary>
public static class Granularity
{
    public const string Day = "day";

    /// <summary>
    /// 日线的**后复权**版本（2026-07-30新增）。为什么要单独存一份：数据源的前复权（<see cref="Day"/>）
    /// 是"减法式"（原价 − 累计分红），显示用没问题（最新价=真实价），但往前推十年后，高分红股票的
    /// 复权价会被减到接近零甚至为负，用它算收益率会得出物理上不可能的结果——2016~2019 实测有 1498 只
    /// 股票（占全市场27%）出现过单日 ±11% 以上的"涨跌幅"，最大 +7200%。后复权是乘法式、永不为负、
    /// 收益率正确，专供回测（FactorLab）使用；界面展示仍用前复权。
    /// 只对**个股和退市股**抓取：指数不除权（两种复权返回同一序列）、ETF 暂不回测、板块指数是本地合成的。
    /// 不生成对应的周/月线（回测用不到，省一半空间和抓取时间）。
    /// </summary>
    public const string DayHfq = "day_hfq";

    /// <summary>
    /// 日线的**不复权**版本（原始成交价，2026-08-31 新增）。
    ///
    /// ════ 为什么要单独存一份 ════
    /// 它是全库唯一**不随时间变化**的价格序列——抓一次永远有效。前复权（<see cref="Day"/>）的基准
    /// 随每次分红变（一除权，那只票的全部历史都要重取）；后复权（<see cref="DayHfq"/>）是数据源
    /// 按"送转乘、分红加"的混合式算的，收益率被系统性阻尼（实测工商银行 −38%、中国石化 −46%）。
    /// 有了原始价，任何口径都能本地推出来，而且推出来的东西自己说了算。
    ///
    /// 直接用途是算 <see cref="DayAdj"/>：原始价 × 复权因子，收益率精确。
    /// 只对**个股和退市股**抓（指数不除权、ETF 暂不回测、板块指数是本地合成的），不生成周/月线。
    /// </summary>
    public const string DayRaw = "day_raw";

    /// <summary>
    /// **回测专用的复权序列**（2026-08-31 新增）＝ <see cref="DayRaw"/> × 本地算的乘法式复权因子。
    ///
    /// ════ 跟 DayHfq 的区别 ════
    /// DayHfq 是数据源给的，算法是"原价 × 累计送转因子 ＋ 累计分红金额"——那个加法项会阻尼波动，
    /// 股息越高、股价越低阻尼越重。实测非除权日的收益率相对真实收益率：茅台 ×0.855、平安银行
    /// ×0.811、云南白药 ×0.772、工商银行 ×0.625、中国石化 ×0.542。拿它做回测，高股息股会显得
    /// "波动小、回撤浅"，因子排序被扭曲。
    ///
    /// 本序列是纯乘法式：非除权日 factor 不变，所以**收益率精确等于真实收益率**（12 只样本、
    /// 23000 个交易日实测零偏差）；除权日按 前收/除权参考价 连接。
    ///
    /// 复权因子来自 Dividend 表，但每个除权日都用价格校验过——实际跳空与理论对不上的记录一律
    /// 不算数（破产重整的"资本公积转增"是给债权人的，不分配给原股东，不产生除权，而数据源会把
    /// 它当普通转增列出来；实测华英农业/*ST恒立/力帆都踩过这个坑）。见 AdjustFactorCalculator。
    /// </summary>
    public const string DayAdj = "day_adj";

    public const string Week = "week";
    public const string Month = "month";
    public const string Min1 = "min1";
    public const string Min5 = "min5";
    public const string Min15 = "min15";
    public const string Min30 = "min30";
    public const string Min60 = "min60";
}

/// <summary>One row of the multi-granularity Bar table (see doc/data-platform-design.md section 4).</summary>
public class Bar
{
    public string Code { get; set; } = "";
    public string Granularity { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public double Open { get; set; }
    public double Close { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Volume { get; set; }
    public double Amount { get; set; }
    // 涨跌幅不再作为字段存储/传递——它是收盘价的派生值，一律在需要处用相邻收盘价现算
    // （见 doc §9.5 / doc/data-platform-design.md 2026-07-14 变更记录）。
    public double Turnover { get; set; }

    /// <summary>实际抓取到这一天数据的时间（墙钟时间，不是交易日日期）——2026-07-09新增，用来
    /// 判断"今天"这一天是不是盘中抓的（可能还会变）还是收盘后抓的（已经是最终数据，以后不用再
    /// 抓）。历史上早于这天入库的行没有这个值，读出来是 <see cref="DateTime.MinValue"/>（永远
    /// 判定为"未确认"，直到下次被重新抓到一次），见 FetchOrchestrator.IsConfirmedFinal。</summary>
    public DateTime FetchedAt { get; set; }
}
