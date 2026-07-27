namespace StockPlatform.Logic.Models;

/// <summary>某只股票某报告期的股东户数（新浪"股本股东-主要股东"页里的"股东总数"）——即用户说的"股东
/// 数量"。<see cref="AvgShares"/> 是对应的户均持股数（按总股本计算），一起顺带存下来。</summary>
public class ShareholderCountRow
{
    public string Code { get; set; } = "";        // 6 位股票代码
    public DateTime ReportDate { get; set; }       // 报告期（截至日期）
    public long HolderNum { get; set; }            // 股东户数（股东总数）
    public double AvgShares { get; set; }          // 户均持股数（股）
    public DateTime FetchedAt { get; set; }
}
