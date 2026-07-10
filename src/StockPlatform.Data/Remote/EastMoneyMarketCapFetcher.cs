using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Collections.Concurrent;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches circulating market cap (流通市值) one stock at a time via EastMoney's single-stock
/// quote endpoint (<c>api/qt/stock/get</c>, same <see cref="EastMoneyBarFetcher.ToSecId"/> secid
/// convention as the bar fetcher) — NOT the bulk <c>clist/get</c> endpoint this used to call.
///
/// **Why not the bulk endpoint**: empirically confirmed unreliable in practice — repeated live
/// testing showed intermittent connection resets, JSON responses truncated mid-array, and (even
/// on a clean 200) every stock's f116/f117 coming back as the string "-" instead of a number, all
/// while the exact same market cap numbers were available instantly and consistently (100% success
/// across repeated tests) via this single-stock endpoint. Same trade-off the K线 fetchers already
/// made — many small, individually-reliable requests beat one big fragile one — so this now goes
/// through the same <see cref="RateLimiter"/>/retry/circuit-breaker machinery as
/// <see cref="EastMoneyBarFetcher"/>, at the cost of needing one HTTP call per stock (~5000+ calls
/// for a full-market run) instead of one bulk call — noticeably slower, but the old "fast" bulk
/// call was producing zero usable data anyway, so there was nothing being traded away.
///
/// **Not wired in by default** (2026-07-08) — EastMoney is largely unreachable in this user's
/// actual network environment (same finding as EastMoneyNetInflowFetcher), so
/// <see cref="TencentMarketCapFetcher"/> is what App.xaml.cs actually constructs. Kept as a second
/// <see cref="IMarketCapFetcher"/> implementation in case EastMoney becomes usable again.
/// </summary>
public class EastMoneyMarketCapFetcher : IMarketCapFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public EastMoneyMarketCapFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    /// <summary>Fetches every given code's circulating market cap independently — one code's
    /// failure (unrecognized code, delisted/suspended with no current quote, a one-off network
    /// hiccup that exhausted the RateLimiter's own retries) is skipped rather than failing the
    /// whole batch, mirroring FetchOrchestrator.ProcessOneStockAsync's per-stock error handling
    /// for K线. Concurrency/pacing is entirely the shared RateLimiter's job (same as
    /// EastMoneyBarFetcher) — this just fires one task per code.</summary>
    public async Task<MarketCapFetchResult> GetMarketCapsAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new ConcurrentBag<MarketCapEntry>();
        var completed = 0;
        var tasks = codes.Select(async code =>
        {
            try
            {
                var entry = await _rateLimiter.RunAsync(() => FetchOneAsync(code, ct), ct);
                if (entry != null) result.Add(entry);
            }
            catch (OperationCanceledException)
            {
                throw; // 用户点了"停止"
            }
            catch (Exception)
            {
                // 单只股票在重试/熔断耗尽后仍失败，跳过这一只，不影响其余股票继续抓取
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                if (done % 200 == 0 || done == codes.Count)
                    progress?.Report($"正在获取流通市值 ({done}/{codes.Count})");
            }
        });
        await Task.WhenAll(tasks);
        // 逐只查询——只看得到被问到的这些代码，没有"顺带发现新股"这回事，NewlyDiscoveredCodes
        // 永远是空的（见 MarketCapFetchResult 的类注释）。
        return new MarketCapFetchResult(result.ToList(), new List<(string, string)>());
    }

    private async Task<MarketCapEntry?> FetchOneAsync(string code, CancellationToken ct)
    {
        string secid;
        try { secid = EastMoneyBarFetcher.ToSecId(code); }
        catch (ArgumentException) { return null; } // 无法识别所属市场的代码，跳过

        var url = $"http://push2.eastmoney.com/api/qt/stock/get?secid={secid}&fields=f116,f117";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法获取 {code} 流通市值：{detail}（可能是代理/防火墙问题，也可能是触发了反爬限流）", ex);
        }
        using var _ = resp;

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"东方财富返回 {(int)resp.StatusCode}，疑似触发反爬限流");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new RateLimitedException($"获取 {code} 流通市值时东方财富返回空响应，疑似触发反爬限流");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // data 为 null（而不是抛错）——代码查不到实时行情（停牌太久/已退市等），当作"这只股票暂时
        // 没有市值数据"跳过，不是网络问题，不需要重试。
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        // f117 缺失、不是数字（比如占位符"-"），或者干脆就是数字0（实测遇到过——834021 这只代码
        // 返回的 data 里 f117 是字面量数字 0，不是"-"，同样代表"没有真实数据"，不是"流通市值真的是
        // 0元"）都按"暂时没有数据"跳过，不写入，避免下游范围判断被一个假的0误导。
        if (!data.TryGetProperty("f117", out var capEl) || capEl.ValueKind != JsonValueKind.Number) return null;
        var marketCap = capEl.GetDouble();
        if (marketCap <= 0) return null;
        return new MarketCapEntry(code, marketCap);
    }
}
