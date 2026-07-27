namespace StockPlatform.Logic.Models;

/// <summary>指数的一只成分股（新浪指数成分接口）——只存"指数→成分股"的归属关系，成分股名称/行情由
/// 分析程序用本地 StockMeta/日K 现查（跟 BoardMember 同样的思路，不存易过期的成分行情）。</summary>
public class IndexConsRow
{
    public string IndexCode { get; set; } = "";   // 6 位指数代码，如 000300
    public string StockCode { get; set; } = "";   // 6 位成分股代码（已去 sh/sz/bj 前缀）
    public DateTime? InDate { get; set; }          // 纳入日期（新浪成分表第3列，可空）——仅供单向时点过滤，不能重建历史成分
    public DateTime FetchedAt { get; set; }
}
