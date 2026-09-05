using System.Net.Http;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财板块抓取——**走 WebView2 浏览器通道**（HttpClient 只当回退）。
///
/// ⚠ 下面整段"push2 第 7 个请求就被切"的实测，讲的是**打 push2** 那会儿的事。
/// 2026-09-05 起成分股默认改打 <see cref="EastMoneyBoardFetcherBase.DefaultMemberHost"/>
/// （pushguest，行情中心板块页翻页时真正打的那个），新域名的脾气还没验证，
/// 所以这条浏览器通道**暂时仍是默认**——它顺带能测出新域名会不会弹图片验证码。
/// 业务逻辑（URL、翻页、total 对账）全在 <see cref="EastMoneyBoardFetcherBase"/>，
/// 这个类只负责一件事：怎么把一个 URL 变成一段 JSON。
///
/// ════ 为什么当初要浏览器内核 ════
/// 2026-09-04 实测：同一 IP、同一接口、相近时间，**浏览器 90% 成功、27 个/分钟，
/// 而 HttpClient 第 7 个请求就被切、折算只有 4.8 个/分钟**。请求头、Cookie、连接复用、
/// HTTP/2 都单独排除过，差别在 TLS 指纹——Chrome 的 ClientHello 跟 .NET 的 Schannel 不同，
/// 而 .NET 改不了这个。详见 <see cref="IBrowserJsonFetcher"/>。
///
/// ════ ⚠ 2026-09-05：那个前提可能已经不成立了 ════
/// 同一台机器复测（程序没绑网卡，走的就是默认路由），普通 HttpClient 打 push2：
/// **连发 100 个零失败**，加上 10 个 JSONP 形态的也全过，累计 110 个请求一个没被拒。
/// 而这条浏览器通道本身正是图片验证码的来源——导航取数会把验证页叫出来，人不在就卡住。
///
/// **但那次复测是周六（非交易日）跑的**，push2 盘中负载完全是另一回事，所以现在两条路
/// 并存、可切换，而不是直接换掉：
///   · 这个类＝浏览器通道（现状，稳妥）；
///   · <see cref="EastMoneyBoardHttpFetcher"/>＝纯 HttpClient（快，但可能在盘中被限）。
/// 交易日盘中跑一次 <c>doc/push2-reachability-probe.ps1</c> 再决定默认用哪个。
/// 切换在 <c>fetcher-settings.json</c> 里改 <c>BoardMemberChannel</c>，不用改代码。
/// </summary>
public class EastMoneyBoardFetcher : EastMoneyBoardFetcherBase
{
    private readonly HttpClient _http;
    private readonly string? _bindNetworkInterface;
    private readonly IBrowserJsonFetcher? _browser;

    /// <param name="bindNetworkInterface">
    /// 把 push2 的请求钉在这块网卡上出去（2026-09-04），填网卡名如 "Wi-Fi"；留空＝走默认路由。
    ///
    /// 为什么单给 push2 开这个口子：本机有线接的是公司网，网关按域名把 push2 拦了
    /// （TCP 和 TLS 都通、一发 HTTP 请求就被切断，0 字节），而 datacenter 那边一直正常。
    /// 换一条没限制的链路（另一个 Wi-Fi、或手机热点）就能通，见 <see cref="NetworkInterfaceBinder"/>。
    /// </param>
    /// <param name="browser">
    /// 浏览器通道。给了就优先走它，没给或没就绪就退回 HttpClient。
    /// </param>
    public EastMoneyBoardFetcher(RateLimiter limiter, HttpClient? httpClient = null,
                                 string? bindNetworkInterface = null,
                                 IBrowserJsonFetcher? browser = null,
                                 string? memberHost = null,
                                 TimeSpan? boardSwitchPause = null)
        : base(limiter, memberHost, boardSwitchPause)
    {
        _browser = browser;
        _http = httpClient ?? new HttpClient(
            NetworkInterfaceBinder.CreateHandler(bindNetworkInterface, Report));
        _bindNetworkInterface = bindNetworkInterface;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    /// <summary>
    /// 浏览器通道（如果配了）准备好没。**要在真正开抓之前调一次**——初始化要几秒
    /// （建 WebView2 + 打开东财页面拿 Cookie），不该混在第一个请求的计时里。
    /// 用不了会自己退回 HttpClient，返回 false 只是告诉调用方"这轮走的是慢路"。
    /// </summary>
    public override async Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        if (_browser == null) return false;
        try
        {
            var ok = await _browser.EnsureReadyAsync(ct);
            // 通道起来之后把观察窗打开：抓取期间人能看见东财那边在发生什么
            // （数据在刷？弹了图片验证码？页面打不开？），不用等日志报错再猜。
            if (ok) await _browser.ShowWorkWindowAsync(ct);
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"⚠ 浏览器通道初始化失败（{ex.Message}），退回普通 HTTP 抓取。");
            return false;
        }
    }

    public override string DescribeChannel() => _browser is { IsReady: true }
        ? $"{MemberHost} 走浏览器通道（Edge 内核）"
        : $"{MemberHost} 走普通 HTTP 抓取" + (_browser == null ? "" : "（浏览器通道没就绪）");

    /// <summary>
    /// 当前走哪块网卡、有没有配对。**调用方要在订阅 OnStatus 之后自己打进日志**——
    /// 这段话原来是在构造函数里发的，那时候还没人订阅，消息就丢了，
    /// 结果配了网卡也看不出有没有生效（2026-09-04 踩过）。
    /// </summary>
    public override string DescribeBinding() => NetworkInterfaceBinder.Describe(_bindNetworkInterface);

    protected override async Task<string> GetAsync(string url, CancellationToken ct)
    {
        // 浏览器通道优先：同一 IP 同一接口，它 90% 成功而 HttpClient 第 7 个就被切（见类注释）。
        // 没配、没就绪、或者它自己失败了，都落回下面的 HttpClient——慢，但至少能抓。
        if (_browser is { IsReady: true })
        {
            try
            {
                var viaBrowser = await _browser.GetJsonAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(viaBrowser)) return viaBrowser;
                throw new RateLimitedException($"东财 {MemberHost} 经浏览器通道返回空（典型的限流表现）。");
            }
            catch (RateLimitedException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new RateLimitedException(
                    $"东财 {MemberHost} 经浏览器通道取数失败：{ex.Message}", ex);
            }
        }

        try
        {
            var body = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(body))
                throw new RateLimitedException($"东财 {MemberHost} 返回空响应（典型的限流表现）。");
            return body;
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            // push2 限流时不是返回 HTTP 错误码，而是直接断开连接
            throw new RateLimitedException(
                $"东财 {MemberHost} 连接被断开：{ex.InnerException?.Message ?? ex.Message}"
                + "（push2 限流很敏感，可在浏览器访问一次 quote.eastmoney.com 过人工验证后重试）", ex);
        }
    }
}
