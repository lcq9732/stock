using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ExcelDataReader;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取某交易日全市场融资融券明细——**交易所官方源**（最权威、非东财）：
/// - 上交所：query.sse.com.cn/marketdata/tradedata/queryMargin.do（JSON；键 rzye=融资余额、rzmre=融资
///   买入额、rqylje=融券余额、rqyl=融券余量、stockCode、securityAbbr）
/// - 深交所：szse.cn/api/report/ShowReport（xlsx；列 0代码/1简称/2融资买入额/3融资余额/5融券余量/6融券
///   余额，数值带千分位逗号）——用 <see cref="ExcelDataReader"/> 解析
/// 两所合并返回。按交易日拉，用 <see cref="RateLimiter"/> 限流（回补历史时按日多次调用）。
/// </summary>
public class ExchangeMarginProvider : IMarginProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static ExchangeMarginProvider()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public ExchangeMarginProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
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

    public async Task<List<MarginDetailRow>> GetDetailAsync(DateOnly date, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var result = new List<MarginDetailRow>();
        result.AddRange(await _rateLimiter.RunAsync(() => FetchSseAsync(date, now, ct), ct));
        result.AddRange(await _rateLimiter.RunAsync(() => FetchSzseAsync(date, now, ct), ct));
        return result;
    }

    // 上交所：JSON
    private async Task<List<MarginDetailRow>> FetchSseAsync(DateOnly date, DateTime now, CancellationToken ct)
    {
        var url = "https://query.sse.com.cn/marketdata/tradedata/queryMargin.do" +
                  $"?isPagination=true&tabType=mxtype&detailsDate={date:yyyyMMdd}&stockCode=" +
                  "&pageHelp.pageSize=10000&pageHelp.pageNo=1&pageHelp.beginPage=1&pageHelp.cacheSize=1&pageHelp.endPage=1";
        byte[] bytes;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.sse.com.cn/");
            var resp = await _http.SendAsync(req, ct);
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接上交所两融接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
        if (bytes.Length == 0) throw new RateLimitedException("上交所两融接口返回空响应，疑似限流");

        var rows = new List<MarginDetailRow>();
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return rows;
        foreach (var item in arr.EnumerateArray())
        {
            var code = item.TryGetProperty("stockCode", out var cEl) ? cEl.GetString() ?? "" : "";
            if (code.Length != 6) continue;
            rows.Add(new MarginDetailRow
            {
                Code = code,
                TradeDate = date.ToDateTime(TimeOnly.MinValue),
                Name = item.TryGetProperty("securityAbbr", out var nEl) ? nEl.GetString() ?? "" : "",
                MarginBalance = GetNum(item, "rzye"),
                MarginBuy = GetNum(item, "rzmre"),
                ShortBalance = GetNum(item, "rqylje"),
                ShortVolume = GetNum(item, "rqyl"),
                FetchedAt = now,
            });
        }
        return rows;
    }

    // 深交所：xlsx
    private async Task<List<MarginDetailRow>> FetchSzseAsync(DateOnly date, DateTime now, CancellationToken ct)
    {
        var url = "https://www.szse.cn/api/report/ShowReport" +
                  $"?SHOWTYPE=xlsx&CATALOGID=1837_xxpl&txtDate={date:yyyy-MM-dd}&tab2PAGENO=1&random=0.1&TABKEY=tab2";
        byte[] bytes;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.szse.cn/disclosure/margin/margin/index.html");
            var resp = await _http.SendAsync(req, ct);
            bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接深交所两融接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
        if (bytes.Length == 0) return new List<MarginDetailRow>();   // 非交易日深交所可能返回空文件

        var rows = new List<MarginDetailRow>();
        using var ms = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(ms);
        bool header = true;
        while (reader.Read())
        {
            if (header) { header = false; continue; }
            if (reader.FieldCount < 7) continue;
            var code = (reader.GetValue(0)?.ToString() ?? "").Trim().PadLeft(6, '0');
            if (code.Length != 6 || !code.All(char.IsDigit)) continue;
            rows.Add(new MarginDetailRow
            {
                Code = code,
                TradeDate = date.ToDateTime(TimeOnly.MinValue),
                Name = reader.GetValue(1)?.ToString()?.Trim() ?? "",
                MarginBuy = ParseNum(reader.GetValue(2)),
                MarginBalance = ParseNum(reader.GetValue(3)),
                ShortVolume = ParseNum(reader.GetValue(5)),
                ShortBalance = ParseNum(reader.GetValue(6)),
                FetchedAt = now,
            });
        }
        return rows;
    }

    private static double GetNum(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el)) return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String) return ParseNum(el.GetString());
        return 0;
    }

    private static double ParseNum(object? value)
    {
        if (value == null || value is DBNull) return 0;
        if (value is double d) return d;
        var s = value.ToString()?.Replace(",", "").Trim() ?? "";
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
