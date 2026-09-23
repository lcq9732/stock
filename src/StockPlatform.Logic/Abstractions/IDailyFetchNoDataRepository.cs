namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 「抓过、确认这个源这天就是没有」的名单（DailyFetchNoData 表，2026-09-08）。
///
/// 谁在用：龙虎榜、融资余额这类**整天一次性返回**的日频数据。它们的回补是逐日发请求的，
/// 不记下"确认没有"的日子，每轮都要把那些日子重试一遍（龙虎榜从 2002 年补一轮就是几百个空请求）。
///
/// 跟交易日历是**两道闸**：日历挡掉非交易日，这张表挡掉"是交易日、但这个源上确实没有"的日子
/// （新浪龙虎榜早年那几年）。
///
/// ⚠ 写入判据在调用方（见 <c>FetchOrchestrator.BackfillDailyAsync</c>），三条缺一不可：
/// 正常返回的空（不是异常）、骨架校验通过（不是空壳反爬页）、日期在 3 天以前（不是还没发布）。
/// </summary>
public interface IDailyFetchNoDataRepository
{
    /// <summary>龙虎榜（Lhb 表）。</summary>
    const string LhbDataset = "Lhb";

    /// <summary>融资余额（MarginDetail 表）。</summary>
    const string MarginDataset = "MarginDetail";

    /// <summary>上交所 ETF 份额（EtfShare 表 market='sh'，2026-09-23）。上交所 2012-01-04 起才有数据。</summary>
    const string EtfShareDataset = "EtfShare";

    /// <summary>深交所 ETF 份额（EtfShare 表 market='sz'，2026-09-23）。深交所 2016-09-26 起才有数据。</summary>
    const string EtfShareSzDataset = "EtfShareSz";

    /// <summary>某个市场的 ETF 份额用哪个数据集名。沪市沿用最早那个名字，已记的空日不作废。</summary>
    static string EtfShareDatasetOf(string market) => market == "sz" ? EtfShareSzDataset : EtfShareDataset;

    void EnsureSchema();

    /// <summary>某个数据集已确认没有数据的全部日子。</summary>
    HashSet<DateOnly> GetConfirmed(string dataset);

    /// <summary>记下"这天确认没有"。</summary>
    void Confirm(string dataset, DateOnly day);

    /// <summary>撤销一天——数据源后来补上了（见 Case 6：自愈）。</summary>
    void Remove(string dataset, DateOnly day);

    /// <summary>清空某个数据集的全部结论，返回清掉几条。「彻底体检」用。</summary>
    int Clear(string dataset);

    int Count(string dataset);
}
