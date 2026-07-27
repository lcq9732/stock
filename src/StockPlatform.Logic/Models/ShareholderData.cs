namespace StockPlatform.Logic.Models;

/// <summary>一只股票的全部股东数据聚合（一次抓取返回的历年各报告期）——户数序列 + 十大股东/十大流通
/// 股东明细。<see cref="IShareholderProvider"/> 返回它，仓储按 code 整体覆盖写入两张表。</summary>
public class ShareholderData
{
    public List<ShareholderCountRow> Counts { get; set; } = new();
    public List<TopShareholderRow> TopHolders { get; set; } = new();   // 含 total 和 float 两类
}
