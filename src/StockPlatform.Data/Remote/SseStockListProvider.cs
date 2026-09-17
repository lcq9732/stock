using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 上交所官网的**沪市全量 A 股**名单（2026-09-17 新增）——
/// <c>query.sse.com.cn/security/stock/getStockListData2.do?stockType=10</c>，jsonp。
/// 跟 <see cref="ExchangeDelistedListProvider"/> 是同一个域名、同一套 Referer 要求。
///
/// ════ 为什么需要它：新浪 hs_a 漏票，而且是**静默**漏 ════
/// 2026-09-17 拿上交所名单跟库里逐一对账，**上交所有、我们 StockMeta 没有的 14 只**：
///
///   · 8 只已改名"退市XX"但仍在交易的（退市创兴/退市苏吴/退市华嵘/退市熊猫/退市沪科/
///     退市国化/退市岩石/退市太和）——新浪的行情节点把它们剔除了
///   · 2 只 *ST（*ST精伦、*ST元成）、1 只 603056 德邦股份
///   · **601091 沈鼓集团，上市日 2026-09-17 —— 当天新股，新浪当天还没收录**
///   · 688287 退市观典
///
/// 这些票两融数据一直在更新，K 线却一根都没有。**上交所官方名单比新浪更全也更及时**，
/// 所以拿它给沪市兜底（深市/北交所仍只能靠新浪，深交所那边的同类缺口还没查）。
///
/// ════ 为什么是 stockType=10 ════
/// 实测各 stockType 的含义（2026-09-17）：
///
/// | stockType | 内容 | 只数 |
/// |---|---|---|
/// | **10** | **沪市全量 A 股（主板+科创板+CDR）** | **2333** |
/// | 1 | 主板 A 股 | 1604 |
/// | 8 | 科创板的一部分 | 405（实际科创板 618 只，这个不全） |
/// | 5 | 终止上市 | 139，**68 开头 0 只**（科创板退市股不在这套接口里） |
///
/// 只有 10 覆盖 688（618 只）和 689（CDR）。1 和 8 都不全，5 是另一回事
/// （见 <see cref="ExchangeDelistedListProvider"/>）。
///
/// ⚠ 返回里有脏行：代码字段是 <c>"-"</c> 的一条（公司简称也是 <c>"-"</c>），按"只收 6 位纯数字"过滤掉。
/// </summary>
public class SseStockListProvider : IStockListProvider
{
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };
    private const int PageSize = 1000;
    private const int MaxPages = 6;     // 2333 只 ⇒ 3 页；留余量，防市场规模变化被截断

    private readonly HttpClient _http;

    public SseStockListProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>跟 <see cref="ExchangeDelistedListProvider"/> 一样走系统代理——用户环境要靠它才出得去。</summary>
    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public async Task<List<StockListEntry>> GetAllStocksAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<StockListEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int total = -1;

        for (int page = 1; page <= MaxPages; page++)
        {
            var (rows, serverTotal) = await FetchPageWithRetryAsync(page, ct);
            if (total < 0) total = serverTotal;
            if (rows.Count == 0) break;
            foreach (var e in rows)
                if (seen.Add(e.Code)) result.Add(e);
            // 服务端说了一共多少条，收够就停——不靠"翻到空页"当终止条件
            if (total > 0 && result.Count >= total) break;
        }

        progress?.Report(total > 0
            ? $"上交所沪市A股名单：服务端声称 {total} 只，收到 {result.Count} 只"
            : $"上交所沪市A股名单：收到 {result.Count} 只（响应里没有 total）");
        return result;
    }

    private async Task<(List<StockListEntry> Rows, int Total)> FetchPageWithRetryAsync(int page, CancellationToken ct)
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

    private async Task<(List<StockListEntry> Rows, int Total)> FetchPageAsync(int page, CancellationToken ct)
    {
        var url = "https://query.sse.com.cn/security/stock/getStockListData2.do" +
                  "?jsonCallBack=cb&isPagination=true&stockCode=&csrcCode=&areaName=&stockType=10" +
                  $"&pageHelp.cacheSize=1&pageHelp.beginPage={page}&pageHelp.pageSize={PageSize}&pageHelp.pageNo={page}";

        string txt;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.sse.com.cn/");   // 不带这个会被拦
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"上交所返回 {(int)resp.StatusCode}，疑似触发限流");
            resp.EnsureSuccessStatusCode();
            txt = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接上交所股票列表接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }

        int open = txt.IndexOf('('), close = txt.LastIndexOf(')');
        if (open < 0 || close <= open)
            throw new RateLimitedException("上交所股票列表接口返回的不是 jsonp，疑似被拦截");

        using var doc = JsonDocument.Parse(txt[(open + 1)..close]);
        if (!doc.RootElement.TryGetProperty("pageHelp", out var pageHelp))
            throw new RateLimitedException("上交所股票列表响应里没有 pageHelp");

        int total = pageHelp.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : -1;

        var rows = new List<StockListEntry>();
        if (pageHelp.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var code = Str(item, "SECURITY_CODE_A");
                // 脏行：代码是 "-"。只收 6 位纯数字。
                if (code.Length != 6 || !code.All(char.IsDigit)) continue;
                var name = FirstNonEmpty(Str(item, "SECURITY_ABBR_A"), Str(item, "COMPANY_ABBR"));
                // 上交所不给市值/最新价——那两个字段留 null，由新浪那条路提供
                // （见 SinaListMarketCapFetcher / CompositeStockListProvider 的合并顺序）
                rows.Add(new StockListEntry(code, name));
            }
        }
        return (rows, total);
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    private static string FirstNonEmpty(params string[] xs) =>
        xs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "-") ?? "";
}
