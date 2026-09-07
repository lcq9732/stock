using System.Net.Http;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财板块抓取——成分股走**操作页面**这条路（2026-09-05 新增，第三条通道）。
///
/// 跟另外两条的区别只有一句话：**我们不自己拼 URL 发请求，而是打开板块页、点表头、点页码，
/// 把页面自己发出去的那些请求的响应截下来**。为什么非这样不可，见
/// <see cref="IBoardMemberPageScraper"/> 的注释——同一台机器同一时刻，导航到接口 URL 是 503，
/// 页面点页码是 200，差别在请求形状（JSONP script 请求 vs document 导航），服务端认这个。
///
/// ════ 这个类自己几乎没有逻辑 ════
/// 取数全交给 <see cref="IBoardMemberPageScraper"/>（实现是 <see cref="ChromeCdpBoardPageScraper"/>，
/// 用 CDP 驱动一个真的 Chrome/Edge）。
/// 这里只做三件事，而且都是**跟另外两条通道共用的**那部分：
///   ① 换板块之前歇一会儿（<see cref="EastMoneyBoardFetcherBase.PauseBetweenBoardsAsync"/>）；
///   ② 整个板块的抓取包在 <see cref="RateLimiter"/> 里，共用同一套熔断/退避；
///   ③ 拿回来的名单走 <see cref="EastMoneyBoardFetcherBase.ReconcileMembers"/> 对账——
///      **差一只都不写库**。这道检查三条通道必须一模一样，所以它在基类里只有一份。
///
/// ════ 板块列表仍然走 HttpClient ════
/// <see cref="GetAsync"/> 只服务于板块列表那条**回退路径**（主路是 sidemenu_new.json）。
/// 没让它也去操作页面：那一步现在压根不跑，为它写一套页面驱动是白费功夫。
/// </summary>
public class EastMoneyBoardPageFetcher : EastMoneyBoardFetcherBase
{
    private readonly IBoardMemberPageScraper _scraper;
    private readonly HttpClient _http;

    public EastMoneyBoardPageFetcher(RateLimiter limiter, IBoardMemberPageScraper scraper,
                                     HttpClient? httpClient = null,
                                     string? bindNetworkInterface = null,
                                     TimeSpan? boardSwitchPause = null)
        : base(limiter, memberHost: null, boardSwitchPause: boardSwitchPause)
    {
        _scraper = scraper;
        // 把 scraper 那边的动静接到程序日志上——验证提示、频率统计、重试都在那儿发出来
        _scraper.OnStatus += Report;
        _http = httpClient ?? new HttpClient(
            NetworkInterfaceBinder.CreateHandler(bindNetworkInterface, Report));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    /// <summary>
    /// 日志里只说**这一轮实际走的是哪条路**，不解释这条路是怎么回事——
    /// 那些属于代码注释，写进运行日志只会把真正的执行记录淹掉（2026-09-06 用户指出）。
    /// </summary>
    // 具体是哪个浏览器由 scraper 自己在就绪时报（"页面通道已就绪：chrome.exe…"），
    // 这里不重复、更不能写死内核——2026-09-06 从 WebView2 换成真 Chrome 之后，
    // 这行还写着"Edge 内核"，日志就在骗人。
    public override string DescribeChannel() => _scraper.IsReady
        ? "成分股：页面通道（真浏览器）"
        : "成分股：页面通道未就绪，本轮抓不了";

    /// <summary>
    /// 建浏览器、打开东财页面拿 Cookie。**要在开抓之前调一次**——初始化要几秒，
    /// 不该混在第一个板块的计时里。
    /// </summary>
    public override async Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        try
        {
            var ok = await _scraper.EnsureReadyAsync(ct);
            // 通道起来之后把观察窗打开：抓取期间人能看见东财那边在发生什么
            // （表格在翻页？弹了图片验证码？页面打不开？），不用等日志报错再猜。
            if (ok) await _scraper.ShowWorkWindowAsync(ct);
            return ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"⚠ 页面通道初始化失败（{ex.Message}）——这一轮成分股抓不了。");
            return false;
        }
    }

    /// <summary>
    /// 一个板块的成分股：交给 scraper 去操作页面，回来的名单照旧对账。
    ///
    /// 整个板块（可能是好几页、好几个请求）**包在一次 <see cref="RateLimiter"/> 调用里**，
    /// 而不是每页包一次：页与页之间的节奏归 scraper 管（它才知道什么时候点下一页），
    /// 限流器在这一层管的是板块与板块之间的节奏、以及连续失败之后的熔断退避。
    /// </summary>
    public override async Task<List<string>> FetchMembersAsync(
        string boardCode, CancellationToken ct = default)
    {
        await PauseBetweenBoardsAsync(ct);

        var (codes, total) = await Limiter.RunAsync(
            () => _scraper.ScrapeMembersAsync(boardCode, ct), ct);

        return ReconcileMembers(boardCode, codes, total);
    }

    /// <summary>板块列表（回退路径）用的普通 HTTP 取数——成分股不走这里。</summary>
    protected override async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var body = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(body))
                throw new RateLimitedException("东财返回空响应（典型的限流表现）。");
            return body;
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException(
                $"东财连接被断开：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }
}
