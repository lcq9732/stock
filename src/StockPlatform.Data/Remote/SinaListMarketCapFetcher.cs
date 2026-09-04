using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Supplies circulating market cap (流通市值) as a byproduct of Sina's stock-list scan
/// (<see cref="SinaStockListProvider"/>) instead of a separate per-stock HTTP call — the DEFAULT
/// market-cap source (2026-07-08), replacing <see cref="TencentMarketCapFetcher"/>.
///
/// Rationale: SinaStockListProvider already pages through the entire market (~55 requests) for
/// the stock-LIST step, and each row already carries `nmc`（流通市值）for free (verified against
/// TencentMarketCapFetcher's per-stock value for 3 diverse stocks before trusting it — see
/// doc/data-platform-design.md §3.5). Replacing ~5000 individual per-stock calls with ~55
/// paginated list requests is both faster AND consolidates onto a single already-relied-on vendor
/// (Sina), matching the user's "一个源就够就不要拼凑" preference (2026-07-08).
///
/// This means <see cref="StockPlatform.Data.Orchestration.FetchOrchestrator"/>'s "拉取当天" mode
/// now does a full market-wide list scan purely to refresh market cap, even though it otherwise
/// deliberately avoids re-scanning the market for its K线 stock set (see that class's remarks) —
/// the user explicitly confirmed the added time cost is acceptable ("慢点没关系", 2026-07-08) in
/// exchange for 拉取当天 also keeping market cap current, not just 拉取全部.
/// </summary>
public class SinaListMarketCapFetcher : IMarketCapFetcher
{
    private readonly SinaStockListProvider _listProvider;

    // SinaStockListProvider has its own internal retry/backoff (RetryDelays) and doesn't expose a
    // RateLimiter-driven OnStatus like the per-stock fetchers — nothing to forward here.
    public event Action<string>? OnStatus { add { } remove { } }

    public SinaListMarketCapFetcher(SinaStockListProvider? listProvider = null)
    {
        _listProvider = listProvider ?? new SinaStockListProvider();
    }

    public async Task<MarketCapFetchResult> GetMarketCapsAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct = default)
    {
        var wanted = codes.ToHashSet();
        var allStocks = await _listProvider.GetAllStocksAsync(progress, ct);
        var entries = allStocks
            .Where(s => wanted.Contains(s.Code) && s.CirculatingMarketCap is > 0)
            .Select(s => new MarketCapEntry(s.Code, s.CirculatingMarketCap!.Value))
            .ToList();
        // 扫描本来就会看到全市场所有代码，不只是 wanted 里那些——把 wanted 之外的也顺带交给调用方，
        // 让它能发现本地股票表里还没有的新股（见 MarketCapFetchResult 的类注释），不需要额外请求。
        var newlyDiscovered = allStocks
            .Where(s => !wanted.Contains(s.Code))
            .Select(s => (s.Code, s.Name))
            .ToList();
        // 扫描本来就把全市场的代码+名称都拿到了，一并带给调用方（2026-09-02）——这样
        // "刷新名册"就不用再单独扫一遍同一个接口（省约 55 个请求，见 MarketCapFetchResult.AllStocks）。
        return new MarketCapFetchResult(entries, newlyDiscovered, IsMarketLive(allStocks),
            allStocks.Select(s => (s.Code, s.Name)).ToList());
    }

    /// <summary>
    /// 判断"扫描这一刻市场是否已经开盘"——决定这批市值该记到哪个交易日（见
    /// <see cref="MarketCapFetchResult.QuotesAreLive"/>）。判据是全市场有最新价（<c>trade</c>&gt;0）
    /// 的股票占比：盘前/周末/节假日新浪把所有股票的最新价都返回 0（2026-08-04 09:07 盘前实测确认，
    /// 此时市值是拿 <c>settlement</c> 昨收算的），开盘后绝大多数股票都有价。
    ///
    /// 用**占比**而不是"有没有任何一只有价"，是因为长期停牌股在盘中也是 0 价——个别 0 价属于常态，
    /// 全场 0 价才说明没开盘。阈值取 50%：真实的两种状态分别接近 0% 和接近 100%，中间地带不存在，
    /// 所以阈值取多少都不敏感，取中间值最稳。
    ///
    /// 扫描本身要跑一两分钟（约55页+每页间隔），横跨 09:30 开盘那一刻时前半段无价、后半段有价——
    /// 这种情况占比会落在中间，判成哪边都不算错：跨开盘意味着这批值本身就是"半截昨收半截实时"混的，
    /// 无论记哪天都不完美，收盘后再跑一次会用干净的收盘值覆盖掉（Upsert 主键含 as_of_date）。
    /// </summary>
    private static bool IsMarketLive(List<StockListEntry> allStocks)
    {
        if (allStocks.Count == 0) return false;
        int withPrice = allStocks.Count(s => s.LastPrice is > 0);
        return withPrice > allStocks.Count / 2;
    }
}
