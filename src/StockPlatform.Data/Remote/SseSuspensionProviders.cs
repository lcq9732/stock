using System.Globalization;
using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 上交所官网「交易提示·停复牌信息」背后的两个查询的共用部分（2026-09-30，见 doc/suspension-design.md）：
/// HTTP（要 Referer、走系统代理）、JSONP 外壳、按月查、自己翻页。
///
/// ════ 接口行为（2026-09-30 沙箱实测）════
/// · 按日期**范围**查，返回的是跟这个范围**有交集**的全部停牌——开始得早、还没复牌的也在里面
///   （基金那个查询 2026-09 返回了 2021-10 起停牌至今的 501023）。所以按月问会重复拿到长期停牌，
///   UPSERT 按键去重就行。
/// · 日期参数必须是 <c>yyyyMMdd</c>，给 yyyy-MM-dd 回 <c>success:false</c> + 格式错误。
/// · pageSize 给 1000 照给；超了看 <c>pageHelp.total</c> 自己翻页。
/// · 出错时整个响应包在一对圆括号里（<c>({"success":"false",...})</c>），正常时没有。
/// · 2010 年起有数据（2005-01 空、2010-01 有 221 条）。
/// </summary>
public abstract class SseSuspensionProviderBase : ISuspensionProvider
{
    private const int PageSize = 1000;

    /// <summary>一个月翻到这么多页还没完就是接口语义变了（按月最多几百条），别无限翻下去。</summary>
    private const int MaxPages = 20;

    private readonly RateLimiter _rateLimiter;
    private readonly HttpClient _http;

    public abstract string Source { get; }
    public abstract string Label { get; }

    /// <summary>2004 年起问：两所都是 2008~2010 年才有记录，早几年只是几十个空请求，换来不用猜起点。</summary>
    public DateOnly FirstMonth => new(2004, 1, 1);

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    protected SseSuspensionProviderBase(RateLimiter rateLimiter, HttpClient? httpClient)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>跟 <see cref="SseEtfShareProvider"/> 一样走系统代理——用户环境要靠它才出得去。</summary>
    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>某一页的完整 URL（区间是整个自然月）。</summary>
    protected abstract string BuildUrl(DateOnly from, DateOnly to, int pageNo);

    /// <summary>一行 → 记录；不要的行（债券、代码不是 6 位）返回 null。</summary>
    protected abstract SuspensionRow? Map(JsonElement item);

