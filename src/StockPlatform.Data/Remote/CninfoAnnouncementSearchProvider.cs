using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Market-wide keyword search over cninfo's (巨潮资讯网) full-text announcement index — the
/// "discovery" step of the order-win pipeline (see doc: 中标/订单公告 抓取方案). One call covers
/// every listed company at once, unlike EastMoney's per-stock announcement list, but only returns
/// a PDF link and a short highlighted snippet, not the full plain-text body — see
/// <see cref="EastMoneyAnnouncementDetailFetcher"/> for that.
/// </summary>
public class CninfoAnnouncementSearchProvider : IAnnouncementSearchProvider
{
    private const string SearchUrl = "http://www.cninfo.com.cn/new/fulltextSearch/full";
    // Hard cap on pages per keyword/date-window — a keyword sweep is meant to catch a recent
    // window's worth of hits, not backfill years of history; without this a broad keyword over a
    // wide date range could page indefinitely.
    private const int MaxPages = 30;

    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SixDigitCode = new(@"^\d{6}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public CninfoAnnouncementSearchProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(12);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public async Task<List<AnnouncementSearchHit>> SearchAsync(string keyword, DateOnly start, DateOnly end, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var hits = new List<AnnouncementSearchHit>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var pageHits = await _rateLimiter.RunAsync(() => SearchPageAsync(keyword, start, end, page, ct), ct);
            if (pageHits.Count == 0) break;
            hits.AddRange(pageHits);
            progress?.Report($"[巨潮全文检索] 关键词\"{keyword}\" 第 {page} 页，累计 {hits.Count} 条命中");
        }
        return hits;
    }

    private async Task<List<AnnouncementSearchHit>> SearchPageAsync(string keyword, DateOnly start, DateOnly end, int page, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["searchkey"] = keyword,
            ["sdate"] = start.ToString("yyyy-MM-dd"),
            ["edate"] = end.ToString("yyyy-MM-dd"),
            // Title-only match, not full-text: isfulltext=true matches the keyword ANYWHERE in the
            // document body (e.g. a bond rating report that happens to mention "中标" in passing),
            // which floods results with irrelevant announcements — see doc: 中标/订单公告 抓取方案.
            // Title-only keeps precision high since order-win announcements reliably say so in
            // their title (中标公告/签订合同公告/etc).
            ["isfulltext"] = "false",
            ["sortName"] = "pubdate",
            ["sortType"] = "desc",
            ["pageNum"] = page.ToString(),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsync(SearchUrl, new FormUrlEncodedContent(form), ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接巨潮资讯网全文检索接口：{detail}", ex);
        }
        using var _ = resp;

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"巨潮资讯网返回 {(int)resp.StatusCode}，疑似触发反爬限流");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new RateLimitedException("巨潮资讯网返回空响应，疑似触发反爬限流");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var hits = new List<AnnouncementSearchHit>();
        if (!root.TryGetProperty("announcements", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return hits;

        foreach (var item in arr.EnumerateArray())
        {
            var code = item.GetProperty("secCode").GetString() ?? "";
            if (!SixDigitCode.IsMatch(code)) continue; // A股only — skip HK/bonds/etc.

            var name = item.TryGetProperty("secName", out var n) ? n.GetString() ?? "" : "";
            var rawTitle = item.TryGetProperty("announcementTitle", out var t) ? t.GetString() ?? "" : "";
            var title = HtmlTagRegex.Replace(rawTitle, ""); // strip cninfo's <em> highlight markup
            var pdfUrl = item.TryGetProperty("adjunctUrl", out var u) ? u.GetString() : null;
            if (!string.IsNullOrEmpty(pdfUrl)) pdfUrl = "http://static.cninfo.com.cn/" + pdfUrl;

            if (!item.TryGetProperty("announcementTime", out var timeEl)) continue;
            var publishDate = DateTimeOffset.FromUnixTimeMilliseconds(timeEl.GetInt64()).LocalDateTime;

            hits.Add(new AnnouncementSearchHit(code, name, title, publishDate, pdfUrl));
        }
        return hits;
    }
}
