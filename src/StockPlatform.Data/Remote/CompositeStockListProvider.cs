using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 把几个名单源**按顺序合并、按代码去重**（2026-09-17 新增）。
///
/// ════ 为什么要合并，而不是换一个"更好的源" ════
/// 没有哪个源是全的，而且**漏的方式各不相同**：
///
/// | 源 | 漏什么 |
/// |---|---|
/// | 新浪 <c>hs_a</c> | 科创板 CDR（689009 从来没进过库）；退市整理期/*ST 的票；当天上市的新股 |
/// | 新浪 <c>kcb</c> | 只有科创板，补不了主板 |
/// | 上交所官方 | 只有沪市，深市/北交所一只都没有 |
///
/// 单独任何一个都会静默丢票——而"丢票"这件事下游**没有任何地方会报错**，只会表现成
/// "这只股票在选股结果里从来没出现过"。所以合并，并让**先到的源优先**。
///
/// ════ 顺序很重要：先到的赢 ════
/// 同一个代码在多个源里出现时，**保留第一个源的条目**，后面的只用来补新代码。
/// 因为第一个源（新浪）的 <see cref="StockListEntry.CirculatingMarketCap"/>/<see cref="StockListEntry.LastPrice"/>
/// 是有值的（<c>hs_a</c> 免费带出 <c>nmc</c>/<c>trade</c>，<c>SinaListMarketCapFetcher</c> 靠它们
/// 省掉全市场一轮市值请求），而上交所那条路这两个字段是 null。顺序反了会把市值洗掉。
///
/// ════ 单个源失败不致命，全失败才抛 ════
/// 兜底源挂了不该让整轮抓取失败——沪市兜底拿不到，最多是那十几只票这轮还缺，
/// 而新浪那条主路照常。但**每个失败都要报出来**（<see cref="OnStatus"/>），
/// 不能像以前那样静默少一批。全部源都失败时把第一个异常抛出去，让调用方按"名单取不到"处理。
/// </summary>
public class CompositeStockListProvider : IStockListProvider
{
    private readonly (string Label, IStockListProvider Provider)[] _sources;

    public event Action<string>? OnStatus;

    public CompositeStockListProvider(params (string Label, IStockListProvider Provider)[] sources)
    {
        if (sources.Length == 0) throw new ArgumentException("至少要给一个名单源", nameof(sources));
        _sources = sources;
    }

    public async Task<List<StockListEntry>> GetAllStocksAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<StockListEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Exception? firstFailure = null;
        int okSources = 0;

        foreach (var (label, provider) in _sources)
        {
            ct.ThrowIfCancellationRequested();
            List<StockListEntry> rows;
            try
            {
                rows = await provider.GetAllStocksAsync(progress, ct);
                okSources++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                firstFailure ??= ex;
                Report(progress, $"⚠ 名单源「{label}」失败（跳过它，其余源照常）：{ex.Message}");
                continue;
            }

            int added = 0;
            foreach (var e in rows)
                if (seen.Add(e.Code)) { result.Add(e); added++; }

            Report(progress, result.Count == added
                ? $"名单源「{label}」：{rows.Count} 只"
                : $"名单源「{label}」：{rows.Count} 只，其中新增 {added} 只（合计 {result.Count} 只）");
        }

        if (okSources == 0 && firstFailure != null)
            throw firstFailure;     // 全挂了才算"名单取不到"

        return result;
    }

    private void Report(IProgress<string>? progress, string text)
    {
        progress?.Report(text);
        OnStatus?.Invoke(text);
    }
}
