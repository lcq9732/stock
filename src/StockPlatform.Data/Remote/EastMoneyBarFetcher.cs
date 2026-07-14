using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches Level-1 bars from EastMoney's public push2his endpoint. Supports day and minute
/// granularities directly (via the klt parameter); week/month are NOT fetched here — they're
/// aggregated locally from day bars (see <see cref="StockPlatform.Logic.Services.BarAggregator"/>
/// and doc/data-platform-design.md section 3.3).
/// </summary>
public class EastMoneyBarFetcher : IBarDataFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public EastMoneyBarFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public static string ToSecId(string code)
    {
        code = code.Trim();
        // 大盘指数（"sh000001"这种带前缀的完整符号，见 MarketIndexCatalog）——东方财富的指数
        // secid 按交易所映射：沪市指数 "1.000001"，深市指数 "0.399001"。
        if (MarketIndexCatalog.IsPrefixedSymbol(code))
            return (code.StartsWith("sh") ? "1." : "0.") + code[2..];

        if (code.Length != 6 || !code.All(char.IsDigit))
            throw new ArgumentException($"'{code}' 不是合法的6位A股代码");
        if (MarketClassifier.Classify(code) == MarketBoard.Unknown)
            throw new ArgumentException($"无法识别代码 '{code}' 所属市场");

        return $"{MarketClassifier.EastMoneySecIdPrefix(code)}{code}";
    }

    private static int ToKlt(string granularity) => granularity switch
    {
        Granularity.Day => 101,
        Granularity.Min1 => 1,
        Granularity.Min5 => 5,
        Granularity.Min15 => 15,
        Granularity.Min30 => 30,
        Granularity.Min60 => 60,
        _ => throw new ArgumentException($"EastMoneyBarFetcher 不直接支持粒度 '{granularity}'（周/月请用本地聚合）"),
    };

    public async Task<(string Name, List<Bar> Bars)> FetchAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        return await _rateLimiter.RunAsync(() => FetchInternalAsync(code, granularity, start, end, ct), ct);
    }

    private async Task<(string, List<Bar>)> FetchInternalAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct)
    {
        var secid = ToSecId(code);
        var klt = ToKlt(granularity);
        var beg = start?.ToString("yyyyMMdd") ?? "0";
        var endStr = end?.ToString("yyyyMMdd") ?? "20500101";

        var url = "https://push2his.eastmoney.com/api/qt/stock/kline/get" +
                  $"?secid={secid}&fields1=f1,f2,f3,f4,f5,f6" +
                  "&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61" +
                  $"&klt={klt}&fqt=1&beg={beg}&end={endStr}&lmt=100000";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法连接东方财富接口：{detail}（可能是代理/防火墙问题，也可能是触发了反爬限流）", ex);
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

        if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException($"未获取到 {code} 的数据，请检查代码是否正确");

        var name = data.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? code : code;
        var bars = new List<Bar>();

        var isMinute = klt != 101;
        var dateFormat = isMinute ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";

        if (data.TryGetProperty("klines", out var klinesEl))
        {
            foreach (var line in klinesEl.EnumerateArray())
            {
                var parts = line.GetString()!.Split(',');
                bars.Add(new Bar
                {
                    Code = code,
                    Granularity = granularity,
                    PeriodStart = DateTime.ParseExact(parts[0], dateFormat, CultureInfo.InvariantCulture),
                    Open = double.Parse(parts[1], CultureInfo.InvariantCulture),
                    Close = double.Parse(parts[2], CultureInfo.InvariantCulture),
                    High = double.Parse(parts[3], CultureInfo.InvariantCulture),
                    Low = double.Parse(parts[4], CultureInfo.InvariantCulture),
                    Volume = double.Parse(parts[5], CultureInfo.InvariantCulture),
                    Amount = double.Parse(parts[6], CultureInfo.InvariantCulture),
                    PctChange = double.Parse(parts[8], CultureInfo.InvariantCulture),
                    Turnover = parts.Length > 10 ? double.Parse(parts[10], CultureInfo.InvariantCulture) : 0,
                    FetchedAt = DateTime.Now,
                });
            }
        }

        return (name, bars);
    }
}
