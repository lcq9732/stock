using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches per-stock daily 主力净流入（大单+超大单净流入之和，元）history from EastMoney's
/// individual-stock fund-flow endpoint (fflow/daykline/get) — a completely separate dataset from
/// OHLCV bars (kline/get), see doc/data-platform-design.md. Field mapping verified against live
/// data before building this: requesting fields2=f51,f52,...,f65 confirmed f52 (主力净流入净额)
/// exactly equals f55(大单)+f56(超大单), and f57%（主力净流入占比）exactly equals f60%+f61%, and
/// the 4 order-size categories' amounts sum to ~0 as expected for a redistribution split — so only
/// f51 (date) and f52 (主力净流入净额) are requested here.
///
/// **Not wired in by default** (2026-07-08) — EastMoney is largely unreachable in this user's
/// actual network environment, so <see cref="SinaNetInflowFetcher"/> is what App.xaml.cs actually
/// constructs. Kept as a second <see cref="INetInflowFetcher"/> implementation in case EastMoney
/// becomes usable again or a future per-source picker is added (mirroring NamedBarSource for K线).
/// </summary>
public class EastMoneyNetInflowFetcher : INetInflowFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public EastMoneyNetInflowFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public async Task<List<NetInflow>> FetchAsync(string code, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        return await _rateLimiter.RunAsync(() => FetchInternalAsync(code, start, end, ct), ct);
    }

    private async Task<List<NetInflow>> FetchInternalAsync(string code, DateTime? start, DateTime? end, CancellationToken ct)
    {
        var secid = EastMoneyBarFetcher.ToSecId(code);
        var beg = start?.ToString("yyyyMMdd") ?? "0";
        var endStr = end?.ToString("yyyyMMdd") ?? "20500101";

        var url = "https://push2his.eastmoney.com/api/qt/stock/fflow/daykline/get" +
                  $"?secid={secid}&fields1=f1,f2,f3,f7&fields2=f51,f52" +
                  $"&klt=101&lmt=100000&beg={beg}&end={endStr}";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法连接东方财富资金流向接口：{detail}（可能是代理/防火墙问题，也可能是触发了反爬限流）", ex);
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
        if (root.TryGetProperty("rc", out var rc) && rc.GetInt32() != 0)
            throw new InvalidOperationException($"EastMoney 返回错误码 {rc.GetInt32()}（代码 {code} 可能不存在）");

        var result = new List<NetInflow>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            return result; // 该股票没有资金流向数据（比如刚上市/停牌），不算错误，交给调用方按"缺数据"处理

        if (data.TryGetProperty("klines", out var klinesEl))
        {
            foreach (var line in klinesEl.EnumerateArray())
            {
                var parts = line.GetString()!.Split(',');
                if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue; // 个别交易日EastMoney会用空字符串占位表示当天没有数据
                result.Add(new NetInflow
                {
                    Code = code,
                    PeriodStart = DateTime.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    MainNetInflow = value,
                    FetchedAt = DateTime.Now,
                });
            }
        }
        return result;
    }
}
