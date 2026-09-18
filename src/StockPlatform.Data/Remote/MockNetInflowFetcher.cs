using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的资金净流入源（2026-09-18）——一个请求都不发，按请求区间里的工作日凭空造行。
/// 照 <see cref="MockBarFetcher"/> 抽的，安全边界也一样（只在 DEBUG 构建里注册 + Debug 数据目录隔离）。
///
/// 加它是为了让【拉取历史区间】那条路能整条离线跑通：那一轮会连着抓
/// 个股/指数/ETF日K、资金净流入、融资余额、龙虎榜、中标公告，
/// 只要有一类没有模拟源，"验证不发真请求"就做不到（2026-09-18 就因此漏发过真请求）。
/// </summary>
public sealed class MockNetInflowFetcher : INetInflowFetcher
{
    /// <summary>造出来的净流入（元）。取个一眼就知道是假数据的整数。</summary>
    public const double Amount = 1_111_111;

    public event Action<string>? OnStatus;

    /// <summary>跟真源一致：这份数据最早到 2010-03-01（见 <c>INetInflowFetcher.EarliestAvailable</c>）。</summary>
    public DateOnly EarliestAvailable { get; init; } = new(2010, 3, 1);

    public Task<List<NetInflow>> FetchAsync(
        string code, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var from = (start ?? DateTime.Today.AddDays(-30)).Date;
        var to = (end ?? DateTime.Today).Date;
        var floor = EarliestAvailable.ToDateTime(TimeOnly.MinValue);
        if (from < floor) from = floor;

        var rows = new List<NetInflow>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            // 周末不返回：造出来反而会让体检和复查对不上（同 MockBarFetcher）。
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            rows.Add(new NetInflow
            {
                Code = code,
                PeriodStart = d,
                MainNetInflow = Amount,
                FetchedAt = d.AddHours(20),   // 盘后，免得被"盘中固化"判据盯上
            });
        }
        OnStatus?.Invoke($"[模拟源] 资金净流入 {code} 造了 {rows.Count} 行，未发任何请求");
        return Task.FromResult(rows);
    }
}
