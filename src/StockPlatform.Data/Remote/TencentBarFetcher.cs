using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// Fetches Level-1 front-adjusted (qfq) daily bars from Tencent's public appstock endpoint.
/// Verified against EastMoney for a real dividend event (贵州茅台 2026-06-25) — the two
/// vendors produce identical front-adjusted numbers for this case (see doc/data-platform-design.md).
/// Only day granularity is supported directly; week/month come from local aggregation.
///
/// Uses the <c>newfqkline</c> endpoint (2026-07-10, was <c>fqkline</c>) — same host/qfq/640-cap/
/// paging, but each row also carries 换手率(%) and 成交额(万元), so Bar.Turnover/Bar.Amount are now
/// populated (previously left at 0). Cross-checked 成交额 against EastMoney's native kline amount
/// field for 贵州茅台 (403521.69万元 ≈ EastMoney的4035216946元, 精确到万元) before switching.
/// Volume stays in 手 (unchanged), same as the old endpoint.
/// </summary>
public class TencentBarFetcher : IBarDataFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    public TencentBarFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    public static string ToSymbol(string code)
    {
        code = code.Trim();
        // 大盘指数（"sh000001"这种带前缀的完整符号）原样使用——腾讯接口本来就吃这种符号，
        // 指数返回节点是 "day" 而不是 "qfqday"（下方解析两者都认），见 MarketIndexCatalog。
        if (MarketIndexCatalog.IsPrefixedSymbol(code)) return code;

        if (code.Length != 6 || !code.All(char.IsDigit))
            throw new ArgumentException($"'{code}' 不是合法的6位A股代码");

        return MarketClassifier.Classify(code) switch
        {
            MarketBoard.Unknown => throw new ArgumentException($"无法识别代码 '{code}' 所属市场"),
            _ => $"{MarketClassifier.TencentSymbolPrefix(code)}{code}",
        };
    }

    public async Task<(string Name, List<Bar> Bars)> FetchAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        if (granularity != Granularity.Day)
            throw new ArgumentException($"TencentBarFetcher 目前只直接支持日线（周/月请用本地聚合，分钟线暂未实现）：'{granularity}'");

        return await _rateLimiter.RunAsync(() => FetchInternalAsync(code, start, end, ct), ct);
    }

    // Tencent's server hard-caps every response at 640 bars, no matter what "count" we ask for
    // (verified empirically — even requesting 2000 still returns exactly 640). A 3-year daily
    // backfill needs ~750 trading days, so long ranges must be paged by walking the end date
    // backwards and stitching results together.
    private const int PageHardCap = 640;
    private const int MaxPages = 10; // 10 * 640 ≈ 6400 trading days ≈ 25 years — comfortably more than we'd ever request

    private async Task<(string, List<Bar>)> FetchInternalAsync(string code, DateTime? start, DateTime? end, CancellationToken ct)
    {
        var startDate = (start ?? DateTime.Today.AddYears(-3)).Date;
        var endDate = (end ?? DateTime.Today).Date;

        var merged = new Dictionary<DateTime, Bar>();
        string? name = null;
        var pageEnd = endDate;

        for (int page = 0; page < MaxPages; page++)
        {
            var (pageName, pageBars) = await FetchPageAsync(code, pageEnd, ct);
            name ??= pageName;
            if (pageBars.Count == 0) break;

            foreach (var b in pageBars) merged[b.PeriodStart] = b;

            var earliest = pageBars.Min(b => b.PeriodStart);
            if (earliest <= startDate || pageBars.Count < PageHardCap) break; // full range covered, or hit the start of listing

            pageEnd = earliest.AddDays(-1);
            await Task.Delay(200, ct); // be polite between pages of the same stock, independent of the outer per-stock rate limit
        }

        var result = merged.Values
            .Where(b => b.PeriodStart >= startDate && b.PeriodStart <= endDate)
            .OrderBy(b => b.PeriodStart)
            .ToList();

        // Per-page pct_chg is wrong at page boundaries (each page's "previous close" resets) —
        // recompute once over the final merged, sorted series.
        double? prevClose = null;
        foreach (var b in result)
        {
            b.PctChange = prevClose is > 0 ? (b.Close - prevClose.Value) / prevClose.Value * 100 : 0;
            prevClose = b.Close;
        }

        return (name ?? code, result);
    }

    private async Task<(string Name, List<Bar> Bars)> FetchPageAsync(string code, DateTime pageEnd, CancellationToken ct)
    {
        var symbol = ToSymbol(code);
        // newfqkline（而不是老的 fqkline）——同一个host、同样的qfq前复权、同样的640条上限和分页
        // 方式，但每行在 volume 之后多带了 换手率 和 成交额 两个字段（见下方解析），老的 fqkline
        // 只到 volume 为止。2026-07-10 为了补上 成交额/换手率 改用这个接口。
        var url = "http://web.ifzq.gtimg.cn/appstock/app/newfqkline/get" +
                  $"?param={symbol},day,,{pageEnd:yyyy-MM-dd},{PageHardCap},qfq";

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接腾讯财经接口：{detail}（可能是网络问题，也可能是触发了反爬限流）", ex);
        }
        using var _ = resp;

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new RateLimitedException($"腾讯财经返回 {(int)resp.StatusCode}，疑似触发反爬限流");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new RateLimitedException("腾讯财经返回空响应，疑似触发反爬限流");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var rc) && rc.GetInt32() != 0)
            throw new InvalidOperationException($"腾讯财经返回错误码 {rc.GetInt32()}（代码 {code} 可能不存在）");

        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty(symbol, out var stockNode))
            throw new InvalidOperationException($"未获取到 {code} 的数据，请检查代码是否正确");

        var name = stockNode.TryGetProperty("qt", out var qt) && qt.TryGetProperty(symbol, out var qtArr) && qtArr.GetArrayLength() > 1
            ? qtArr[1].GetString() ?? code
            : code;

        var bars = new List<Bar>();
        if (stockNode.TryGetProperty("qfqday", out var rows) || stockNode.TryGetProperty("day", out rows))
        {
            foreach (var row in rows.EnumerateArray())
            {
                var len = row.GetArrayLength();
                var date = DateTime.ParseExact(row[0].GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var open = double.Parse(row[1].GetString()!, CultureInfo.InvariantCulture);
                var close = double.Parse(row[2].GetString()!, CultureInfo.InvariantCulture);
                var high = double.Parse(row[3].GetString()!, CultureInfo.InvariantCulture);
                var low = double.Parse(row[4].GetString()!, CultureInfo.InvariantCulture);
                var volume = len > 5 ? double.Parse(row[5].GetString()!, CultureInfo.InvariantCulture) : 0;

                // newfqkline 每行结构：[0]日期 [1]开 [2]收 [3]高 [4]低 [5]成交量(手) [6]{}(占位对象，忽略)
                // [7]换手率(%) [8]成交额(万元) [9]""。都做长度+类型保护，缺字段就按0（这样万一接口
                // 退回老的只到[5]的格式、或者某行字段不全，也不会抛异常）。换手率原样存百分数（10.43
                // 表示10.43%，跟 QuoteDetailWindow 的"{Turnover:F2}%"显示一致）；成交额从万元换成元
                // 存（跟 EastMoneyBarFetcher 存元、以及 FormatLargeNumber 按亿/万显示的约定一致）。
                double turnover = 0, amountYuan = 0;
                if (len > 7 && row[7].ValueKind == JsonValueKind.String &&
                    double.TryParse(row[7].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var to))
                    turnover = to;
                if (len > 8 && row[8].ValueKind == JsonValueKind.String &&
                    double.TryParse(row[8].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var amtWan))
                    amountYuan = amtWan * 1e4; // 万元 → 元

                bars.Add(new Bar
                {
                    Code = code,
                    Granularity = Granularity.Day,
                    PeriodStart = date,
                    Open = open,
                    Close = close,
                    High = high,
                    Low = low,
                    Volume = volume,
                    Amount = amountYuan,
                    Turnover = turnover,
                    FetchedAt = DateTime.Now,
                });
            }
        }

        return (name, bars);
    }
}
