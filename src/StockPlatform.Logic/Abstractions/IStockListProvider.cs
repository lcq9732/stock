namespace StockPlatform.Logic.Abstractions;

/// <summary><paramref name="CirculatingMarketCap"/> (流通市值，元) is populated only by providers
/// whose underlying list endpoint happens to carry it for free (e.g. SinaStockListProvider) —
/// null otherwise (e.g. EastMoneyStockListProvider's list endpoint doesn't have it). See
/// SinaListMarketCapFetcher, which relies on this field instead of a separate per-stock call.
///
/// <paramref name="LastPrice"/>（最新价，元，2026-08-04新增）同样是"顺带免费带出来的"字段，只有新浪
/// 的列表接口有。它存在的唯一目的是判断**这一刻市场有没有在交易**：接口在盘前/周末/节假日会把
/// 最新价返回 0（此时 <paramref name="CirculatingMarketCap"/> 是拿"昨收"算出来的，属于上一个交易日，
/// 不属于今天），开盘后才是当日实时价。SinaListMarketCapFetcher 用全市场的这个占比推出
/// <see cref="StockPlatform.Logic.Models.MarketCapFetchResult.QuotesAreLive"/>，
/// FetchOrchestrator 再据此决定市值行的 as_of_date 该记哪个交易日。拿不到/为0一律 null。</summary>
public record StockListEntry(string Code, string Name, double? CirculatingMarketCap = null, double? LastPrice = null);

/// <summary>Fetches the full list of tradable A-share stock codes, so the Fetcher program
/// doesn't require the user to type codes in one by one — see doc/data-platform-design.md.</summary>
public interface IStockListProvider
{
    Task<List<StockListEntry>> GetAllStocksAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}
