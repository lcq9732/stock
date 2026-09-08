namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 交易日历的**官方来源**（2026-09-08）。眼下只有深交所官网一家实现
/// （<c>SzseTradingCalendarProvider</c>）——上交所试过两个 query 接口，一个返回空壳、
/// 一个直接 <c>SOA service is null</c>，没有可用的结构化历史日历。
/// </summary>
public interface ITradingCalendarProvider
{
    event Action<string>? OnStatus;

    /// <summary>这个源给得到的最早月份。更早的月份由本地K线归纳补上，见 <see cref="ILocalTradingDaySource"/>。</summary>
    DateOnly EarliestMonth { get; }

    /// <summary>
    /// 某个月的**交易日**（非交易日不返回）。空列表＝这个月还没发布/超出覆盖范围，**不是错误**；
    /// 请求失败要抛异常。两者必须分得开——否则"网络抽风"会被当成"这个月没有交易日"写进日历。
    /// </summary>
    Task<List<DateOnly>> GetMonthAsync(int year, int month, CancellationToken ct = default);
}
