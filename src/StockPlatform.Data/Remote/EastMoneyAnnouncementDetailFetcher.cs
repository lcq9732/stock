using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Services;

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
                      $"?sr=-1&page_size={ListPageSize}&page_index=1&ann_type={AnnTypeOf(code)}&client_source=web" +
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

    /// <summary>
    /// 纯 B 股（深 200xxx／沪 900xxx）的公告挂在 <c>ann_type=B</c> 下，写死 A 会**返回空列表**
    /// （实测 200512 闽灿坤B：A → <c>total_hits:0</c>，B → 正常返回），于是这类票的正文永远取不到，
    /// 而且不报错、只是静默地少一条——2026-09-17 体检发现库里 4 条空壳全是它。
    ///
    /// ⚠ 判前缀一律走 <see cref="MarketClassifier"/>，不在这里自己写规则
    /// （provider 自写前缀规则害得 342 只票静默抓不到，见那边的类注释）。
    /// 600679 上海凤凰这种 A+B 两地挂牌的票不受影响：代码是 A 股代码，B 股公告也在 A 列表里。
    /// </summary>
    private static string AnnTypeOf(string code)
        => MarketClassifier.Classify(code) is MarketBoard.ShanghaiB or MarketBoard.ShenzhenB ? "B" : "A";

    /// <summary>Strips each title's leading "股票名:" / "股票名：" prefix (the two sites use
    /// different colon widths and don't always include the prefix at all), normalizes the rest
    /// (see <see cref="NormalizeForMatch"/>) and compares what's left, either direction containing
    /// the other — titles from the two sites are not always byte-identical even for the same article.</summary>
    internal static bool TitlesLooselyMatch(string a, string b)
    {
        var coreA = NormalizeForMatch(a);
        var coreB = NormalizeForMatch(b);
        if (coreA.Length == 0 || coreB.Length == 0) return false;
        return coreA.Contains(coreB, StringComparison.Ordinal)
            || coreB.Contains(coreA, StringComparison.Ordinal);
    }

    /// <summary>
    /// 把标题压成可比的形状：去掉「股票名：」前缀 → 全角转半角 → 删掉所有空白 → 转小写。
    ///
    /// ════ 为什么非归一化不可（2026-09-17 补）════
    /// 巨潮和东财对**同一篇公告**的标题写法差在标点和空格上，原来只做"互相包含"配不上，
    /// 于是走软失败分支：标题落库、数值全 null。实测两类差异：
    /// · 全角/半角括号——巨潮「中创智领（郑州）工业…」vs 东财「中创智领(郑州)工业…」；
    /// · 多余空格——巨潮「艾迪精密␣关于股份回购实施结果…」「…暨回购␣B␣股股份…」，东财没有。
    /// 09-17 那轮 6 条取不到正文的，6 条**全是这两类**，公告在东财都有；
    /// 全库 38 条空壳里 34 条同因（剩下 4 条是 <see cref="AnnTypeOf"/> 那个 B 股坑）。
    ///
    /// 全角转半角走 FF01–FF5E → ASCII 的整段偏移（差 0xFEE0），比列举「（）：，」稳：
    /// 括号、冒号、逗号、百分号、全角字母数字一次全覆盖。U+3000 全角空格并进空白一起删。
    /// </summary>
    internal static string NormalizeForMatch(string title)
    {
        var core = LeadingNamePrefix.Replace(title, "");
        var sb = new StringBuilder(core.Length);
        foreach (var ch in core)
        {
            if (char.IsWhiteSpace(ch) || ch == '　') continue;   // 空格差异一律抹掉
            sb.Append(ch is >= '！' and <= '～' ? (char)(ch - 0xFEE0) : ch);
        }
        return sb.ToString().ToLowerInvariant();
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
