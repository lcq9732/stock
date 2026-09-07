using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 个股分档资金流（东财 <c>push2his.eastmoney.com</c>）。
///
/// 这是唯一不走 datacenter 的东财 provider——分档资金流没有对应的报表接口，只有行情侧的
/// <c>fflow/daykline</c>。<c>push2his</c> 跟限流最凶的 <c>push2</c> 是**不同域名、独立计数**，
/// 主环境可达（push2 需要人工过反爬验证，push2his 不需要）。
///
/// 两个必须知道的限制：
///   1. <b>只返回最近约 120 个交易日</b>，<c>lmt=0</c> 也突破不了。所以这份数据的历史深度
///      得靠定期抓取慢慢养，一次抓不出长历史。
///   2. <b>只能按股票查</b>，没有"某天全市场"的入口。全市场一轮就是 5500+ 个请求，
///      按 1 秒间隔约 1.5 小时——所以调用方应该支持"只抓关注的股票"。
/// </summary>
public class EastMoneyMoneyFlowProvider
{
    private const string BaseUrl = "https://push2his.eastmoney.com/api/qt/stock/fflow/daykline/get";

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus;

    /// <summary>
    /// 限流熔断还要等到几点；没在暂停就是 null。
    /// 用来在暂停期里直接回绝新的抓取——实测暂停期内点【执行】会干等 8 分钟才报失败，
    /// 那 8 分钟既没数据也看不出在等什么。
    /// </summary>
    public DateTime? PausedUntil => _rateLimiter.PausedUntil;

    public EastMoneyMoneyFlowProvider(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(25);
        _rateLimiter.OnStatus += s => OnStatus?.Invoke(s);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>抓一只股票的分档资金流（最近约 120 个交易日）。</summary>
    public async Task<List<NetInflowDetail>> FetchAsync(string code, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?lmt=0&klt=101&secid={SecId(code)}&fields1=f1,f2,f3,f7" +
                  "&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61,f62,f63,f64,f65";

        string body;
        try
        {
            body = await _rateLimiter.RunAsync(() => _http.GetStringAsync(url, ct), ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException(
                $"无法连接东财资金流接口（{code}）：{ex.InnerException?.Message ?? ex.Message}", ex);
        }

        var result = new List<NetInflowDetail>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object) return result;      // 停牌/退市/无数据
        if (!data.TryGetProperty("klines", out var klines) ||
            klines.ValueKind != JsonValueKind.Array) return result;

        var now = DateTime.Now;
        foreach (var line in klines.EnumerateArray())
        {
            var parts = (line.GetString() ?? "").Split(',');
            // f51..f63 共 13 个是有意义的（f64/f65 未用），少于 13 段的行直接跳过
            if (parts.Length < 13) continue;
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var day)) continue;

            result.Add(new NetInflowDetail
            {
                Code = code,
                TradeDate = day,
                MainNet = D(parts[1]),
                SmallNet = D(parts[2]),
                MidNet = D(parts[3]),
                BigNet = D(parts[4]),
                SuperNet = D(parts[5]),
                MainRatio = D(parts[6]),
                SmallRatio = D(parts[7]),
                MidRatio = D(parts[8]),
                BigRatio = D(parts[9]),
                SuperRatio = D(parts[10]),
                ClosePrice = D(parts[11]),
                ChangeRate = D(parts[12]),
                FetchedAt = now,
            });
        }
        return result;
    }

    /// <summary>
    /// 东财的 secid 前缀。**交给 <see cref="MarketClassifier"/> 判，别再在这儿自己写一份**——
    /// 这里原来是 "6 或 9 开头＝沪市 1.，其余 0."，而 <b>920xxx 是北交所</b>（2024-2025 代码迁移
    /// 之后北交所基本都是 92 开头），被当成沪市之后请求发出去是 <c>1.920000</c>，
    /// 东财回 <c>rc:100 / data:null</c>——**没有异常、没有报错，就是没数据**。
    ///
    /// 后果是全库 342 只 920 开头的票**一行分档资金流都抓不到**，而界面上只表现为
    /// "还有 342 只从没抓过"这个数字一直不动，谁也看不出是 secid 拼错了
    /// （2026-09-06 查出来；同样形状的坑 K线那边早就踩过，MarketClassifier 就是那次建的）。
    /// </summary>
    public static string SecId(string code) => MarketClassifier.EastMoneySecIdPrefix(code) + code;

    private static double? D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
