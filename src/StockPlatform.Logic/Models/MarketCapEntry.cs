namespace StockPlatform.Logic.Models;

/// <summary>One stock's circulating market cap (流通市值，元), from whichever IMarketCapFetcher
/// implementation supplied it.</summary>
public record MarketCapEntry(string Code, double CirculatingMarketCap);

/// <summary><paramref name="NewlyDiscoveredCodes"/>（2026-07-10新增）是这次市值查询顺带发现的、
/// 调用方传入的<c>codes</c>里没有的股票代码+名称——只有像 SinaListMarketCapFetcher 这样本身就是
/// 扫全市场列表拿市值的实现才可能有内容（扫描过程天然会看到全市场所有代码，不只是调用方问的那些），
/// 逐只查询的实现（EastMoney/Tencent）永远返回空列表，因为它们只看得到被问到的那些代码，看不到
/// 别的。调用方（FetchOrchestrator）用这个列表把本地股票表补上新股，不需要额外的网络请求——蹭的是
/// 市值查询本来就在做的那次扫描。</summary>
public record MarketCapFetchResult(List<MarketCapEntry> Entries, List<(string Code, string Name)> NewlyDiscoveredCodes);
