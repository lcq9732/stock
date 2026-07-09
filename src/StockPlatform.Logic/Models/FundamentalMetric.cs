namespace StockPlatform.Logic.Models;

/// <summary>Common metric_key values. New keys can be added without a schema change.</summary>
public static class MetricKeys
{
    public const string Revenue = "revenue";
    public const string NetProfit = "net_profit";
    public const string Roe = "roe";
    public const string Eps = "eps";
    public const string Bvps = "bvps";
    public const string Pe = "pe";
    public const string Pb = "pb";

    /// <summary>流通市值（不含限售股），单位固定是"元"（不是"万元"/"亿元"）——跟 Bar 里价格字段
    /// 一样用最小单位存，换算成"亿"只在展示/比较阈值时做（见
    /// MidCapPullbackAnalysisEngine：80亿=8_000_000_000）。这个 key 目前还没有任何数据获取程序
    /// 写入过（FundamentalMetric 表本身早就建好了，但从来是空的）——是为将来数据获取程序接入
    /// 市值/流通股本数据预留的写入点，Analyzer 这边已经按"读到就用，读不到就跳过"的方式实现了
    /// 消费端（见 MidCapPullbackAnalysisEngine 的 Error 处理），数据获取程序那边只需要按
    /// IFundamentalMetricRepository.InsertOrIgnore 写入 { Code, MetricKey = CirculatingMarketCap,
    /// AsOfDate = 交易日, Value = 流通市值(元), Source, FetchedAt } 即可接上，不需要改 Analyzer。</summary>
    public const string CirculatingMarketCap = "circulating_market_cap";
}

/// <summary>One row of the key-value FundamentalMetric table (see doc/data-platform-design.md section 4).</summary>
public class FundamentalMetric
{
    public string Code { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public DateTime AsOfDate { get; set; }
    public double Value { get; set; }
    public string Source { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}
