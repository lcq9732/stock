namespace StockPlatform.Logic.Models;

/// <summary>
/// 方案类公告的一条进展记录（回购/定增/重组），见 doc/watch-item-design.md §4.1。
///
/// ⚠ **这是事实表，只增不删**。方案结束了要摘掉的是观察项（<c>WatchItem</c>），不是这里的行——
/// 删了就没法回溯"当初方案说的上限是 573"、也没法对账"进展公告拖了几个月"。
/// 「事实只增、待办才有增删」是这套设计的一条基本分界，见设计文档 §2.1。
/// </summary>
public sealed class PlanAnnouncement
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>见 <see cref="PlanKind"/>。</summary>
    public string Kind { get; set; } = PlanKind.Buyback;

    /// <summary>公告日（方案标识的一半）。</summary>
    public DateTime AnnounceDate { get; set; }

    /// <summary>见 <see cref="PlanStage"/>。**必须独立成列**：「方案」的金额上限和「进展」的累计金额是两个量纲。</summary>
    public string Stage { get; set; } = PlanStage.Progress;

    /// <summary>
    /// 数据截止的那一天——**不是公告日**。月度进展公告说的是"截至上月末"，
    /// 按 <c>feedback_trading_date_not_write_date</c>，值所属日期要单独记。
    /// 抽不到就是 null（比方案公告本来就没有这个概念）。
    /// </summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>累计回购股数。**0 和 null 不是一回事**：0＝公告明说"尚未实施"，null＝没抽到。</summary>
    public double? CumShares { get; set; }

    /// <summary>累计支付金额（元，不含交易费用）。</summary>
    public double? CumAmount { get; set; }

    /// <summary>区间最低成交价（元/股）。</summary>
    public double? PriceLow { get; set; }

    /// <summary>区间最高成交价（元/股）。</summary>
    public double? PriceHigh { get; set; }

    /// <summary>累计已回购占总股本比例（%）。</summary>
    public double? PctOfCapital { get; set; }

    /// <summary>方案的回购价格上限（元/股）——只有 <see cref="PlanStage.Proposal"/> 才有。</summary>
    public double? PlanCapPrice { get; set; }

    /// <summary>方案的资金下限/上限（元）——只有 <see cref="PlanStage.Proposal"/> 才有。</summary>
    public double? PlanAmountLow { get; set; }
    public double? PlanAmountHigh { get; set; }

    public string Title { get; set; } = "";
    public string ArtCode { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public DateTime FetchedAt { get; set; } = DateTime.Now;
}

/// <summary>方案类型。目前只做回购，定增/重组留位——它们的进展披露节奏完全不同，不能共用一套抽取规则。</summary>
public static class PlanKind
{
    public const string Buyback = "回购";
    public const string PrivatePlacement = "定增";
    public const string Restructuring = "重组";
}

/// <summary>
/// 方案的生命周期阶段。**L1 能自动摘除观察项，全靠这一列**——
/// 不把生命周期落成结构化状态，程序就永远不知道回购已经结束、该把那条待办撤下来
/// （见 doc/watch-item-design.md §2）。
/// </summary>
public static class PlanStage
{
    /// <summary>方案公告/回购报告书——带价格上限和资金区间。</summary>
    public const string Proposal = "方案";

    /// <summary>首次回购——**这就是"到底买没买"的那个信号**，法定次一交易日披露。</summary>
    public const string FirstBuy = "首次回购";

    /// <summary>月度进展（每月前三个交易日披露截至上月末）。</summary>
    public const string Progress = "进展";

    /// <summary>累计每增加总股本的 1% 时披露。</summary>
    public const string Milestone = "达标";

    /// <summary>实施完毕/期限届满。← L1 看到它就摘掉观察项。</summary>
    public const string Done = "完毕";

    /// <summary>终止回购。同样是摘除条件。</summary>
    public const string Terminated = "终止";
}