    public async Task<List<SuspensionRow>> GetMonthAsync(DateOnly month, CancellationToken ct = default)
    {
        var from = new DateOnly(month.Year, month.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var rows = new List<SuspensionRow>();
        for (int page = 1; ; page++)
        {
            var txt = await _rateLimiter.RunAsync(() => GetAsync(BuildUrl(from, to, page), ct), ct);
            var (items, total) = ParsePage(txt, Label);
            rows.AddRange(items.Select(Map).OfType<SuspensionRow>());
            int seen = (page - 1) * PageSize + items.Count;
            if (seen >= total || items.Count == 0) break;
            if (page >= MaxPages)
                throw new InvalidOperationException($"{Label} {from:yyyy-MM} 翻了 {page} 页还没完（共 {total} 条）");
        }
        return rows;
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.sse.com.cn/");   // 不带这个会被拦
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new RateLimitedException($"上交所返回 {(int)resp.StatusCode}，疑似触发限流");
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException($"无法连接上交所停复牌接口：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解析一页：返回这一页的原始行（克隆出来，脱离 JsonDocument 的生命周期）和服务端自报的总数。
    ///
    /// ⚠ 空列表和"响应不对"必须分开：<c>success:false</c>、不是 JSON、没有 <c>pageHelp.data</c> 都抛——
    /// 当成空的话那个月会被记成"问过了、一条都没有"，以后再也不问。
    /// </summary>
    public static (List<JsonElement> Items, int Total) ParsePage(string txt, string label)
    {
        var s = txt.Trim();
        if (s.StartsWith('(') && s.EndsWith(')')) s = s[1..^1];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(s); }
        catch (JsonException ex)
        {
            throw new RateLimitedException($"{label}停复牌接口返回的不是 JSON，疑似被拦截", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.String
                && ok.GetString() == "false")
                throw new InvalidOperationException(
                    $"{label}停复牌接口报错：{(root.TryGetProperty("errorMsg", out var m) ? m.ToString() : "（没有说明）")}");

            if (!root.TryGetProperty("pageHelp", out var ph)
                || !ph.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new RateLimitedException($"{label}停复牌响应里没有 pageHelp.data，疑似被拦截");

            int total = ph.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt32() : data.GetArrayLength();
            return (data.EnumerateArray().Select(e => e.Clone()).ToList(), total);
        }
    }

    protected static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    /// <summary>yyyyMMdd；空＝没有（还没复牌）。别的格式抛——接口改了格式，宁可整月报错也不能猜。</summary>
    protected static DateOnly? Day(string text, string label) =>
        text.Length == 0 || text == "null" ? null
        : DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : throw new InvalidOperationException($"{label}停复牌日期认不出来：{text}");
}

/// <summary>
/// 上交所「停复牌信息·股票和可转债」（<c>commonSoaQuery.do?sqlId=GW_PL_JYTS_TFPXX</c>）。
/// 股票、债券（GB）、可转债（CB）都在里面，**全部存下**（2026-09-30 用户定：取到的全存），
/// 品种记在 <see cref="SuspensionRow.ControlType"/>。体检按 Bar 代码查，债券的行自然用不上。
/// </summary>
public sealed class SseStockSuspensionProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    : SseSuspensionProviderBase(rateLimiter, httpClient)
{
    public override string Source => SuspensionSource.SseStock;
    public override string Label => "上交所·股票";

    protected override string BuildUrl(DateOnly from, DateOnly to, int pageNo) =>
        "https://query.sse.com.cn/commonSoaQuery.do?isPagination=true&sqlId=GW_PL_JYTS_TFPXX"
        + $"&pageHelp.pageSize=1000&pageHelp.pageNo={pageNo}&productCode=&keyWords="
        + $"&startStopDate={from:yyyyMMdd}&endStopDate={to:yyyyMMdd}";

    protected override SuspensionRow? Map(JsonElement item) => MapStock(item);

    /// <summary>公开出来给单测拿真实响应样本验。</summary>
    public static SuspensionRow? MapStock(JsonElement item)
    {
        var code = Str(item, "productCode");
        if (code.Length == 0) return null;   // 连代码都没有的行存不了（没有键）
        return new SuspensionRow(
            SuspensionSource.SseStock, "sh", code, Str(item, "productName"),
            Day(Str(item, "startStopDate"), "上交所·股票"), "",
            Day(Str(item, "endStopDate"), "上交所·股票"), "",
            Kind: Str(item, "type"), StopTime: Str(item, "stopTime"), Reason: Str(item, "stopReason"),
            EndReason: Str(item, "endStopReason"), ControlType: Str(item, "controlType"));
    }
}

/// <summary>
/// 上交所「停复牌信息·基金」（<c>sseQuery/commonSoaQuery.do?sqlId=SSE_PL_JYTS_TFPXX_JJ</c>），含 ETF、LOF。
/// 字段名跟股票那个不一样（secCode / startStopType / startStopReason）。
/// </summary>
public sealed class SseFundSuspensionProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    : SseSuspensionProviderBase(rateLimiter, httpClient)
{
    public override string Source => SuspensionSource.SseFund;
    public override string Label => "上交所·基金";

    protected override string BuildUrl(DateOnly from, DateOnly to, int pageNo) =>
        "https://query.sse.com.cn/sseQuery/commonSoaQuery.do?isPagination=true&sqlId=SSE_PL_JYTS_TFPXX_JJ"
        + "&secCode=&stopReason=&order=startStopDate%7Cdesc%2CsecCode%7Casc"
        + $"&startDate={from:yyyyMMdd}&endDate={to:yyyyMMdd}"
        + $"&pageHelp.pageSize=1000&pageHelp.pageNo={pageNo}&pageHelp.beginPage={pageNo}"
        + $"&pageHelp.cacheSize=1&pageHelp.endPage={pageNo}";

    protected override SuspensionRow? Map(JsonElement item) => MapFund(item);

    public static SuspensionRow? MapFund(JsonElement item)
    {
        var code = Str(item, "secCode");
        if (code.Length == 0) return null;   // 连代码都没有的行存不了（没有键）
        return new SuspensionRow(
            SuspensionSource.SseFund, "sh", code, Str(item, "secAbbr"),
            Day(Str(item, "startStopDate"), "上交所·基金"), "",
            Day(Str(item, "endStopDate"), "上交所·基金"), "",
            Kind: Str(item, "startStopType"), StopTime: Str(item, "stopTime"), Reason: Str(item, "startStopReason"),
            EndReason: Str(item, "endStopReason"),
            EndKind: Str(item, "endStopType"), StartType: Str(item, "startType"), EndType: Str(item, "endType"),
            DateSource: Str(item, "dateSource"), FullName: Str(item, "expandAbbr"));
    }
}
