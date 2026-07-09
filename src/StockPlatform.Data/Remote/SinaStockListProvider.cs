using System.Text;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches the full list of A-share stock codes from Sina Finance's public quote-node endpoint —
/// a deliberately different domain (sina.com.cn) from <see cref="EastMoneyStockListProvider"/>
/// (eastmoney.com), so switching the Fetcher's data source dropdown to a non-EastMoney vendor
/// (see doc/data-platform-design.md section 3.4) also routes the stock-LIST step around a
/// network that blocks eastmoney.com, not just the bar-data step. Tencent's public API has no
/// equivalent full-list endpoint (only per-code lookups), so this is paired with the Tencent bar
/// source instead — see the composition root.
/// </summary>
public class SinaStockListProvider : IStockListProvider
{
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };

    // The server hard-caps every page at 100 rows no matter what "num" is requested (verified
    // empirically — num=200 and num=6000 both still return exactly 100). ~5500 A-shares means
    // ~55 pages; MaxPages is a generous ceiling so a change in the actual market size never
    // silently truncates the list.
    private const int PageSize = 100;
    private const int MaxPages = 200;

    // 没有这个延时的话，真的会被限流——开发这个功能时用bash脚本紧挨着连续请求了约80页，很快就
    // 收到了HTML反爬拦截页而不是JSON（不是猜测，是实测踩到的坑）。这个类现在不只是"拉取全部"时
    // 偶尔调一次的股票列表步骤了，SinaListMarketCapFetcher 让它变成每次"拉取全部"/"拉取当天"
    // 都要完整跑一遍，被连续请求的频率比以前高，延时更有必要。
    private static readonly TimeSpan DelayBetweenPages = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http;

    static SinaStockListProvider()
    {
        // GBK isn't included by default in .NET Core — this endpoint replies with
        // "charset=gbk" regardless of Accept-Charset.
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaStockListProvider(HttpClient? httpClient = null)
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
            if (pageEntries.Count == 0) break; // ran past the last page
            result.AddRange(pageEntries);
            if (page % 10 == 0) progress?.Report($"正在获取全市场股票列表...（已取 {result.Count} 只）");
            await Task.Delay(DelayBetweenPages, ct); // 见 DelayBetweenPages 注释——连续不间断请求会被限流
        }
        return result;
    }

    private async Task<List<StockListEntry>> FetchPageWithRetryAsync(int page, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchPageAsync(page, ct);
            }
            catch (RateLimitedException) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<List<StockListEntry>> FetchPageAsync(int page, CancellationToken ct)
    {
        // node=hs_a covers 沪深北 A股 all together (Shanghai/Shenzhen/Beijing exchanges).
        var url = "http://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
                  $"?page={page}&num={PageSize}&sort=symbol&asc=1&node=hs_a&symbol=&_s_r_a=init";

        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接新浪财经接口：{detail}（可能是网络问题，也可能是触发了反爬限流）", ex);
        }

        if (bytes.Length == 0)
            throw new RateLimitedException("新浪财经返回空响应，疑似触发反爬限流");

        var json = Encoding.GetEncoding("GBK").GetString(bytes);
        if (string.IsNullOrWhiteSpace(json) || json == "null") return new List<StockListEntry>();

        using var doc = JsonDocument.Parse(json);
        var result = new List<StockListEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var code = item.GetProperty("code").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? "";
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;

            // "nmc"（流通市值，单位万元）跟这批股票的行情数据一起免费带出来，不需要单独发请求——
            // 字段含义在 SinaListMarketCapFetcher 引入前用贵州茅台/000001/300750三只股本结构不同
            // 的股票跟 TencentMarketCapFetcher 的结果做过交叉校验，见 doc/data-platform-design.md
            // 3.5节。缺失/非数字/<=0 一律按"没有数据"处理，不写入假的0元市值。
            double? marketCap = item.TryGetProperty("nmc", out var nmcEl) && nmcEl.ValueKind == JsonValueKind.Number
                ? nmcEl.GetDouble() * 10_000
                : null;
            if (marketCap <= 0) marketCap = null;

            result.Add(new StockListEntry(code, name, marketCap));
        }
        return result;
    }
}
