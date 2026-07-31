using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取沪深两所官网的"终止上市公司"名单（2026-07-29 实测两个接口都可用，见 doc/factorlab-design.md）：
/// - 深交所：szse.cn/api/report/ShowReport/data?CATALOGID=1793_ssgs&amp;TABKEY=tab2（JSON，20条/页按
///   pagecount 翻页；字段 zqdm/zqjc/ssrq上市日/zzrq终止日；需带 Referer，偶发整页空数据需重试）
/// - 上交所：query.sse.com.cn/security/stock/getStockListData2.do?stockType=5（jsonp，pageSize=500 一次
///   取完；字段 SECURITY_CODE_A/SECURITY_ABBR_A/LISTING_DATE/CHANGE_DATE终止日（部分行为"-"缺失）；
///   需带 Referer www.sse.com.cn）
/// 只保留 A 股代码（00/30/60/68 前缀），B股(200/900)、老三板等剔除。名单是全量快照、总量仅几百条，
/// 不需要限流器；单页失败重试3次。
/// </summary>
public class ExchangeDelistedListProvider : IDelistedListProvider
{
    private static readonly string[] SzsePrefixes = ["00", "30"];
    private static readonly string[] SsePrefixes = ["60", "68"];

    private readonly HttpClient _http;

    public event Action<string>? OnStatus;

    public ExchangeDelistedListProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public async Task<List<DelistedStockRow>> GetAllAsync(CancellationToken ct = default)
    {
        var result = new List<DelistedStockRow>();
        var sse = await FetchSseAsync(ct);
        OnStatus?.Invoke($"上交所终止上市名单：{sse.Count} 只A股");
        result.AddRange(sse);
        var szse = await FetchSzseAsync(ct);
        OnStatus?.Invoke($"深交所终止上市名单：{szse.Count} 只A股");
        result.AddRange(szse);
        return result;
    }

    // ── 上交所：jsonp，一般一页拿完（total≈139，pageSize=500），保险起见仍按 total 翻页 ──
    private async Task<List<DelistedStockRow>> FetchSseAsync(CancellationToken ct)
    {
        var rows = new List<DelistedStockRow>();
        for (int page = 1; page <= 10; page++)
        {
            var url = "https://query.sse.com.cn/security/stock/getStockListData2.do" +
                      "?jsonCallBack=cb&isPagination=true&stockCode=&csrcCode=&areaName=&stockType=5" +
                      $"&pageHelp.cacheSize=1&pageHelp.beginPage={page}&pageHelp.pageSize=500&pageHelp.pageNo={page}";
            var txt = await GetStringWithRetryAsync(url, "https://www.sse.com.cn/", ct);
            int open = txt.IndexOf('('), close = txt.LastIndexOf(')');
            if (open < 0 || close <= open) throw new RateLimitedException("上交所终止上市接口返回的不是jsonp，疑似被拦截");
            using var doc = JsonDocument.Parse(txt[(open + 1)..close]);
            if (!doc.RootElement.TryGetProperty("pageHelp", out var pageHelp) ||
                !pageHelp.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;
            int before = rows.Count;
            foreach (var item in data.EnumerateArray())
            {
                var code = Str(item, "SECURITY_CODE_A");
                if (code.Length != 6 || !SsePrefixes.Any(code.StartsWith)) continue;
                rows.Add(new DelistedStockRow
                {
                    Code = code,
                    Name = FirstNonEmpty(Str(item, "SECURITY_ABBR_A"), Str(item, "COMPANY_ABBR")),
                    Exchange = "sse",
                    ListDate = ParseDate(Str(item, "LISTING_DATE")),
                    DelistDate = ParseDate(Str(item, "CHANGE_DATE")),
                });
            }
            int total = pageHelp.TryGetProperty("total", out var t) && t.TryGetInt32(out var ti) ? ti : rows.Count;
            if (rows.Count - before == 0 || rows.Count >= total) break;
        }
        return rows;
    }

    // ── 深交所：JSON，20条/页，第一页的 metadata.pagecount 决定翻几页 ──
    private async Task<List<DelistedStockRow>> FetchSzseAsync(CancellationToken ct)
    {
        var rows = new List<DelistedStockRow>();
        int pageCount = 1;
        for (int page = 1; page <= pageCount; page++)
        {
            var url = "https://www.szse.cn/api/report/ShowReport/data" +
                      $"?SHOWTYPE=JSON&CATALOGID=1793_ssgs&TABKEY=tab2&PAGENO={page}";
            var txt = await GetStringWithRetryAsync(url, "https://www.szse.cn/", ct);
            using var doc = JsonDocument.Parse(txt);
            bool gotData = false;
            foreach (var block in doc.RootElement.EnumerateArray())
            {
                if (!block.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in data.EnumerateArray())
                {
                    var code = Str(item, "zqdm");
                    if (code.Length != 6) continue;
                    gotData = true;
                    if (!SzsePrefixes.Any(code.StartsWith)) continue;
                    rows.Add(new DelistedStockRow
                    {
                        Code = code,
                        Name = Str(item, "zqjc"),
                        Exchange = "szse",
                        ListDate = ParseDate(Str(item, "ssrq")),
                        DelistDate = ParseDate(Str(item, "zzrq")),
                    });
                }
                if (page == 1 && gotData && block.TryGetProperty("metadata", out var meta) &&
                    meta.TryGetProperty("pagecount", out var pc) && pc.TryGetInt32(out var pcVal))
                    pageCount = pcVal;
            }
            // 实测该接口偶发返回整页空数据（不报错）——第一页就空说明这次名单没取到，让上层重试整个操作
            if (page == 1 && !gotData)
                throw new RateLimitedException("深交所终止上市接口返回空名单（偶发现象），请重试");
            await Task.Delay(300, ct); // 页间稍作停顿，礼貌抓取
        }
        return rows;
    }

    private async Task<string> GetStringWithRetryAsync(string url, string referer, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Referrer = new Uri(referer);
                var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var txt = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
                last = new RateLimitedException("接口返回空响应");
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
        }
        throw new RateLimitedException($"请求失败（重试3次）：{url} —— {last?.Message}", last);
    }

    private static string Str(JsonElement item, string key) =>
        item.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string FirstNonEmpty(params string[] xs) => xs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    /// <summary>"yyyy-MM-dd"；"-"/空 → null（上交所转板/合并的行没有终止日）。</summary>
    private static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
}
