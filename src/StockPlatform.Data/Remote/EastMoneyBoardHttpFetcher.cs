using System.Net;
using System.Net.Http;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财板块抓取——**纯 HttpClient**，不碰浏览器（2026-09-05 新增）。
/// 业务逻辑（URL、翻页、total 对账）全在 <see cref="EastMoneyBoardFetcherBase"/>，
/// 这个类只负责一件事：怎么把一个 URL 变成一段 JSON。
///
/// ════ 为什么加这条路 ════
/// 兄弟类 <see cref="EastMoneyBoardFetcher"/> 走 WebView2，是为了绕开 2026-09-04 实测的
/// "HttpClient 打 push2 第 7 个请求就被切"。但那条路自己带来了更大的麻烦：**导航取数会把
/// 东财的图片验证码叫出来**，人不在电脑前就卡在那儿，1000 个板块跑一天只拿下 207 个。
///
/// 2026-09-05 在同一台机器上复测（程序没绑网卡——<c>fetcher-settings.json</c> 是空的，
/// <c>Push2NetworkInterface</c> 为空，走的就是默认路由，跟这个类完全等价）：
///
/// | 形态 | 请求数 | 结果 |
/// |---|---|---|
/// | 跟 <c>FetchMembersAsync</c> 一模一样的 URL，间隔 1.2 秒 | 100 | **100/100 全过** |
/// | 带 <c>cb=</c> 回调参数（JSONP 形态） | 10 | **10/10 全过** |
///
/// 累计 110 个请求零失败——"第 7 个就被切"这个前提在当时那个时点不成立了。
///
/// ════ ⚠ 但还不能当默认 ════
/// **那次复测是周六（非交易日）跑的**，而用户撞上验证码都是在交易日；push2 盘中负载
/// 完全是另一回事。所以两条路并存、可切换，交易日盘中跑一次
/// <c>doc/push2-reachability-probe.ps1</c> 再定默认值：
///   · 100/100 全过 → 把默认换成这个类，那 2500 个成分股请求从此不用人守着；
///   · 中途开始被拒 → 老结论仍然成立，继续用浏览器通道。
///
/// 切换在 <c>fetcher-settings.json</c> 里改 <c>BoardMemberChannel</c>（<c>"http"</c> / <c>"browser"</c>），
/// 不用改代码重新发布。
///
/// ════ 限流交给谁管 ════
/// 这个类自己不做节流——节奏由传进来的 <see cref="RateLimiter"/> 定，跟浏览器通道那条路
/// 用的是同一套熔断/退避逻辑。它比浏览器通道能跑得快些：**没有页面加载那 1~3 秒**，
/// 也不需要等验证脚本跑完，所以调用方给的间隔可以更小（见 App.xaml.cs 的接线）。
/// </summary>
public class EastMoneyBoardHttpFetcher : EastMoneyBoardFetcherBase
{
    private readonly HttpClient _http;
    private readonly string? _bindNetworkInterface;

    /// <param name="bindNetworkInterface">
    /// 把请求钉在这块网卡上出去，填网卡名如 "Wi-Fi"；留空＝走默认路由（眼下就是空的）。
    /// 留着这个口子是因为公司网关曾经按域名把 push2 拦了（TCP/TLS 都通、一发请求就被切断），
    /// 换一条没限制的链路就能通，见 <see cref="NetworkInterfaceBinder"/>。
    /// </param>
    public EastMoneyBoardHttpFetcher(RateLimiter limiter, HttpClient? httpClient = null,
                                     string? bindNetworkInterface = null)
        : base(limiter)
    {
        _bindNetworkInterface = bindNetworkInterface;
        _http = httpClient ?? new HttpClient(
            NetworkInterfaceBinder.CreateHandler(bindNetworkInterface, Report));

        // 请求头照着真实浏览器配。单独测过它们不是成败关键（2026-09-04 排除过），
        // 但既然是在模仿浏览器，就别在这种地方留下不必要的差异。
        // Referer 尤其要有：这个接口本来就是给 quote 页面用的，页面自己调它时一定带着。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    public override string DescribeChannel() => "push2 走纯 HttpClient（不用浏览器、不会弹验证）";

    public override string DescribeBinding() => NetworkInterfaceBinder.Describe(_bindNetworkInterface);

    protected override async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var body = await _http.GetStringAsync(url, ct);

            // 空响应是 push2 限流的一种表现（另一种是直接断连，走下面的 catch）。
            // 不能当成"没数据"返回上去——上游会把它当成"这个板块是空的"写进库。
            if (string.IsNullOrWhiteSpace(body))
                throw new RateLimitedException("东财 push2 返回空响应（典型的限流表现）。");
            return body;
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            // push2 限流时不返回 HTTP 错误码，而是直接断开连接
            throw new RateLimitedException(
                $"东财 push2 连接被断开：{ex.InnerException?.Message ?? ex.Message}"
                + "（这条是纯 HttpClient 通道；如果盘中持续这样，说明老的限流结论仍然成立，"
                + "把 fetcher-settings.json 的 BoardMemberChannel 改回 \"browser\"）", ex);
        }
    }
}
