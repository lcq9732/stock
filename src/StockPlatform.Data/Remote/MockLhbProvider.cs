using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的龙虎榜源（2026-09-18）——一个请求都不发，按交易日凭空造行。
/// 照 <see cref="MockBarFetcher"/> 抽的，安全边界也一样（只在 DEBUG 构建里注册 + Debug 数据目录隔离）。
///
/// ⚠ **故意只实现 <see cref="ILhbProvider"/>、不实现 <c>ILhbRangeProvider</c>**：
/// 实现了的话 <c>LhbTask</c> 会走"按月切片"那条路，而【拉取历史区间】和补残缺日走的都是
/// 逐日路径——要模拟的正是后者。少一个接口，两条路就都落在逐日入口上。
/// </summary>
public sealed class MockLhbProvider : ILhbProvider
{
    /// <summary>每天造几行。</summary>
    public const int RowsPerDay = 4;

    public event Action<string>? OnStatus;

    /// <summary>跟东财源一致：这份数据最早到 2004-06-25。</summary>
    public DateOnly EarliestAvailable { get; init; } = new(2004, 6, 25);

    /// <summary>这天（含）起返回 0 行。**默认是今天**——龙虎榜当晚才发布，
    /// 模拟源照着来，"当天的空不该定案"那条判据才验得到（见 <c>DailyNoDataGate</c>）。</summary>
    public DateOnly? EmptyFrom { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    public Task<List<LhbRow>> GetDailyAsync(DateOnly date, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || date < EarliestAvailable
            || (EmptyFrom is { } e && date >= e))
        {
            OnStatus?.Invoke($"[模拟源] 龙虎榜 {date:yyyy-MM-dd}：0 行，未发任何请求");
            return Task.FromResult(new List<LhbRow>());
        }

        var day = date.ToDateTime(TimeOnly.MinValue);
        var rows = new List<LhbRow>(RowsPerDay);
        for (int i = 0; i < RowsPerDay; i++)
        {
            // 前一半沪市、后一半深市——日频体检的"某市整天没有"判据要两市都见得到。
            string code = i < RowsPerDay / 2 ? $"6000{i:D2}" : $"0000{i:D2}";
            rows.Add(new LhbRow
            {
                TradeDate = day,
                StockCode = code,
                StockName = $"模拟{i}",
                ClosePrice = 11.11,
                Reason = "日涨幅偏离值达到7%的前5只证券",
                FetchedAt = day.AddHours(20),
            });
        }
        OnStatus?.Invoke($"[模拟源] 龙虎榜 {date:yyyy-MM-dd}：造了 {rows.Count} 行，未发任何请求");
        return Task.FromResult(rows);
    }
}
