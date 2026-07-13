namespace StockPlatform.Logic.Models;

/// <summary>板块类型：概念/题材板块 vs 行业板块（新浪的两套分类）。</summary>
public enum BoardType
{
    Concept,   // 概念/题材（如"华为汽车""固态电池"）
    Industry,  // 行业（新浪行业分类）
}

/// <summary>
/// 一个板块在某一时刻的行情快照（新浪板块接口）——板块自己的涨跌幅/成交额是数据商算好的板块口径，
/// 我们本地没法准确复算，所以直接存下来。成分股仅存代码（MemberCodes），名称/最新行情由分析程序
/// 用本地的 StockMeta / 日K 现查，避免存一份易过期的成分股行情。
/// </summary>
public class Board
{
    public string BoardCode { get; set; } = "";   // 新浪板块代码，如 gn_hwqc / new_blhy
    public BoardType Type { get; set; }
    public string Name { get; set; } = "";
    public int MemberCount { get; set; }
    public double ChangePct { get; set; }          // 板块涨跌幅（%）
    public double Amount { get; set; }             // 板块合计成交额（元）
    public string LeaderCode { get; set; } = "";   // 领涨股代码（已去掉 sh/sz/bj 前缀）
    public string LeaderName { get; set; } = "";
    public DateTime AsOf { get; set; }             // 这份快照的抓取时刻

    /// <summary>成分股代码列表——只在抓取/写入时用来落 BoardMember 表，读出来的 Board 不填。</summary>
    public List<string> MemberCodes { get; set; } = new();
}
