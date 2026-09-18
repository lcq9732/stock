namespace StockPlatform.Logic.Models;

/// <summary>
/// 一只票的分红送配抓取状态（2026-09-18）——【拉取分红送配】的水位线，对应 DividendFetchState 表。
///
/// 为什么 <see cref="DividendRow"/> 那张表当不了水位线：没分红的票一行都不写，
/// "没抓过"和"抓过、确实没分红"长得一模一样。见 doc/dividend-task-design.md §4。
/// </summary>
public class DividendFetchState
{
    /// <summary>6位股票代码（含退市股）。</summary>
    public string Code { get; set; } = "";

    /// <summary>上次**抓成功**的时刻；从没成功过为 null。失败不更新它——失败不算抓过。</summary>
    public DateTime? LastOkAt { get; set; }

    /// <summary>上次成功时拿到几条分红。0 = 那次确实没有（不是"以后别再抓"）。</summary>
    public int DividendRows { get; set; }

    /// <summary>上次成功时拿到几条配股。</summary>
    public int RightsRows { get; set; }

    /// <summary>上次失败的时刻（诊断用）。</summary>
    public DateTime? LastFailAt { get; set; }

    /// <summary>上次失败的原因（诊断用）。</summary>
    public string? FailReason { get; set; }

    /// <summary>这条状态是不是已经过期、该重抓了。<paramref name="cutoff"/> 之前抓的算旧。</summary>
    public bool IsStale(DateTime cutoff) => LastOkAt is null || LastOkAt.Value < cutoff;
}
