namespace StockPlatform.Data.Orchestration;

public class FetchResult
{
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// 这一轮**本来就没什么可做**（不是失败，是已经齐了），2026-08-31 新增。
    ///
    /// 给计划里"空闲时"那类任务用：它们会在程序空闲时一轮轮反复跑，全部补齐之后要是还按
    /// 正常节奏每 20 分钟来一次，就是每次查一遍几 GB 的库、什么都没干。看到这个标记，
    /// 计划引擎会把下一次的间隔拉长到几小时（见 PlanRunner 的空闲调度）。
    /// 默认 false，其它调用方不受影响。
    /// </summary>
    public bool NothingToDo { get; set; }

    /// <summary>
    /// 这一轮**根本没开工**，以及为什么（2026-09-04）。跟 <see cref="NothingToDo"/> 是两回事：
    /// 那个是"活干完了没什么可做"，这个是"想干但现在干不了"（典型：数据源还在限流熔断里）。
    ///
    /// 为什么要单独一个字段：不填的话这种轮次会被记成 <see cref="RunOutcome.Ok"/>「完成」——
    /// 界面上显示成绿勾"09:25 完成"，可它一行数据都没抓；更要命的是
    /// <see cref="FetchPlanItem.AlreadyRanOn"/> 只认 Ok，于是**今天就不会再跑了**。
    /// 填了它，计划引擎会记成 <see cref="RunOutcome.Skipped"/>，今天还有机会补。
    /// </summary>
    public string? SkippedReason { get; set; }

    /// <summary>
    /// 这一轮跑完之后的**存量进度**，一句话（2026-09-04）。比如"已抓 144/1000，待重试 6"。
    ///
    /// 为什么要它：像板块成分股这种跨好几轮才做得完的活，状态列只写"完成"是不够的——
    /// 完成的是这一轮，可全库到底攒到什么程度、还剩多少，人看不见就心里没底，
    /// 只能去翻日志。有了它，状态列和悬停提示都能直接显示。
    /// 不适用的任务不设，保持 null，界面照旧。
    /// </summary>
    public string? Progress { get; set; }

    /// <summary>
    /// 这一轮**整项失败了**（2026-09-16）。跟 <see cref="Errors"/> 有几条是两回事：
    /// 逐只抓的任务常常"5500 只里 3 只失败"，那是完成里带几条错误，不该标红；
    /// 这个字段说的是"这一项这一轮没干成"。
    ///
    /// 为什么非要加：新式任务（<c>FetchTaskBase</c>）把异常**吞在骨架里**、翻译成
    /// <see cref="Scheduling.Tasks.TaskState.Failed"/> 返回，而 <c>PlanRunner</c> 只有
    /// "抛异常"那条路才记 <see cref="RunOutcome.Failed"/>。两边一对接，任务明明失败了，
    /// 状态列显示的却是绿色的"完成，但有 1 条错误"——【分档资金流快照】抓不到一行时正是如此，
    /// 而它恰恰是全库最不能静默失败的一项（漏一天就永久没了）。
    /// 老编排层的任务不设这个字段，行为完全不变。
    /// </summary>
    public bool Failed { get; set; }
}

/// <summary>See <see cref="FetchOrchestrator.GetDataStatus"/>.</summary>
public class DataStatus
{
    public DateTime? EarliestDay { get; set; }
    public DateTime? LatestDay { get; set; }
    public DateTime? LastFetchAt { get; set; }
    public string? LastFetchKind { get; set; }

    /// <summary>
    /// 各个任务最近一次跑完的时间（2026-09-02 新增，随【拉取全部】拆成 13 个原子项）。
    /// 按时间倒序。拆细之后"上次抓取"只剩最后收尾的那一项，光看它没法回答
    /// "融资余额今天补了没""名册什么时候刷新的"——这份明细就是补这个的。
    /// </summary>
    public IReadOnlyList<TaskRunLine> RecentTaskRuns { get; set; } = [];
}

/// <summary>见 <see cref="DataStatus.RecentTaskRuns"/>。</summary>
public sealed record TaskRunLine(string Task, DateTime At, int ErrorCount);
