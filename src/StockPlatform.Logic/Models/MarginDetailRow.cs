namespace StockPlatform.Logic.Models;

/// <summary>某只标的某交易日的融资融券明细（交易所官方数据）——核心是<see cref="MarginBalance"/>融资余额。
/// 上交所(JSON)/深交所(xlsx)字段不完全一致，这里取两所共有的核心项统一存。按交易日累积(INSERT OR
/// IGNORE)，跟 K线/龙虎榜一样是每日数据。</summary>
public class MarginDetailRow
{
    public string Code { get; set; } = "";        // 6 位标的代码
    public DateTime TradeDate { get; set; }        // 交易日
    public string Name { get; set; } = "";
    public double MarginBalance { get; set; }      // 融资余额（元）
    public double MarginBuy { get; set; }          // 融资买入额（元）
    public double ShortBalance { get; set; }       // 融券余额（元）
    public double ShortVolume { get; set; }        // 融券余量（股/份）
    public DateTime FetchedAt { get; set; }
}
