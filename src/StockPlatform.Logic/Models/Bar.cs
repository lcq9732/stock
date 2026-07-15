namespace StockPlatform.Logic.Models;

/// <summary>
/// Supported bar granularities. New periods can be added here without touching the
/// Bar table schema — see doc/data-platform-design.md section 4.
/// </summary>
public static class Granularity
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";
    public const string Min1 = "min1";
    public const string Min5 = "min5";
    public const string Min15 = "min15";
    public const string Min30 = "min30";
    public const string Min60 = "min60";
}

/// <summary>One row of the multi-granularity Bar table (see doc/data-platform-design.md section 4).</summary>
public class Bar
{
    public string Code { get; set; } = "";
    public string Granularity { get; set; } = "";
    public DateTime PeriodStart { get; set; }
    public double Open { get; set; }
    public double Close { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Volume { get; set; }
    public double Amount { get; set; }
    // 涨跌幅不再作为字段存储/传递——它是收盘价的派生值，一律在需要处用相邻收盘价现算
    // （见 doc §9.5 / doc/data-platform-design.md 2026-07-14 变更记录）。
    public double Turnover { get; set; }

    /// <summary>实际抓取到这一天数据的时间（墙钟时间，不是交易日日期）——2026-07-09新增，用来
    /// 判断"今天"这一天是不是盘中抓的（可能还会变）还是收盘后抓的（已经是最终数据，以后不用再
    /// 抓）。历史上早于这天入库的行没有这个值，读出来是 <see cref="DateTime.MinValue"/>（永远
    /// 判定为"未确认"，直到下次被重新抓到一次），见 FetchOrchestrator.IsConfirmedFinal。</summary>
    public DateTime FetchedAt { get; set; }
}
