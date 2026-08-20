namespace StockPlatform.Logic.Models;

/// <summary>某只股票某报告期的一位前十大股东（<see cref="Kind"/>=<c>total</c>，占总股本）或前十大流通股东
/// (<see cref="Kind"/>=<c>float</c>，占流通股)——两类共用一张表，用 Kind 区分。均来自新浪股本股东页。</summary>
public class TopShareholderRow
{
    public const string KindTotal = "total";   // 十大股东（占总股本）
    public const string KindFloat = "float";   // 十大流通股东（占流通股）

    public string Code { get; set; } = "";        // 6 位股票代码
    public DateTime ReportDate { get; set; }       // 报告期
    public string Kind { get; set; } = "";         // total / float
    public int Rank { get; set; }                  // 名次 1..10
    public string HolderName { get; set; } = "";   // 股东名称
    public double Shares { get; set; }             // 持股数量（股）
    public double Ratio { get; set; }              // 占比（%）：total=占总股本，float=占流通股
    public string ShareType { get; set; } = "";    // 股本性质（如 流通A股/国有法人股）

    /// <summary>本期相对上期的持股变化方向：<see cref="ChangeUp"/>/<see cref="ChangeDown"/>，
    /// 无标记（不变、或本期新进榜）为 null。来源是新浪页面持股数后面的涨跌箭头
    /// （↑红=增持、↓绿=减持）——这个箭头以前会被解析函数当成脏字符导致整格解析失败、持股数
    /// 静默变成0（见 SinaShareholderProvider.ParseD 的注释），2026-08-13 修复时顺便把它存下来：
    /// 机构调仓方向本身就是有用信息，丢掉可惜。</summary>
    public string? ChangeDirection { get; set; }

    public const string ChangeUp = "增";
    public const string ChangeDown = "减";

    public DateTime FetchedAt { get; set; }
}
