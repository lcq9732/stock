namespace StockPlatform.Logic.Models;

/// <summary>One stock's circulating market cap (流通市值，元), from whichever IMarketCapFetcher
/// implementation supplied it.</summary>
public record MarketCapEntry(string Code, double CirculatingMarketCap);

/// <summary><paramref name="NewlyDiscoveredCodes"/>（2026-07-10新增）是这次市值查询顺带发现的、
/// 调用方传入的<c>codes</c>里没有的股票代码+名称——只有像 SinaListMarketCapFetcher 这样本身就是
/// 扫全市场列表拿市值的实现才可能有内容（扫描过程天然会看到全市场所有代码，不只是调用方问的那些），
/// 逐只查询的实现（EastMoney/Tencent）永远返回空列表，因为它们只看得到被问到的那些代码，看不到
/// 别的。调用方（FetchOrchestrator）用这个列表把本地股票表补上新股，不需要额外的网络请求——蹭的是
/// 市值查询本来就在做的那次扫描。
///
/// <paramref name="QuotesAreLive"/>（2026-08-04新增）= "这批市值是用**当日**行情算出来的吗"。
/// 市值接口只给"当下"的快照、不带日期，而快照的基准价在盘前/周末/节假日是**上一个交易日的收盘**、
/// 开盘后才是当日价——所以光有值不足以决定这行该记哪个交易日（这正是历史上 as_of_date 一律写
/// <c>DateTime.Today</c> 导致周末/盘前抓的值被标错日期的原因，见 FetchOrchestrator.FetchMarketCapAsync）。
/// <c>true</c>=当日已开盘（值属于今天）、<c>false</c>=还没开盘或今天不是交易日（值属于上一个交易日）、
/// <c>null</c>=该实现给不出这个信号。只有 SinaListMarketCapFetcher 填真值（它的列表接口顺带带了最新价，
/// 见 <see cref="StockPlatform.Logic.Abstractions.StockListEntry.LastPrice"/>）；逐只查询的
/// EastMoney/Tencent 实现只取了市值字段、没取价格，所以是 null——它们目前都没有被使用，真要启用得先
/// 补上这个信号，否则盘中抓的值会被记到上一个交易日。</summary>
public record MarketCapFetchResult(
    List<MarketCapEntry> Entries,
    List<(string Code, string Name)> NewlyDiscoveredCodes,
    bool? QuotesAreLive = null);
