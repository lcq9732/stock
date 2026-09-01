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
}

/// <summary>See <see cref="FetchOrchestrator.GetDataStatus"/>.</summary>
public class DataStatus
{
    public DateTime? EarliestDay { get; set; }
    public DateTime? LatestDay { get; set; }
    public DateTime? LastFetchAt { get; set; }
    public string? LastFetchKind { get; set; }
}
