using System.Globalization;
using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 全市场总股本，走东财**条件选股接口**（2026-09-14 新增）。字段清单见
/// doc/eastmoney-selection-api.md。
///
/// ════ 为什么是这个接口而不是别的 ════
/// 它是唯一一个「一个请求拿全市场当前总股本」的通道：<c>ps=10000</c> 一次回 5562 只、
/// 782KB、<c>nextpage=false</c>，1~2 秒。相比之下 push2delay 的 clist 要 60 页，
/// 而 F10 的股本结构报表是逐只查。
///
/// 2026-09-14 实测：
///   · 不需要认证——无 Cookie、无 Referer、连 User-Agent 都不用，裸请求 HTTP 200
///     （下面照旧带上这些头，是为了跟其余东财 provider 一致，不是接口的要求）；
///   · 域名走 <c>datacenter</c>，本机可达（不通的只有 push2）；
///   · **北交所在内**：920 开头 343 只，跟库里数量对得上。这一条必须每轮验，
///     理由见下面的覆盖告警；
///   · <c>TOTAL_SHARES</c> 单位是**股**（科士达 582,225,094）。⚠ 文档里那张表写的是
///     「亿」，是错的——那份文档的单位列本来就注明「不完全可靠，入库前逐个核实」。
///
/// ════ filter 用不了 ════
/// 文本型 filter（<c>(SECUCODE="600000.SH")</c>）返回空，语法未知。所以拿不了单只票，
/// 只能整个市场拉回来自己挑——好在一次就够，代价可以忽略。
/// </summary>
public class EastMoneyTotalSharesProvider : ITotalSharesProvider
{
    private const string BaseUrl = "https://datacenter.eastmoney.com/stock/selection/api/data/get";

    /// <summary>
    /// 一页要多少。实测全市场 5562 只一页装得下（<c>nextpage=false</c>），留着翻页逻辑
    /// 是防接口哪天改了上限——半截名单比拿不到更糟，见 TotalSharesTask 的覆盖告警。
    /// </summary>
    private const int PageSize = 10000;

    /// <summary>翻页上限。正常一页就完，翻到第三页就说明接口行为变了，该停下来报错。</summary>
    private const int MaxPages = 10;

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public string SourceName => "EastMoneySelection";

    public event Action<string>? OnStatus;

    public EastMoneyTotalSharesProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://data.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _rateLimiter.OnStatus += s => OnStatus?.Invoke(s);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public async Task<TotalSharesSnapshot> GetAllAsync(CancellationToken ct = default)
    {
        var rows = new List<TotalSharesEntry>();
        var seen = new HashSet<string>();
        DateTime? tradeDate = null;

        for (int page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{BaseUrl}?type=RPTA_APP_STOCKSELECT"
                    + "&sty=SECUCODE,SECURITY_NAME_ABBR,TOTAL_SHARES"
                    + $"&p={page}&ps={PageSize}&source=SELECT_SECURITIES&client=PC";

            OnStatus?.Invoke($"总股本：拉第 {page} 页（每页 {PageSize} 只）…");
            var body = await _rateLimiter.RunAsync(() => GetAsync(url, ct), ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new InvalidOperationException(
                    $"东财选股接口没有返回 result（message：{msg ?? "无"}）——接口可能改了。");
            }

            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in data.EnumerateArray())
            {
                var code = Str(item, "SECURITY_CODE");
                // SECURITY_CODE 是接口白送的 6 位码；万一哪天没了，从 SECUCODE 剥后缀兜底。
                if (string.IsNullOrEmpty(code))
                {
                    var secu = Str(item, "SECUCODE");
                    if (!string.IsNullOrEmpty(secu))
                    {
                        int dot = secu.IndexOf('.');
                        code = dot > 0 ? secu[..dot] : secu;
                    }
                }
                if (string.IsNullOrEmpty(code)) continue;

                // 总股本为 null / 非正数一律跳过：宁可库里没有这一行，也不要写一个 0 进去。
                // 消费方查不到就回退报表股本并标识，写个 0 会把「市值 0、PE 0」算出来。
                if (Num(item, "TOTAL_SHARES") is not { } shares || shares <= 0) continue;

                if (!seen.Add(code)) continue;   // 翻页重复（不该发生，但别让它进库）
                rows.Add(new TotalSharesEntry(code, shares));

                if (tradeDate == null && Date(item, "MAX_TRADE_DATE") is { } d) tradeDate = d;
            }

            if (!(result.TryGetProperty("nextpage", out var np) && np.ValueKind == JsonValueKind.True))
                break;
        }

        OnStatus?.Invoke($"总股本：拿到 {rows.Count} 只"
                       + (tradeDate is { } td ? $"，接口自报交易日 {td:yyyy-MM-dd}" : ""));
        return new TotalSharesSnapshot(rows, tradeDate);
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float,
                                                      CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;
    }
}
