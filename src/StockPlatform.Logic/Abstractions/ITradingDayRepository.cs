namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 交易日历的本地存取（TradingDay 表，2026-09-08）——全库公共设施。
///
/// 两个来源：2005-01 起是深交所官方接口（<see cref="SzseSource"/>），2004-12 及以前从本地
/// 全市场K线归纳（<see cref="LocalSource"/>，深交所接口对那段返回空）。建表注释里有完整理由。
/// </summary>
public interface ITradingDayRepository
{
    /// <summary>深交所官网接口。</summary>
    const string SzseSource = "szse";

    /// <summary>本地全市场K线归纳（只用于 2004-12 及以前那段死历史）。</summary>
    const string LocalSource = "local";

    void EnsureSchema();

    /// <summary>写入/覆盖一批交易日（同一天重复写按 source 覆盖，官方值可以纠正归纳值）。</summary>
    void Upsert(IEnumerable<(DateOnly Day, string Source)> days);

    /// <summary>全部交易日，升序。</summary>
    List<DateTime> GetAll();

    /// <summary>已知最早/最晚的交易日；表空时都是 null。用来跟 Bar 的 MIN/MAX 对账、决定补哪一头。</summary>
    (DateTime? Min, DateTime? Max) GetRange();

    /// <summary>某个来源的日子有多少天（给日志和自检用）。</summary>
    int Count(string? source = null);

    /// <summary>某一段里已有的交易日（闭区间），用于跟官方结果比对。</summary>
    HashSet<DateOnly> GetBetween(DateOnly from, DateOnly to);
}
