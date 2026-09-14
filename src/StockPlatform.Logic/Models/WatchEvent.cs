namespace StockPlatform.Logic.Models;

/// <summary>
/// 叙述里的**一行事件**（2026-09-14 重构）。
///
/// 观察项页从"左右两表、按事项组织"改成"**一股一行、事件叙述**"：
/// 看到一件事不用再跳到另一张表找结果，一只票身上发生过什么按时间列在一起。
/// </summary>
/// <param name="Date">事件发生（或将要发生）的日期。</param>
/// <param name="Category">事项类别：回购／限售解禁／业绩预告／股东增减持／龙虎榜／行情。</param>
/// <param name="Text">人话叙述，带数据。不含日期——日期单独排在前面。</param>
/// <param name="Round">
/// 同一类事项的第几轮（只有回购用得上）。一只票可以做好几轮回购——
/// 实测永和股份 07-16 发方案、08-01 完毕、08-01 又发新方案，两轮混在一起会读成一团。
/// </param>
/// <param name="IsFuture">还没发生（解禁日期在将来）。界面上可以标个"还有 N 天"。</param>
/// <param name="Indent">
/// 缩进层级：0＝主行（回购方案、解禁…），1＝它的后续进展（首次回购、进展、完毕）。
///
/// 排序规则（用户 2026-09-14 定的）：**事项之间按日期倒序，事项内部按日期正序**。
/// 回购那组就是"方案在最上面，执行过程缩进跟在后面、顺着时间往下读"——
/// 一件事的来龙去脉本来就该顺着看，倒着读"完毕→进展→首次"是反直觉的。
/// </param>
/// <param name="Tip">
/// 附注，界面上挂 ToolTip、**不占正文**（2026-09-14 加）。
///
/// 现在只有行业指标用：「碳酸锂指数 338.85（较 09-10 -3.62%）」后面本来还跟着
/// 「— 锂电池：碳酸锂是主要原材料成本」，那是"为什么这条指标跟这只票有关"——
/// 看一次就知道了，天天占半行不值当，尤其是塞进分析详情窗右栏那种窄地方。
/// </param>
public sealed record WatchEvent(
    DateTime Date,
    string Category,
    string Text,
    int? Round = null,
    bool IsFuture = false,
    int Indent = 0,
    string? Tip = null);

/// <summary>事项类别。界面按它分组，同组的事件连在一起。</summary>
public static class WatchCategory
{
    public const string Buyback = "回购";
    public const string ShareLift = "限售解禁";
    public const string EarningsForecast = "业绩预告";
    public const string EarningsSchedule = "定期报告预约披露";
    public const string HolderChange = "股东增减持";
    public const string Lhb = "龙虎榜";
    public const string Dividend = "分红方案";
    public const string Quote = "行情";
    public const string Indicator = "行业指标";
}

/// <summary>一只票的全部事件，给主表一行用。</summary>
/// <param name="Opinion">个人观点，来自 <c>notes/{code}.md</c>。</param>
public sealed record StockWatchEvents(
    string Code,
    string Name,
    IReadOnlyList<WatchEvent> Events,
    string Opinion = "");

/// <summary>
/// 市场层面的观察项（2026-09-14）——**不是个股独有**的那些。
///
/// 为什么要分出来：跌破 MA20 每只票都有，熊市里几千只同时触发，
/// 逐票列在个股行里没有任何区分度。而"今天 4127 只跌破 MA20（占全市场 68%）"
/// 本身是有用的市场信息。
///
/// ⚠ 个股行里**仍然保留**自己那条 MA20 —— 持有它就要看自己的数；
/// 市场观察提供的是**广度**背景：今天是几千只一起跌，还是就它一只跌，含义完全不同。
/// </summary>
public sealed record MarketWatchItem(
    DateTime Date,
    string Text);
