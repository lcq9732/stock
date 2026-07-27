namespace StockPlatform.Logic.Models;

/// <summary>龙虎榜的一条记录（新浪龙虎榜按交易日的每日汇总）——同一只股票同一天可能因多个"上榜指标"
/// (<see cref="Reason"/>)分别上榜，所以主键是 (trade_date, stock_code, reason)。按日累积保留历史
/// (INSERT OR IGNORE)，不覆盖。</summary>
public class LhbRow
{
    public DateTime TradeDate { get; set; }        // 交易日
    public string StockCode { get; set; } = "";    // 6 位股票代码
    public string StockName { get; set; } = "";
    public double ClosePrice { get; set; }         // 收盘价
    public double Deviation { get; set; }          // 对应值（涨跌幅/偏离值，随上榜指标而定）
    public double Volume { get; set; }             // 成交量
    public double Amount { get; set; }             // 成交额
    public string Reason { get; set; } = "";       // 上榜指标（如"日涨幅偏离值达7%的证券"）
    public DateTime FetchedAt { get; set; }
}
