namespace StockPlatform.Logic.Models;

/// <summary><see cref="LhbRow.Source"/> 的取值——哪个数据源抓的。</summary>
public static class LhbSources
{
    /// <summary>东财 datacenter（2026-09-09 起的主源）。上榜原因是交易所原文。</summary>
    public const string EastMoney = "em";

    /// <summary>新浪龙虎榜每日页（后备）。上榜原因被归并成粗类。</summary>
    public const string Sina = "sina";
}

/// <summary>
/// <see cref="LhbRow.DeviationSource"/> 的取值——"对应值"那一列是**抓来的还是算出来的**。
///
/// 为什么非要分清：新浪那份历史就是因为分不清才没法用（reason 被归并成粗类、对应值却还跟着
/// 各自的原规则走，同一个 reason 下混着当日涨跌幅、两日累计、多日累计）。派生值和源给的值
/// 混在一列里而不留标记，下一个用这张表的人只能重新踩一遍同样的坑。
/// </summary>
public static class LhbDeviationSources
{
    /// <summary>源直接给的（换手率类取东财 TURNOVERRATE、涨跌幅类取 CHANGE_RATE），没做任何计算。</summary>
    public const string FromSource = "源";

    /// <summary>本地日K算的，**规则已用主板历史数据验证过**：2026-06 以来 1300+ 行里，
    /// 沪主板对上证综指中位误差 0.0032、命中率 88.2%，深主板对深证综指 0.0082 / 70.6%。</summary>
    public const string Derived = "派生";

    /// <summary>
    /// 本地日K算的，**但没有可信真值能验**——创业板/科创板/北交所。
    ///
    /// 不是算法可疑，是没有对照物：唯一的历史真值是新浪那份，而它对这三个板块的对应值本身
    /// 就是错配的（2026-08-04 创业板同一 reason 下混着 20.0 / 39.8 / 31.54 三种口径）。
    /// 拿错的东西当真值去"验证"，验过了反而更危险。要定案得人工抽几条对交易所公告。
    /// </summary>
    public const string DerivedUnverified = "派生-未核验";
}
