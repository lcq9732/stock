using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的流通市值源（2026-09-18）。
///
/// 照真源（<c>SinaListMarketCapFetcher</c>）的形状：一次扫回全市场列表，顺带带出市值，
/// 所以 <see cref="MarketCapFetchResult.AllStocks"/> 有值、调用方据此判定"名册已整体刷新"。
///
/// <see cref="QuotesAreLive"/> 默认 false——模拟**盘后/周末**那一档，
/// 这样"as_of_date 要归到上一个交易日"那条判据才验得到（见 <c>RosterMarketCapTask</c>）。
/// </summary>
public sealed class MockMarketCapFetcher : IMarketCapFetcher
{
    /// <summary>一眼就知道是假数据的流通市值（元）。</summary>
    public const double Cap = 1_111_111_111;

    public event Action<string>? OnStatus;

    /// <summary>扫描回来的全市场名单。默认：本地已知的那些 + 2 只"新股"。</summary>
    public Func<IReadOnlyList<string>, List<(string Code, string Name)>>? Roster { get; init; }

    /// <summary>行情是不是实时的（true＝今天已开盘）。默认 false＝盘后/周末。</summary>
    public bool? QuotesAreLive { get; init; }

    /// <summary>true＝整轮扫描抛异常，用来验"失败粒度是一轮"。</summary>
    public bool Throws { get; init; }

    public Task<MarketCapFetchResult> GetMarketCapsAsync(
        IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Throws) throw new InvalidOperationException("[模拟源] 流通市值整轮扫描按约定失败");

        var all = Roster?.Invoke(codes)
                  ?? codes.Select(c => (c, $"票{c}")).Concat([("600999", "模拟新股1"), ("000999", "模拟新股2")]).ToList();
        var known = codes.ToHashSet(StringComparer.Ordinal);
        var newly = all.Where(x => !known.Contains(x.Code)).ToList();
        var entries = all.Select(x => new MarketCapEntry(x.Code, Cap)).ToList();

        OnStatus?.Invoke($"[模拟源] 流通市值：扫回 {all.Count} 只（新发现 {newly.Count} 只），未发任何请求");
        progress?.Report($"[模拟源] 流通市值：扫回 {all.Count} 只，未发任何请求");
        return Task.FromResult(new MarketCapFetchResult(entries, newly, QuotesAreLive, all));
    }
}
