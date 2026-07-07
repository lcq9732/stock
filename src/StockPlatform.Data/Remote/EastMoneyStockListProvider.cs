using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches the full list of A-share stock codes from EastMoney's public clist endpoint —
/// covers 上证主板/科创板, 深证主板/创业板, 北交所 (see fs parameter below).
/// </summary>
public class EastMoneyStockListProvider : IStockListProvider
{
    private readonly HttpClient _http;

    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10) };

    public EastMoneyStockListProvider(HttpClient? httpClient = null)
    {
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

    public async Task<List<StockListEntry>> GetAllStocksAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchOnceAsync(progress, ct);
            }
            catch (RateLimitedException) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task<List<StockListEntry>> FetchOnceAsync(IProgress<string>? progress, CancellationToken ct)
    {
        const string fs = "m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23,m:0+t:81+s:2048";
        var url = "http://push2.eastmoney.com/api/qt/clist/get" +
                  $"?pn=1&pz=10000&po=1&np=1&fltt=2&invt=2&fid=f3&fs={fs}&fields=f12,f14";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法获取全市场股票列表：{detail}（可能是网络问题，也可能是触发了反爬限流）", ex);
        }
        using var _ = resp;

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"东方财富返回 {(int)resp.StatusCode}，疑似触发反爬限流");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new RateLimitedException("东方财富返回空响应，疑似触发反爬限流");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("未获取到股票列表数据");

        var result = new List<StockListEntry>();
        if (data.TryGetProperty("diff", out var diff))
        {
            foreach (var item in diff.EnumerateArray())
            {
                var code = item.GetProperty("f12").GetString() ?? "";
                var name = item.GetProperty("f14").GetString() ?? "";
                if (code.Length == 6 && code.All(char.IsDigit))
                    result.Add(new StockListEntry(code, name));
            }
        }

        // Diagnostic: EastMoney's response normally carries data.total (how many stocks matched
        // the fs filter server-side) alongside data.diff (the actual page of results returned).
        // If total is much bigger than diff.Length, the response got truncated in transit
        // (network/proxy interference) — the filter itself is fine, we just didn't receive
        // everything the server intended to send. If total itself is small, the fs filter is only
        // matching a subset of the market — a different, code-side problem. Logged every time so
        // this is diagnosable from the user's own run without needing to reproduce it elsewhere.
        var total = data.TryGetProperty("total", out var totalEl) ? totalEl.GetInt32() : (int?)null;
        progress?.Report(total.HasValue
            ? $"东方财富股票列表：服务端声称匹配 {total} 只，实际收到 {result.Count} 条" +
              (total.Value > result.Count ? "（响应被截断，不是筛选条件的问题）" : "")
            : $"东方财富股票列表：收到 {result.Count} 条（响应里没有 total 字段，无法判断服务端原本匹配了多少）");

        return result;
    }
}
