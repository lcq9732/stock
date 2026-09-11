namespace StockPlatform.Logic.Models;

/// <summary>
/// <c>StockWatchIndicator</c> 的一行：**我们自己认定的**"这只票该盯这个行业指标"。
/// 见 doc/watch-item-design.md §4.1。
///
/// ⚠ 跟 <see cref="StockIndicatorLink"/>（东财给的 <c>StockIndustryIndicator</c>）是**两张表**，
/// 不是一张表加个标记。理由是**所有权**，不是数据质量：
/// <c>SqliteIndustryIndicatorRepository.ReplaceLinks()</c> 是 <c>DELETE FROM</c> 整表替换——
/// 东财目录认定自己是那张表的唯一主人；而我们的规则也要"每轮重算"，也想当主人。
/// 两个都对，只能各占一张，查询时 UNION。
///
/// 写进东财那张表的后果是**静默的**：下一轮【行业景气指标】跑完映射就没了，
/// 界面上只表现为"这只票恰好没有指标"，排查时根本想不到是这儿。
/// </summary>
/// <param name="Code">6 位股票代码。</param>
/// <param name="IndicatorId">指向 <c>IndustryIndicator.indicator_id</c>。</param>
/// <param name="Weight">展示序，越小越相关——跟东财 <c>indicator_order</c> 同语义，合并显示时能排到一起。</param>
/// <param name="Origin">见 <see cref="WatchIndicatorOrigin"/>。**决定这一行会不会被规则重算冲掉**。</param>
/// <param name="Reason">为什么挂它（给人读）。规则产出的写规则名，手挂的写人话。</param>
public sealed record WatchIndicatorLink(
    string Code,
    string IndicatorId,
    int Weight,
    string Origin,
    string Reason)
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// <see cref="WatchIndicatorLink.Origin"/> 的取值。**这是本设计里的硬边界**，
/// 跟 <c>WatchItem.Origin</c> 是同一条线的两次出现（见 doc/watch-item-design.md §2）：
/// 规则每轮重算时整组重建自己那部分，**人手挂的一行都不许碰**。
/// 不划这条线，规则跑一次就把人挂的冲掉了，而且跟上面那个坑一样——界面上看不出来。
/// </summary>
public static class WatchIndicatorOrigin
{
    /// <summary>规则派生（L1）。每轮 <c>ReplaceRuleLinks</c> 整组重建。</summary>
    public const string Rule = "rule";

    /// <summary>人手挂的（L2）。任何规则都不许删改。</summary>
    public const string Manual = "manual";
}

/// <summary>
/// 一条"板块 → 该盯哪些行业指标"的规则，来自 <c>data/watch-indicator-rules.json</c>。
///
/// 为什么是配置不是代码：这张映射表会**随认识变化频繁调整**（今天觉得锂电该看碳酸锂，
/// 明天发现还该看六氟磷酸锂），改一条要重编译发版不可接受。
/// </summary>
/// <param name="BoardCode">东财板块码 BKxxxx，对 <c>StockIndustryEm.board_code</c>。</param>
/// <param name="BoardName">板块名，**只为了让配置文件能读懂**，匹配一律按 <paramref name="BoardCode"/>。</param>
/// <param name="IndicatorIds">该板块的票要挂的指标，顺序即 <see cref="WatchIndicatorLink.Weight"/>。</param>
/// <param name="Reason">这条规则的理由，会原样写进每一行的 <see cref="WatchIndicatorLink.Reason"/>。</param>
public sealed record BoardIndicatorRule(
    string BoardCode,
    string BoardName,
    IReadOnlyList<string> IndicatorIds,
    string Reason);
