namespace StockPlatform.Logic.Models;

/// <summary>
/// One trading day's 资金净流入（元）for a stock — a separate dataset/endpoint from Bar (OHLCV).
/// Kept as its own table/model rather than extra columns on Bar since it's fetched from a
/// different endpoint on its own schedule/watermark, not bundled with K线. Exact composition
/// depends on which INetInflowFetcher populated it: the default SinaNetInflowFetcher uses Sina's
/// `netamount`（全部单量级净流入之和）; the alternate EastMoneyNetInflowFetcher uses EastMoney's
/// f52（主力净流入，仅大单+超大单）— see doc/data-platform-design.md section 3.7 for why Sina is
/// the default (EastMoney is largely unreachable in this user's real network).
/// </summary>
public class NetInflow
{
    public string Code { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public double MainNetInflow { get; set; }

    /// <summary>实际抓取到这一天数据的时间（墙钟时间）——2026-07-09新增，跟 Bar.FetchedAt 同样的
    /// 用途：判断"今天"这一天是不是收盘后确认的最终数据，见 FetchOrchestrator.IsConfirmedFinal。</summary>
    public DateTime FetchedAt { get; set; }
}
