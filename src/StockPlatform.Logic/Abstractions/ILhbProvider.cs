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
    /// <summary>
    /// 按月切片抓一段，**每片 yield 一次**由调用方落库。
    ///
    /// 2026-09-17 从"回调式 onBatch"改成异步枚举：新任务框架的 <c>FetchAsync</c> 要的就是
    /// "抓一批产出一批"，回调式的话落库时机被压在 provider 里，骨架的 MaxItems / Deadline
    /// 两个上限就都插不进去。
    /// </summary>
    IAsyncEnumerable<LhbSlice> FetchSlicesAsync(
        DateTime start, DateTime end, CancellationToken ct = default);
}

/// <summary>一个月片的结果——行 + 这一片覆盖的区间（报进度、判空日都要）。</summary>
/// <param name="Start">片的起始日（含）。</param>
/// <param name="End">片的结束日（含）。</param>
/// <param name="Name">片名，形如 "2026-09"，报进度用。</param>
/// <param name="Index">第几片（从 1 起）。</param>
/// <param name="Total">共几片。</param>
/// <param name="Rows">这一片抓到的行。</param>
public sealed record LhbSlice(
    DateTime Start, DateTime End, string Name, int Index, int Total, List<LhbRow> Rows);
