namespace StockPlatform.Logic.Models;

/// <summary>一只已终止上市的A股（来自交易所官网终止上市名单）。</summary>
public class DelistedStockRow
{
    /// <summary>6位A股代码。</summary>
    public string Code { get; set; } = "";
    /// <summary>终止上市时的证券简称（如"乐视退"）。</summary>
    public string Name { get; set; } = "";
    /// <summary>sse=上交所, szse=深交所。</summary>
    public string Exchange { get; set; } = "";
    public DateTime? ListDate { get; set; }
    /// <summary>终止上市日。上交所部分行（转板/吸收合并等）该字段缺失为 null。</summary>
    public DateTime? DelistDate { get; set; }
}
