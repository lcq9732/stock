using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取某个交易日的龙虎榜（新浪龙虎榜每日汇总，较稳）——非交易日/无数据返回空列表。
/// 按交易日拉，供"拉取龙虎榜"当天抓取或按日期区间回补历史。</summary>
public interface ILhbProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某个交易日的龙虎榜记录（同股同日可能多条，按上榜指标区分）。</summary>
    Task<List<LhbRow>> GetDailyAsync(DateOnly date, CancellationToken ct = default);
}
