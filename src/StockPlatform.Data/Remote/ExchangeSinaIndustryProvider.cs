using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 全市场证监会行业分类（2026-08-04 新增，三个来源合并，均已实测）：
/// - **门类**（19类，覆盖沪深全部 5404 只）：
///   深交所 `szse.cn/api/report/ShowReport/data?CATALOGID=1110&TABKEY=tab1` 的 `sshymc`（形如 "C 制造业"）；
///   上交所 `query.sse.com.cn/sseQuery/commonQuery.do?sqlId=COMMON_SSE_CP_GPJCTPZ_GPLB_GP_L`
///   的 `CSRC_CODE`/`CSRC_CODE_DESC`。
/// - **大类**（84类，约 3200 只）：新浪 `newFLJK.php?param=industry` 给行业清单（hangye_ZAxx），
///   再逐个用 `Market_Center.getHQNodeData?node=hangye_xxx` 取成分股。
///
/// 为什么要两级：门类里"制造业"一类就占全市场六成，拿它做行业中性化等于没中性化；而大类粒度合适
/// 却只覆盖 58%。所以两级都存，消费端优先用大类、缺失退回门类（见 <see cref="StockIndustry.Best"/>）。
/// 为什么不用申万：新浪的申万节点(sw2_xxxxxx)虽然能取成分股，但拿不到"所有申万节点"的清单，
/// 只能靠枚举代码猜，不可靠。
/// </summary>
public class ExchangeSinaIndustryProvider : IIndustryProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus;

    static ExchangeSinaIndustryProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 新浪是GBK
    }

    public ExchangeSinaIndustryProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _rateLimiter.OnStatus += m => OnStatus?.Invoke(m);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public async Task<List<StockIndustry>> GetAllAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, StockIndustry>(StringComparer.Ordinal);

        // ① 门类（覆盖面最广，先铺底）
        foreach (var (code, cls, name) in await FetchSzseClassAsync(ct))
            map[code] = new StockIndustry { Code = code, ClassCode = cls, ClassName = name };
        OnStatus?.Invoke($"深交所行业门类：{map.Count} 只");

        int before = map.Count;
        foreach (var (code, cls, name) in await FetchSseClassAsync(ct))
            map[code] = new StockIndustry { Code = code, ClassCode = cls, ClassName = name };
        OnStatus?.Invoke($"上交所行业门类：{map.Count - before} 只，累计 {map.Count} 只");

        // ② 大类（粒度更细，补在同一条记录上；新浪没收录的保持只有门类）
        int majorHit = 0;
        foreach (var (code, major) in await FetchSinaMajorAsync(ct))
        {
            if (map.TryGetValue(code, out var row)) row.MajorName = major;
            else map[code] = new StockIndustry { Code = code, MajorName = major };
            majorHit++;
        }
        OnStatus?.Invoke($"新浪证监会大类：{majorHit} 只有细分行业；合计 {map.Count} 只");

        return map.Values.ToList();
    }

    // ── 深交所：A股列表，sshymc 形如 "C 制造业" ──
    private async Task<List<(string Code, string Cls, string Name)>> FetchSzseClassAsync(CancellationToken ct)
    {
        var result = new List<(string, string, string)>();
        int pageCount = 1;
        for (int page = 1; page <= pageCount && page <= 300; page++)
        {
            var url = $"https://www.szse.cn/api/report/ShowReport/data?SHOWTYPE=JSON&CATALOGID=1110&TABKEY=tab1&PAGENO={page}";
            var txt = await _rateLimiter.RunAsync(() => GetStringAsync(url, "https://www.szse.cn/", gbk: false, ct), ct);
            using var doc = JsonDocument.Parse(txt);
            bool got = false;
            foreach (var block in doc.RootElement.EnumerateArray())
            {
                // ⚠️ 这个接口把 pagecount 返回成**字符串**（"145"），直接 TryGetInt32 会失败、
                // pageCount 停在1，结果只抓到第一页20条（2026-08-04 实测踩过）。两种类型都认。
                if (page == 1 && block.TryGetProperty("metadata", out var meta) &&
                    meta.TryGetProperty("pagecount", out var pc) && TryReadInt(pc, out var n)) pageCount = n;
                if (!block.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in data.EnumerateArray())
                {
                    var code = Str(item, "agdm");
                    var hy = Str(item, "sshymc");     // "C 制造业"
                    if (code.Length != 6) continue;
                    got = true;
                    var parts = hy.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    result.Add((code, parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : hy));
                }
            }
            if (!got) break;
        }
        return result;
    }

    // ── 上交所：CSRC_CODE='J' + CSRC_CODE_DESC='金融业' ──
    private async Task<List<(string Code, string Cls, string Name)>> FetchSseClassAsync(CancellationToken ct)
    {
        var result = new List<(string, string, string)>();
        const int pageSize = 500;
        for (int page = 1; page <= 20; page++)
        {
            var url = "https://query.sse.com.cn/sseQuery/commonQuery.do?sqlId=COMMON_SSE_CP_GPJCTPZ_GPLB_GP_L" +
                      $"&isPagination=true&pageHelp.pageSize={pageSize}&pageHelp.pageNo={page}" +
                      $"&pageHelp.beginPage={page}&pageHelp.cacheSize=1";
            var txt = await _rateLimiter.RunAsync(() => GetStringAsync(url, "https://www.sse.com.cn/", gbk: false, ct), ct);
            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
            int n = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var code = Str(item, "A_STOCK_CODE");
                if (code.Length != 6) continue;
                result.Add((code, Str(item, "CSRC_CODE"), Str(item, "CSRC_CODE_DESC")));
                n++;
            }
            if (n < pageSize) break;
        }
        return result;
    }

    // ── 新浪：先取84个大类清单，再逐个取成分股 ──
    private async Task<List<(string Code, string Major)>> FetchSinaMajorAsync(CancellationToken ct)
    {
        var result = new List<(string, string)>();
        var listTxt = await _rateLimiter.RunAsync(
            () => GetStringAsync("https://vip.stock.finance.sina.com.cn/q/view/newFLJK.php?param=industry",
                                 "https://finance.sina.com.cn/", gbk: true, ct), ct);
        // 形如 "hangye_ZA01":"hangye_ZA01,农业,15,..."
        var nodes = Regex.Matches(listTxt, @"""(hangye_\w+)"":""hangye_\w+,([^,]+),(\d+),")
            .Select(m => (Node: m.Groups[1].Value, Name: m.Groups[2].Value, Count: int.Parse(m.Groups[3].Value)))
            .Where(x => x.Count > 0)
            .ToList();
        OnStatus?.Invoke($"证监会大类行业 {nodes.Count} 个，开始逐个取成分股...");

        int done = 0;
        foreach (var (node, name, count) in nodes)
        {
            for (int page = 1; page <= (count / 80) + 1; page++)
            {
                var url = "https://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
                          $"?page={page}&num=80&sort=symbol&asc=1&node={node}";
                string txt;
                try
                {
                    txt = await _rateLimiter.RunAsync(() => GetStringAsync(url, "https://finance.sina.com.cn/", gbk: false, ct), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { break; } // 单个行业取不到不影响整体，它会退回门类
                if (string.IsNullOrWhiteSpace(txt) || txt.TrimStart().StartsWith("null")) break;
                List<string> codes;
                try
                {
                    using var doc = JsonDocument.Parse(txt);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) break;
                    codes = doc.RootElement.EnumerateArray().Select(x => Str(x, "code")).Where(x => x.Length == 6).ToList();
                }
                catch { break; }
                if (codes.Count == 0) break;
                foreach (var c in codes) result.Add((c, name));
                if (codes.Count < 80) break;
            }
            if (++done % 20 == 0) OnStatus?.Invoke($"证监会大类进度 {done}/{nodes.Count}");
        }
        return result;
    }

    private async Task<string> GetStringAsync(string url, string referer, bool gbk, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(referer);
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"行业接口返回 {(int)resp.StatusCode}，疑似限流");
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) throw new RateLimitedException("行业接口返回空响应");
            return gbk ? Encoding.GetEncoding("GBK").GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接行业接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>JSON 里数字有时是 number、有时是 string（深交所的 pagecount 就是字符串），两种都读。</summary>
    private static bool TryReadInt(JsonElement e, out int value)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetInt32(out value);
        if (e.ValueKind == JsonValueKind.String) return int.TryParse(e.GetString(), out value);
        value = 0;
        return false;
    }
}
