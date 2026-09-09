using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取某个交易日的龙虎榜（新浪龙虎榜每日汇总，较稳）——非交易日/无数据返回空列表。
/// 按交易日拉，供"拉取龙虎榜"当天抓取或按日期区间回补历史。</summary>
public interface ILhbProvider
{
    event Action<string>? OnStatus;

    /// <summary>这份数据**最早存在**的那一天——早于它的日子源上根本没有，请求也是白发。
    /// 回补的起点会被抬到这一天（见 FetchOrchestrator.BackfillDailyAsync）。</summary>
    DateOnly EarliestAvailable { get; }

    /// <summary>抓某个交易日的龙虎榜记录（同股同日可能多条，按上榜指标区分）。</summary>
    Task<List<LhbRow>> GetDailyAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>
/// 能**整段**抓的龙虎榜源（2026-09-09）——东财按月切片一次拿一个月，新浪只能一天一个页面。
///
/// 为什么要多这么一个接口而不是让调用方去 <c>is EastMoneyLhbProvider</c>：请求数差着一个量级，
/// 而这个差别决定了功能可不可行。全量回补 2004 年至今：逐日入口 5300 个请求约 1.8 小时，
/// 月片入口约 580 个、十几分钟；日常增量要回看一个月补滞后字段，逐日是 20+ 个请求，月片是 2 个。
/// 所以调用方**支持就该走这条**，用接口把这件事摆到明面上。
/// </summary>
public interface ILhbRangeProvider
{
    /// <summary>按月切片抓一段，每片就绪时回调一次由调用方落库；返回累计写入行数。</summary>
    Task<int> FetchRangeAsync(
        DateTime start, DateTime end, Func<List<LhbRow>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default);
}
