using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Per-stock announcement detail lookup against EastMoney's announcement API — the "detail" step
/// of the order-win pipeline. EastMoney doesn't offer market-wide keyword search, but for a single
/// known stock code it returns the announcement's full plain-text body directly (no PDF parsing
/// needed), unlike cninfo which only exposes the PDF attachment. Bridges from a cninfo discovery
/// hit (title + approximate date, no id) to an EastMoney article id by matching title/date within
/// the stock's own recent announcement list — necessarily a heuristic, since the two sites don't
/// share IDs; see <see cref="TitlesLooselyMatch"/>.
/// </summary>
public class EastMoneyAnnouncementDetailFetcher : IAnnouncementDetailFetcher
{
    // How many of the stock's most recent announcements to scan for a title/date match — generous
    // enough to cover a stock with a burst of same-day announcements without paging indefinitely.
    private const int ListPageSize = 100;

    private static readonly Regex LeadingNamePrefix = new(@"^[^\s:：]{2,10}[:：]\s*", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public EastMoneyAnnouncementDetailFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public async Task<(string ArtCode, string Content)?> FetchDetailAsync(string code, string title, DateOnly approxDate, CancellationToken ct = default)
    {
        return await _rateLimiter.RunAsync(() => FetchDetailInternalAsync(code, title, approxDate, ct), ct);
    }

    private async Task<(string, string)?> FetchDetailInternalAsync(string code, string title, DateOnly approxDate, CancellationToken ct)
    {
        var listUrl = "https://np-anotice-stock.eastmoney.com/api/security/ann" +
                      $"?sr=-1&page_size={ListPageSize}&page_index=1&ann_type=A&client_source=web" +
                      $"&stock_list={code}&f_node=0&s_node=0";

        var body = await GetStringAsync(listUrl, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        string? matchedArtCode = null;
        foreach (var item in list.EnumerateArray())
        {
            var itemTitle = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (!item.TryGetProperty("notice_date", out var dateEl)) continue;
            // notice_date is a full "yyyy-MM-dd HH:mm:ss" string — DateOnly.TryParse rejects that
            // outright (it only accepts date-only representations), so this has to go through
            // DateTime first.
            if (!DateTime.TryParse(dateEl.GetString(), out var itemDateTime)) continue;
            var itemDate = DateOnly.FromDateTime(itemDateTime);

            // ±3 days: cninfo's announcementTime and EastMoney's notice_date occasionally disagree
            // by a day or two for the same article (timezone/publish-vs-effective-date quirks).
            if (Math.Abs(itemDate.DayNumber - approxDate.DayNumber) > 3) continue;
            if (!TitlesLooselyMatch(title, itemTitle)) continue;

            matchedArtCode = item.GetProperty("art_code").GetString();
            break;
        }
        if (matchedArtCode == null) return null;

        var contentUrl = $"https://np-cnotice-stock.eastmoney.com/api/content/ann?art_code={matchedArtCode}&client_source=web&page_index=1";
        var contentBody = await GetStringAsync(contentUrl, ct);
        if (string.IsNullOrWhiteSpace(contentBody)) return null;

        using var contentDoc = JsonDocument.Parse(contentBody);
        if (!contentDoc.RootElement.TryGetProperty("data", out var contentData)) return null;
        var noticeContent = contentData.TryGetProperty("notice_content", out var c) ? c.GetString() : null;
        return string.IsNullOrWhiteSpace(noticeContent) ? null : (matchedArtCode, noticeContent);
    }

    /// <summary>Strips each title's leading "股票名:" / "股票名：" prefix (the two sites use
    /// different colon widths and don't always include the prefix at all) and compares what's
    /// left case-insensitively, either direction containing the other — titles from the two sites
    /// are not always byte-identical even for the same article.</summary>
    private static bool TitlesLooselyMatch(string a, string b)
    {
        var coreA = LeadingNamePrefix.Replace(a, "").Trim();
        var coreB = LeadingNamePrefix.Replace(b, "").Trim();
        if (coreA.Length == 0 || coreB.Length == 0) return false;
        return coreA.Contains(coreB, StringComparison.OrdinalIgnoreCase)
            || coreB.Contains(coreA, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接东方财富公告接口：{detail}", ex);
        }
        using var _ = resp;

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"东方财富返回 {(int)resp.StatusCode}，疑似触发反爬限流");

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
