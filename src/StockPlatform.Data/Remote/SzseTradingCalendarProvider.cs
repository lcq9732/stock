using System.Globalization;
using System.Net;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 交易日历——**深交所官网**按月接口（2026-09-08）：
/// <c>www.szse.cn/api/report/exchange/onepersistenthour/monthList?month=yyyy-MM</c>，要带
/// <c>Referer: https://www.szse.cn/</c>。返回 <c>{"data":[{"jyrq":"2026-09-01","jybz":"1"},...]}</c>，
/// <c>jyrq</c>＝日期、<c>jybz</c>＝1 是交易日 / 0 不是。
///
/// ════ 覆盖范围（2026-09-08 沙箱实测，逐月二分确认）════
/// **2005-01 起**才有；2004-12 及更早一律返回空数组，不是请求失败。所以 2004 年及以前那段
/// 由本地全市场K线归纳（见 <see cref="Logic.Abstractions.ITradingDayRepository.LocalSource"/>），
/// 那是死历史、建一次就固定。
/// 往后能拿到**已公布**的部分：实测 2026-12 有、2027 全空——交易所每年底才发下一年的日历，
/// 所以 12 月起要试着拉下一年，拉到空不算错。
///
/// 单独成类而不是并进 <see cref="ExchangeMarginProvider"/>：一个数据通道一个类（项目规矩），
/// 而且这个只用深交所、跟那边"沪深两所合并"的形态不一样。
/// </summary>
public class SzseTradingCalendarProvider : ITradingCalendarProvider
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    /// <summary>深交所这个接口给得到的最早月份（2005-01）。更早的返回空数组，不是失败。</summary>
    public static readonly DateOnly SzseEarliestMonth = new(2005, 1, 1);

    /// <inheritdoc/>
    public DateOnly EarliestMonth => SzseEarliestMonth;

    public SzseTradingCalendarProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
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

    /// <summary>
    /// 取某个月的**交易日**（jybz=1 的那些）。返回空列表＝这个月交易所还没发布/不覆盖，不是错误；
    /// 请求本身失败会抛 <see cref="RateLimitedException"/>——两者必须分得开，
    /// 否则"网络抽风"会被当成"这个月没有交易日"写进日历。
    /// </summary>
    public Task<List<DateOnly>> GetMonthAsync(int year, int month, CancellationToken ct = default)
        => _rateLimiter.RunAsync(() => FetchMonthAsync(year, month, ct), ct);

    private async Task<List<DateOnly>> FetchMonthAsync(int year, int month, CancellationToken ct)
    {
        var url = "https://www.szse.cn/api/report/exchange/onepersistenthour/monthList" +
                  $"?month={year:D4}-{month:D2}&random={Random.Shared.NextDouble():F16}";
        string json;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri("https://www.szse.cn/");
            var resp = await _http.SendAsync(req, ct);
            json = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException($"无法连接深交所交易日历接口：{detail}（可能是代理/网络问题）", ex);
        }
        if (string.IsNullOrWhiteSpace(json))
            throw new RateLimitedException("深交所交易日历接口返回空响应");

        var days = new List<DateOnly>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new RateLimitedException("深交所交易日历接口返回的不是预期结构（没有 data 数组）");

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("jyrq", out var dayEl) || !item.TryGetProperty("jybz", out var flagEl))
                continue;
            if (flagEl.GetString() != "1") continue;      // 0＝非交易日，不入库
            if (DateOnly.TryParseExact(dayEl.GetString() ?? "", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                days.Add(d);
        }
        return days;
    }
}
