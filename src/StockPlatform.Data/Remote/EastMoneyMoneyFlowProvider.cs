using System.Net;
using System.Net.Http;
using StockPlatform.Logic.Abstractions;
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
public class EastMoneyMoneyFlowProvider : IMoneyFlowDetailFetcher
{
    /// <summary>域名。URL 的其余部分跟浏览器通道共用一份（见 <see cref="MoneyFlowKlineParser.BuildUrl"/>）。</summary>
    public const string Host = "push2his.eastmoney.com";

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
        var url = MoneyFlowKlineParser.BuildUrl(Host, code);

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

        return MoneyFlowKlineParser.Parse(code, body, DateTime.Now);
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

    // ─────────────── IMoneyFlowDetailFetcher 的通道自述 ───────────────

    public string ChannelName => "HttpClient 直连 push2his";

    /// <summary>连续 15 只失败就收尾（这条通道的老阈值，2026-09-12 起一直是它）。</summary>
    public int GiveUpAfterConsecutiveFailures => 15;

    /// <summary>
    /// ⚠ 别一口咬定"被限流"：2026-09-11 公司网关按域名把 push2his 整个拦了
    /// （TCP/TLS 都通、一发请求就被切、收到 0 字节），日志却写着"判定被限流"——
    /// 把人往"等一会儿就好了"的方向带，实际等多久都不会好。两者处置完全相反。
    /// </summary>
    public string ExhaustedVerdict(string? lastError) =>
        lastError?.Contains("无法连接") == true
            ? "这条链路连不上 push2his（多半是网关按域名拦了：TCP 通、一发请求就被切）。"
              + "这台机器上该把 MoneyFlowBackfillTransport 切成 browser——换网络也解决不了域名拦截"
            : "判定被限流（等一段时间会自己恢复）";
}
