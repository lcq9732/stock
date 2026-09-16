using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ExcelDataReader;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 抓取某交易日全市场融资融券明细——**交易所官方源**（最权威、非东财）。
/// 两所合并返回。按交易日拉，用 <see cref="RateLimiter"/> 限流（回补历史时按日多次调用）。
///
/// **上交所**：query.sse.com.cn/marketdata/tradedata/queryMargin.do（JSON）
///   stockCode / securityAbbr / rzye 融资余额 / rzmre 融资买入额 / **rzche 融资偿还额** /
///   rqyl 融券余量 / **rqmcl 融券卖出量** / **rqchl 融券偿还量** / rqylje 融券余额（**恒为 null**）
///
/// **深交所**：szse.cn/api/report/ShowReport（xlsx，数值带千分位逗号）——8 列：
///   0 代码 / 1 简称 / 2 融资买入额 / 3 融资余额 / **4 融券卖出量** / 5 融券余量 /
///   6 融券余额 / 7 融资融券余额（= 3+6，可算，不取）
///   深交所**没有**融资偿还额、融券偿还量这两项。
///
/// ⚠ 2026-09-16 补了三个字段（加粗那几个）。它们**一直都在响应里，只是没解析**——
/// 跟东财 columns=ALL 那次一个毛病（project_em_columns_all_dropped），加字段零新增请求。
/// 深市第 4 列（融券卖出量）原先读的是 0,1,2,3,5,6，把它跳过了。
///
/// ⚠ 沪市 rqylje 恒 null：以前用 GetNum 读成 0，导致 1675 只沪市票的融券余额**历史上
/// 从没有过非 0 值**。现在沪市这一项留 null，由本地按官方公式
/// 「融券余量 × 当日收盘价」补算（见 MarginShortBalanceFiller）——不在这里算，
/// 因为本类是纯网络层、不碰数据库，取收盘价得反查库。
///
/// ⚠ **不要为了字段更全就改用深交所的 JSON 接口**（SHOWTYPE=JSON）：它的单位是
/// 「亿元/万股/万元」混合，而 xlsx 是「元/股」，库里存的是元。换过去要做单位换算，
/// 实测两边数值确实对得上（平安银行 46.40 亿 = 4,640,555,891 元），但没必要冒这个风险。
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

    /// <summary>2010-03-31——融资融券**首批试点开市当天**（6家券商、90只标的）。两融这个业务在这天
    /// 之前不存在，所以两所也没有任何一天的明细可发。回补历史时从本地K线最早那天（1990-12-19）起跑
    /// 会白白空跑 4700 多个交易日，故把起点钉在这里。</summary>
    public DateOnly EarliestAvailable => new(2010, 3, 31);

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
                // 沪市 rqylje 恒为 null（实测 2026-09-14 全量如此）。**留 null 不写 0**——
                // 写 0 会让"没有这个字段"和"确实没有融券"变得无法区分，那正是之前的 bug。
                // 由 MarginShortBalanceFiller 按「融券余量 × 当日收盘价」补算。
                ShortBalance = GetNullableNum(item, "rqylje"),
                ShortVolume = GetNum(item, "rqyl"),
                MarginRepay = GetNullableNum(item, "rzche"),
                ShortSellVolume = GetNullableNum(item, "rqmcl"),
                ShortRepayVolume = GetNullableNum(item, "rqchl"),
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
                // 第 4 列 = 融券卖出量。2026-09-16 之前这一列被跳过（只读 0,1,2,3,5,6）。
                ShortSellVolume = ParseNum(reader.GetValue(4)),
                ShortVolume = ParseNum(reader.GetValue(5)),
                ShortBalance = ParseNum(reader.GetValue(6)),
                // 深交所这张表没有融资偿还额、融券偿还量 → 留 null，**不写 0**。
                // 深市要算融资净买入只能用余额差分（交易所页脚自己写的恒等式：
                // 本日融资余额 = 前日融资余额 + 本日买入 − 本日偿还）。
                MarginRepay = null,
                ShortRepayVolume = null,
                FetchedAt = now,
            });
        }
        return rows;
    }

    /// <summary>
    /// 跟 <see cref="GetNum"/> 的区别只在**缺字段/null 时返回 null 而不是 0**。
    /// 用于源头可能压根不给的项（沪市 rqylje 就是这样）——把"没有"写成 0 会让下游
    /// 永远分不清"这个市场不提供"和"当天是 0"，这是踩过的坑，见类注释。
    /// </summary>
    private static double? GetNullableNum(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String)
        {
            var sv = el.GetString();
            if (string.IsNullOrWhiteSpace(sv)) return null;
            return ParseNum(sv);
        }
        return null;
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
