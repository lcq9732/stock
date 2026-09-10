using System.Net;
using System.Text;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 证监会行业分类抓取的共同骨架（2026-09-10 从 <see cref="ExchangeSinaIndustryProvider"/> 抽出）。
///
/// **两级分类、两个来源**：
///   · <b>门类代码</b>（单字母 A~S）只有沪深两所官网给，别的数据源都替代不了，所以这一路
///     固定在基类里，不由子类选择；
///   · <b>门类名 / 大类名</b>由子类决定从哪来（新浪＝两所门类名 + 新浪大类；东财＝两级都用
///     东财的，见 <c>ExchangeEastMoneyIndustryProvider</c>）。
///
/// 抽基类的理由见 feedback「每个数据源一个类」：两所那段踩过的坑（深交所 pagecount 必须取
/// 最大值、上交所翻页判据）各写一遍就会各错一遍，而它跟"大类从哪来"完全正交。
///
/// ⚠ 基类**不查库**，所以"这轮结果比库里少太多就整轮放弃"那道护栏不在这里，在
/// <c>IndustryTask</c>——只有任务层知道库里上次是多少。
/// </summary>
public abstract class IndustryProviderBase : IIndustryProvider
{
    protected readonly HttpClient Http;
    protected readonly RateLimiter Limiter;

    public event Action<string>? OnStatus;

    /// <summary>写进 <c>StockIndustry.source</c> 的来源标记，取值见 <see cref="IndustrySources"/>。</summary>
    public abstract string SourceName { get; }

    static IndustryProviderBase()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // 新浪是 GBK
    }

    protected IndustryProviderBase(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        Limiter = rateLimiter;
        Http = httpClient ?? new HttpClient(CreateHandler());
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        Http.Timeout = TimeSpan.FromSeconds(30);
        Limiter.OnStatus += m => OnStatus?.Invoke(m);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    protected void Status(string message) => OnStatus?.Invoke(message);

    /// <summary>
    /// 先用两所门类铺底（覆盖面最广，且只有它给门类字母），再交给子类补细分。
    /// </summary>
    public async Task<List<StockIndustry>> GetAllAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, StockIndustry>(StringComparer.Ordinal);

        foreach (var (code, cls, name) in await FetchSzseClassAsync(ct))
            map[code] = new StockIndustry { Code = code, ClassCode = cls, ClassName = name };
        Status($"深交所行业门类：{map.Count} 只");

        int before = map.Count;
        foreach (var (code, cls, name) in await FetchSseClassAsync(ct))
            map[code] = new StockIndustry { Code = code, ClassCode = cls, ClassName = name };
        Status($"上交所行业门类：{map.Count - before} 只，累计 {map.Count} 只");

        await ApplyDetailAsync(map, ct);
        return map.Values.ToList();
    }

    /// <summary>
    /// 子类在这里把细分信息补到 <paramref name="map"/> 上——两所没收录的票要新建条目
    /// （北交所就不在两所那两个接口里）。就地改，不返回新集合。
    /// </summary>
    protected abstract Task ApplyDetailAsync(Dictionary<string, StockIndustry> map, CancellationToken ct);

    // ── 深交所：A股列表，sshymc 形如 "C 制造业" ──
    protected async Task<List<(string Code, string Cls, string Name)>> FetchSzseClassAsync(CancellationToken ct)
    {
        var result = new List<(string, string, string)>();
        int pageCount = 1;
        for (int page = 1; page <= pageCount && page <= 300; page++)
        {
            var url = $"https://www.szse.cn/api/report/ShowReport/data?SHOWTYPE=JSON&CATALOGID=1110&TABKEY=tab1&PAGENO={page}";
            var txt = await Limiter.RunAsync(() => GetStringAsync(url, "https://www.szse.cn/", gbk: false, ct), ct);
            using var doc = JsonDocument.Parse(txt);
            bool got = false;
            foreach (var block in doc.RootElement.EnumerateArray())
            {
                // ⚠️ 响应里有4个tab块（A股/B股/CDR/A+B股），只有A股那块有数据、pagecount=145，
                // 后面几块都是 0——必须取**最大值**，否则会被后面的 0 覆盖，循环停在第一页只拿到20条
                // （2026-08-04 实测踩过这个坑）。顺带兼容 pagecount 是字符串的情况。
                if (page == 1 && block.TryGetProperty("metadata", out var meta) &&
                    meta.TryGetProperty("pagecount", out var pc) && TryReadInt(pc, out var n) && n > pageCount)
                    pageCount = n;
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
    protected async Task<List<(string Code, string Cls, string Name)>> FetchSseClassAsync(CancellationToken ct)
    {
        var result = new List<(string, string, string)>();
        const int pageSize = 500;
        for (int page = 1; page <= 20; page++)
        {
            var url = "https://query.sse.com.cn/sseQuery/commonQuery.do?sqlId=COMMON_SSE_CP_GPJCTPZ_GPLB_GP_L" +
                      $"&isPagination=true&pageHelp.pageSize={pageSize}&pageHelp.pageNo={page}" +
                      $"&pageHelp.beginPage={page}&pageHelp.cacheSize=1";
            var txt = await Limiter.RunAsync(() => GetStringAsync(url, "https://www.sse.com.cn/", gbk: false, ct), ct);
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

    protected async Task<string> GetStringAsync(string url, string referer, bool gbk, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(referer);
            var resp = await Http.SendAsync(req, ct);
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

    protected static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>JSON 里数字有时是 number、有时是 string（深交所的 pagecount 就是字符串），两种都读。</summary>
    protected static bool TryReadInt(JsonElement e, out int value)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetInt32(out value);
        if (e.ValueKind == JsonValueKind.String) return int.TryParse(e.GetString(), out value);
        value = 0;
        return false;
    }
}
