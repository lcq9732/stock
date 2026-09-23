using System.Globalization;
using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 上交所官网的**每日 ETF 份额**（2026-09-23）——
/// <c>query.sse.com.cn/commonQuery.do?sqlId=COMMON_SSE_ZQPZ_ETFZL_XXPL_ETFGM_SEARCH_L&amp;STAT_DATE=yyyy-MM-dd</c>，
/// 即官网「ETF 规模」那一页背后的接口。
///
/// 一个请求拿当天**全部**沪市 ETF（2024-09-30 是 555 只，2026-09-22 是 912 只），pageSize 给 5000 一页收完。
/// 返回的 <c>TOT_VOL</c> 单位是**万份**，两位小数。
///
/// 覆盖范围（2026-09-23 实测）：**2012-01-04 起**（那天 23 只），更早的日子返回空列表；
/// 当天的份额收盘后才发布，白天查当天也是空的。
///
/// 请求参数里带 <c>SEC_CODE</c> 过滤不起作用（照样返回全部），所以不带。
/// 跟 <see cref="SseStockListProvider"/> 同一个域名、同样要 Referer。
/// </summary>
public class SseEtfShareProvider : IEtfShareProvider
{
    /// <summary>一页收完。当前 900 多只，留足余量；服务端总数超过它时会报错而不是静默截断。</summary>
    private const int PageSize = 5000;

    private readonly RateLimiter _rateLimiter;
    private readonly HttpClient _http;

    public string Market => "sh";

    /// <summary>2026-09-23 实测：2011-12-30 空、2012-01-04 有 23 只。</summary>
    public DateOnly FirstDay => new(2012, 1, 4);

    public EtfShareBatch Batch => EtfShareBatch.Day;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public SseEtfShareProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>跟 <see cref="SseStockListProvider"/> 一样走系统代理——用户环境要靠它才出得去。</summary>
    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public Task<List<EtfShareRow>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        // 这个接口一次只给一天（STAT_DATE 是单个日期）。给区间就是调用方把 Batch 用错了，别静默只取第一天。
        if (from != to)
            throw new ArgumentException($"上交所 ETF 份额一次只能取一天，收到 {from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}");
        return _rateLimiter.RunAsync(() => FetchAsync(from, ct), ct);
    }

    private async Task<List<EtfShareRow>> FetchAsync(DateOnly day, CancellationToken ct)
    {
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = "https://query.sse.com.cn/commonQuery.do?isPagination=true"
                + $"&pageHelp.pageSize={PageSize}&pageHelp.pageNo=1&pageHelp.beginPage=1&pageHelp.cacheSize=1"
                + $"&sqlId=COMMON_SSE_ZQPZ_ETFZL_XXPL_ETFGM_SEARCH_L&STAT_DATE={date}";

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
            throw new RateLimitedException($"无法连接上交所 ETF 份额接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }

        return Parse(txt, day);
    }

    /// <summary>
    /// 解析一天的响应。拆出来是为了能拿真实响应样本单测，不用联网。
    ///
    /// ⚠ 空列表和"响应不对"必须分开：前者是"那天确实没有"（会被记进 DailyFetchNoData、以后不再问），
    /// 后者要抛——把反爬页当成空列表，那一天就被永久判成没有数据了。
    /// </summary>
    public static List<EtfShareRow> Parse(string json, DateOnly day)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new RateLimitedException("上交所 ETF 份额接口返回的不是 JSON，疑似被拦截", ex);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("pageHelp", out var pageHelp)
                || !pageHelp.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new RateLimitedException("上交所 ETF 份额响应里没有 pageHelp.data，疑似被拦截");

            int total = pageHelp.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt32() : -1;
            if (total > data.GetArrayLength())
                throw new InvalidOperationException(
                    $"上交所 ETF 份额 {day:yyyy-MM-dd} 共 {total} 只，一页只收到 {data.GetArrayLength()} 只——PageSize 不够了");

            var expectDate = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var rows = new List<EtfShareRow>();
            foreach (var item in data.EnumerateArray())
            {
                var code = Str(item, "SEC_CODE");
                if (code.Length != 6 || !code.All(char.IsDigit)) continue;

                // 返回的是别的日子，说明接口语义变了——宁可整天报错，也不能把别的日子的份额记成这一天
                var stat = Str(item, "STAT_DATE");
                if (stat != expectDate)
                    throw new InvalidOperationException(
                        $"上交所 ETF 份额：请求 {expectDate}，返回里有 {stat} 的行（{code}）");

                if (!double.TryParse(Str(item, "TOT_VOL"), NumberStyles.Float, CultureInfo.InvariantCulture, out var wan)
                    || wan <= 0)
                    continue;
                rows.Add(new EtfShareRow("sh", code, day, wan));
            }
            return rows;
        }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}
