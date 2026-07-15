using System.Text;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 全市场 ETF 列表（2026-07-15新增）——跟 <see cref="SinaStockListProvider"/> 走同一个新浪
/// getHQNodeData 接口，只把 node 从 hs_a（沪深北A股）换成 etf_hq_fund（场内ETF基金）。新浪/腾讯
/// 都能连（用户环境东财不可用），ETF 日K本身复用现有 BarFetcher（带前缀符号原生支持）。
///
/// **返回的 Code 是带 sh/sz 前缀的8位符号**（"sh510300"/"sz159915"），不是6位裸代码——刻意
/// 这样存：ETF 的 510300 之类跟个股/指数代码空间会重叠，带前缀存进 Bar 表既不撞主键，又能被
/// SqliteBarRepository.GetAllCodes（只认6位纯数字）自动挡在选股全集之外，不会混进个股筛选。
/// 跟大盘指数(MarketIndexCatalog)用的是同一套"带前缀符号"策略。
///
/// 注意：node 名 etf_hq_fund 与返回字段(symbol/code/name)按新浪当前接口约定；**用户首次运行时
/// 需在真实网络里确认接口可达、字段无误**（沙箱网络与用户环境不同，见 doc/data-platform-design.md）。
/// </summary>
public class SinaEtfListProvider : IStockListProvider
{
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };
    private const int PageSize = 100;   // 服务端每页硬上限100，跟 hs_a 一样
    private const int MaxPages = 100;   // ETF 总数约千级，100页(1万)足够，防止市场规模变化被截断
    private static readonly TimeSpan DelayBetweenPages = TimeSpan.FromMilliseconds(300); // 连续请求会被反爬限流，见 SinaStockListProvider

    private readonly HttpClient _http;

    static SinaEtfListProvider()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // 接口回 GBK
    }

    public SinaEtfListProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(12);
    }

    public async Task<List<StockListEntry>> GetAllStocksAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<StockListEntry>();
        for (int page = 1; page <= MaxPages; page++)
        {
            var pageEntries = await FetchPageWithRetryAsync(page, ct);
            if (pageEntries.Count == 0) break;
            result.AddRange(pageEntries);
            if (page % 5 == 0) progress?.Report($"正在获取全市场ETF列表...（已取 {result.Count} 只）");
            await Task.Delay(DelayBetweenPages, ct);
        }
        return result;
    }

    private async Task<List<StockListEntry>> FetchPageWithRetryAsync(int page, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await FetchPageAsync(page, ct); }
            catch (RateLimitedException) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<List<StockListEntry>> FetchPageAsync(int page, CancellationToken ct)
    {
        var url = "http://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
                  $"?page={page}&num={PageSize}&sort=symbol&asc=1&node=etf_hq_fund&symbol=&_s_r_a=init";

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪财经ETF接口：{detail}（可能是网络问题，也可能是触发了反爬限流）", ex);
        }

        if (bytes.Length == 0)
            throw new RateLimitedException("新浪财经返回空响应，疑似触发反爬限流");

        var json = Encoding.GetEncoding("GBK").GetString(bytes);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new List<StockListEntry>();

        using var doc = JsonDocument.Parse(json);
        var result = new List<StockListEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // "symbol" 是带前缀符号(sh510300)，"code" 是6位裸代码——这里要带前缀的，用 symbol。
            var symbol = item.TryGetProperty("symbol", out var symEl) ? symEl.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nmEl) ? nmEl.GetString() ?? "" : "";
            symbol = symbol.Trim().ToLowerInvariant();
            if (symbol.Length != 8 || !(symbol.StartsWith("sh") || symbol.StartsWith("sz") || symbol.StartsWith("bj"))
                || !symbol[2..].All(char.IsDigit))
                continue; // 只收规整的带前缀ETF符号，异常行跳过
            result.Add(new StockListEntry(symbol, name));
        }
        return result;
    }
}
