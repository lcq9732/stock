using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Fetches raw bars for one stock/granularity from a remote data source (e.g. EastMoney).</summary>
public interface IBarDataFetcher
{
    Task<(string Name, List<Bar> Bars)> FetchAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default);

    /// <summary>
    /// 本数据源能否提供**后复权**日线（<see cref="Granularity.DayHfq"/>）。默认 false——抓取流程据此
    /// 决定要不要跑 hfq 那一轮，而不是让不支持的数据源对着 5000 多只股票逐个抛异常刷屏。
    /// 目前只有腾讯支持（接口参数 hfq）；新浪/东财没有对应接口，选这两个数据源时回测用的后复权数据
    /// 就补不上，会在日志里提示一次。
    /// </summary>
    bool SupportsHfq => false;

    /// <summary>
    /// Fires for out-of-band status worth surfacing to the UI even though it isn't tied to any
    /// single stock — e.g. "rate limiter is intentionally pausing for N minutes". Without this,
    /// a long defensive pause is indistinguishable in the log from the program having hung.
    /// </summary>
    event Action<string>? OnStatus;
}
