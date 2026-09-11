namespace StockPlatform.Logic.Models;

/// <summary>
/// 一条观察项——"这只票的这件事要盯着"。见 doc/watch-item-design.md §4.2。
///
/// ⚠ **L0/L1/L2 三层全部落在这一个类型里**，靠 <see cref="Origin"/> 区分来源。
/// 三层不是三张表：<c>PlanAnnouncement</c> 和 <c>StockWatchIndicator</c> 不是"某一层"，
/// 它们是支撑 L1 干活所需的两块数据（见设计文档 §2.1「四类数据别混」）。
/// </summary>
public sealed class WatchItem
{
    public Guid ItemId { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>L0/L1/L2，见 <see cref="WatchLayer"/>。只是标签，行为由 <see cref="Origin"/> 决定。</summary>
    public string Layer { get; set; } = WatchLayer.L1;

    /// <summary>
    /// **硬边界**，见 <see cref="WatchOrigin"/>：派生项每轮整组重建，手写项任何规则都不许碰。
    /// 不划这条线，规则跑一次就把人写的冲掉了——跟 <c>StockWatchIndicator</c> 那个坑同病。
    /// </summary>
    public string Origin { get; set; } = WatchOrigin.Derived;

    /// <summary>主动仓/底仓/无仓位，见 <see cref="PositionKind"/>。两层的信号不能混，否则互污。</summary>
    public string Position { get; set; } = PositionKind.None;

    /// <summary>取值器类型，见 <see cref="WatchKind"/>——决定谁去取这个值。</summary>
    public string Kind { get; set; } = "";

    /// <summary>取值参数：指标码、财报科目名、MA 周期…由 <see cref="Kind"/> 解释。</summary>
    public string Expr { get; set; } = "";

    /// <summary>比较算子，见 <see cref="WatchOp"/>。</summary>
    public string Op { get; set; } = WatchOp.Lt;

    public double? Threshold { get; set; }

    /// <summary>A=推送 / B=进日报 / C=只落库。</summary>
    public string Priority { get; set; } = "B";

    public bool Enabled { get; set; } = true;

    /// <summary>L2 才有：一句话论点。全文写在 <c>notes/{code}.md</c> 里，这里只存摘要。</summary>
    public string? Thesis { get; set; }

    /// <summary>为什么挂它（派生项写规则名，手写项写人话）。</summary>
    public string Reason { get; set; } = "";

    public DateTime CreatedDate { get; set; } = DateTime.Now;
}

public static class WatchLayer
{
    /// <summary>兜底：所有票都有的法定事件。</summary>
    public const string L0 = "L0";
    /// <summary>派生：按画像自动挂/摘。</summary>
    public const string L1 = "L1";
    /// <summary>手写：只有人知道的论点。</summary>
    public const string L2 = "L2";
}

/// <summary>
/// <see cref="WatchItem.Origin"/> 的取值——**这是整套设计的硬边界**。
/// 跟 <see cref="WatchIndicatorOrigin"/> 是同一条线的第二次出现。
/// </summary>
public static class WatchOrigin
{
    /// <summary>L0 兜底，规则整组重建。</summary>
    public const string Builtin = "兜底";
    /// <summary>L1 派生，规则整组重建。</summary>
    public const string Derived = "派生";
    /// <summary>L2 手写，**任何规则都不许碰**。</summary>
    public const string Manual = "手写";
}

public static class PositionKind
{
    public const string Active = "主动仓";
    public const string Core = "底仓";
    public const string None = "无仓位";
}

/// <summary>取值器类型——决定 <c>WatchEvaluator</c> 去哪张表取值。见设计文档 §4.3。</summary>
public static class WatchKind
{
    /// <summary>回购等方案的 stage 跃迁。Expr=方案类型（回购/定增/重组）。</summary>
    public const string PlanStage = "PlanStage";
    /// <summary>行业指标。Expr=indicator_id。⚠ 值是自然日序列，见设计文档 §4.3 的口径坑。</summary>
    public const string IndustryIndicator = "IndustryIndicator";
    /// <summary>财报科目的单季环比。Expr=metric_key。</summary>
    public const string FinMetricQoQ = "FinMetricQoQ";
    /// <summary>两个财报科目的比值（%）。Expr="a/b"。</summary>
    public const string FinMetricRatio = "FinMetricRatio";
    /// <summary>价格相对均线。Expr=MA 周期。</summary>
    public const string PriceMA = "PriceMA";
    /// <summary>融资余额环比（%）。</summary>
    public const string MarginBalance = "MarginBalance";
    /// <summary>股东户数环比（%）。</summary>
    public const string HolderCount = "HolderCount";
    /// <summary>法定事件表。Expr=表名。</summary>
    public const string EventTable = "EventTable";
    /// <summary>
    /// **无数据源**，到点提醒人去查。见设计文档 §4.3：这是必要的诚实出口，不是偷懒——
    /// 有些关键变量库里确实没有（如月度动力电池装车份额）。
    /// 让它显式存在，好过假装能自动判、或者把它排除在系统之外让人忘掉。
    /// ⚠ 只产出"该去查了"的提醒，**不产出"已触发"的结论**。
    /// </summary>
    public const string Manual = "Manual";
}

public static class WatchOp
{
    public const string Lt = "lt";
    public const string Gt = "gt";
    public const string CrossDown = "cross_down";
    public const string CrossUp = "cross_up";
    /// <summary>stage 发生了跃迁就触发（不比阈值）。</summary>
    public const string StageChange = "stage_change";
    /// <summary>只提醒，不判定。<see cref="WatchKind.Manual"/> 专用。</summary>
    public const string Remind = "remind";
}

/// <summary>一次触发。只增不删——它是"当时确实报过"的证据。</summary>
public sealed class WatchHit
{
    public Guid HitId { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>触发所依据的那个值**所属的交易日**，不是求值那天。</summary>
    public DateTime TriggerTradeDate { get; set; }

    public double? ObservedValue { get; set; }

    /// <summary>人读的一句话：发生了什么。</summary>
    public string Message { get; set; } = "";

    public string Priority { get; set; } = "B";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
