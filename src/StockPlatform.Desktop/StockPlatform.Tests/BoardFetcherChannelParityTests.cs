using System.Net;
using System.Text;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 两条取数通道的**等价性**（2026-09-05）——浏览器通道版 <see cref="EastMoneyBoardFetcher"/>
/// 和纯 HttpClient 版 <see cref="EastMoneyBoardHttpFetcher"/> 喂同一份响应，结果必须一模一样。
///
/// 为什么要专门钉这个：两个类是给人切换用的（fetcher-settings.json 里的 BoardMemberChannel），
/// 而"换条通道抓出来的数据不一样"是最难查的一类问题——切过去之后库里的板块成分悄悄变了，
/// 没有任何报错。业务逻辑现在共用 <see cref="EastMoneyBoardFetcherBase"/> 一份，
/// 这些测试就是拦住"以后有人图省事在某个子类里覆盖一下"。
///
/// 顺带钉住两条通道各自该有的差异：<see cref="EastMoneyBoardHttpFetcher"/> 不需要预热，
/// 而 <see cref="EastMoneyBoardFetcher"/> 没有浏览器时要能自己退回 HttpClient。
/// </summary>
public class BoardFetcherChannelParityTests
{
    /// <summary>按顺序吐预设响应的假 handler：第 N 个请求拿第 N 条。</summary>
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private int _n;

        /// <summary>实际请求过的 URL，按顺序。用来钉住域名和翻页参数。</summary>
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            var body = _n < responses.Length ? responses[_n] : responses[^1];
            _n++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static RateLimiter NoWait() =>
        new(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero);

    /// <summary>浏览器通道版，但**不给浏览器**——于是它走 HttpClient 回退，跟 http 版可比。</summary>
    private static EastMoneyBoardFetcher Browser(params string[] responses) =>
        new(NoWait(), new HttpClient(new ScriptedHandler(responses)),
            boardSwitchPause: TimeSpan.Zero);

    private static EastMoneyBoardHttpFetcher Http(params string[] responses) =>
        new(NoWait(), new HttpClient(new ScriptedHandler(responses)),
            boardSwitchPause: TimeSpan.Zero);

    /// <summary>一页成分股：total 报 <paramref name="total"/>，这一页给 <paramref name="count"/> 只。</summary>
    private static string MemberPage(int total, int count, int startIndex)
    {
        var items = Enumerable.Range(startIndex, count).Select(i =>
            "{\"f12\":\"" + i.ToString("D6") + "\",\"f14\":\"股票" + i + "\"}");
        return "{\"rc\":0,\"data\":{\"total\":" + total + ",\"diff\":[" + string.Join(",", items) + "]}}";
    }

    /// <summary>一页板块列表。</summary>
    private static string ListPage(int total, int count, int startIndex)
    {
        var items = Enumerable.Range(startIndex, count).Select(i =>
            "{\"f3\":1.5,\"f6\":1000,\"f12\":\"BK" + i.ToString("D4") + "\",\"f14\":\"板块" + i
            + "\",\"f128\":\"龙头\",\"f140\":\"600000\"}");
        return "{\"rc\":0,\"data\":{\"total\":" + total + ",\"diff\":[" + string.Join(",", items) + "]}}";
    }

    [Fact]
    public async Task 成分股_两条通道结果完全一致()
    {
        var pages = new[] { MemberPage(150, 100, 1), MemberPage(150, 50, 101) };

        var viaBrowser = await Browser(pages).FetchMembersAsync("BK0477");
        var viaHttp = await Http(pages).FetchMembersAsync("BK0477");

        Assert.Equal(150, viaHttp.Count);
        Assert.Equal(viaBrowser, viaHttp);        // 顺序也要一样：翻页顺序影响不了结果
    }

    [Fact]
    public async Task 板块列表_两条通道结果完全一致()
    {
        var pages = new[] { ListPage(150, 100, 1), ListPage(150, 50, 101) };

        var viaBrowser = await Browser(pages).FetchBoardListAsync(BoardType.Concept);
        var viaHttp = await Http(pages).FetchBoardListAsync(BoardType.Concept);

        Assert.Equal(150, viaHttp.Count);
        Assert.Equal(viaBrowser.Select(b => b.BoardCode), viaHttp.Select(b => b.BoardCode));
        Assert.Equal(viaBrowser.Select(b => b.Name), viaHttp.Select(b => b.Name));
    }

    [Fact]
    public async Task 半截名单_两条通道都要拒收()
    {
        // total 报 150 却只给得出 100 —— 这份名单不完整，写进库会被上游当成"其余的已下架"
        var halfway = new[] { MemberPage(150, 100, 1), MemberPage(150, 0, 101) };

        await Assert.ThrowsAsync<RateLimitedException>(
            () => Browser(halfway).FetchMembersAsync("BK0477"));
        await Assert.ThrowsAsync<RateLimitedException>(
            () => Http(halfway).FetchMembersAsync("BK0477"));
    }

    [Fact]
    public async Task 合法JSON但data为空_两条通道都当限流()
    {
        // push2 限流除了断连，还会返回这个——不能当成"这个板块是空的"
        const string nullData = """{"rc":0,"data":null}""";

        await Assert.ThrowsAsync<RateLimitedException>(
            () => Browser(nullData).FetchBoardListAsync(BoardType.Concept));
        await Assert.ThrowsAsync<RateLimitedException>(
            () => Http(nullData).FetchBoardListAsync(BoardType.Concept));
    }

    [Fact]
    public async Task 空响应_http通道要当限流而不是当空名单()
    {
        await Assert.ThrowsAsync<RateLimitedException>(
            () => Http("").FetchMembersAsync("BK0477"));
    }

    [Fact]
    public async Task http通道不需要预热()
    {
        // 没有浏览器要建，PrepareAsync 直接返回 false（＝"这轮没走浏览器"），不该抛
        Assert.False(await Http(MemberPage(1, 1, 1)).PrepareAsync());
    }

    // ─────────────── 域名（2026-09-05 从 push2 换到 pushguest）───────────────

    [Fact]
    public async Task 成分股_默认打pushguest_并且带着翻页那三条规矩()
    {
        var handler = new ScriptedHandler(MemberPage(150, 100, 1), MemberPage(150, 50, 101));
        await new EastMoneyBoardHttpFetcher(NoWait(), new HttpClient(handler),
                                            boardSwitchPause: TimeSpan.Zero)
            .FetchMembersAsync("BK0477");

        Assert.Equal(2, handler.Urls.Count);
        foreach (var url in handler.Urls)
        {
            Assert.StartsWith("https://pushguest.eastmoney.com/api/qt/clist/get", url);
            Assert.Contains("fid=f12", url);      // 按代码排序，不能按涨跌幅——见基类注释
            Assert.Contains("pz=100", url);       // 网页端锁死 20，我们不受这个限制
            Assert.Contains("fs=b:BK0477", url);
        }
        Assert.Contains("pn=1", handler.Urls[0]);
        Assert.Contains("pn=2", handler.Urls[1]);
    }

    [Fact]
    public async Task 成分股_域名可以一行配置退回push2()
    {
        var handler = new ScriptedHandler(MemberPage(1, 1, 1));
        await new EastMoneyBoardHttpFetcher(NoWait(), new HttpClient(handler),
                                            memberHost: "push2.eastmoney.com",
                                            boardSwitchPause: TimeSpan.Zero)
            .FetchMembersAsync("BK0477");

        Assert.StartsWith("https://push2.eastmoney.com/api/qt/clist/get", handler.Urls[0]);
    }

    [Fact]
    public async Task 板块列表_是回退路径_故意还留在push2上()
    {
        // pushguest 支不支持 fs=m:90+t:2 没验证过，没验证的东西不该悄悄换上去。
        // 这条测试是提醒：哪天要一起换，得先当场验一次，而不是顺手改。
        var handler = new ScriptedHandler(ListPage(1, 1, 1));
        await new EastMoneyBoardHttpFetcher(NoWait(), new HttpClient(handler),
                                            boardSwitchPause: TimeSpan.Zero)
            .FetchBoardListAsync(BoardType.Concept);

        Assert.StartsWith("https://push2.eastmoney.com/api/qt/clist/get", handler.Urls[0]);
    }

    // ─────────────── 节奏：换板块要比翻页慢一档 ───────────────

    [Fact]
    public async Task 换板块要多歇一会_但第一个板块不用等()
    {
        // 用 400 毫秒当基准（实际是 0.5~1.5 倍随机，即 200~600 毫秒），测试才跑得完
        var fetcher = new EastMoneyBoardHttpFetcher(
            NoWait(), new HttpClient(new ScriptedHandler(MemberPage(1, 1, 1))),
            boardSwitchPause: TimeSpan.FromMilliseconds(400));

        var t0 = System.Diagnostics.Stopwatch.StartNew();
        await fetcher.FetchMembersAsync("BK0001");
        var first = t0.Elapsed;

        var t1 = System.Diagnostics.Stopwatch.StartNew();
        await fetcher.FetchMembersAsync("BK0002");
        var second = t1.Elapsed;

        // 第一个板块前面歇是白歇——那会儿还没发过请求，只是让人干等
        Assert.True(first < TimeSpan.FromMilliseconds(150), $"第一个板块不该等，实际等了 {first}");
        // 第二个要等到随机区间的下限以上
        Assert.True(second >= TimeSpan.FromMilliseconds(180), $"换板块该歇一会儿，实际只有 {second}");
    }

    [Fact]
    public async Task 换板块的停顿不影响抓回来的名单()
    {
        var pages = new[] { MemberPage(150, 100, 1), MemberPage(150, 50, 101) };
        var withPause = new EastMoneyBoardHttpFetcher(
            NoWait(), new HttpClient(new ScriptedHandler(pages)),
            boardSwitchPause: TimeSpan.FromMilliseconds(1));

        Assert.Equal(await Http(pages).FetchMembersAsync("BK0477"),
                     await withPause.FetchMembersAsync("BK0477"));
    }

    [Fact]
    public void 两条通道的日志能区分()
    {
        // 出问题时人得能从日志一眼看出这轮走的是哪条路
        Assert.Contains("HttpClient", Http(MemberPage(1, 1, 1)).DescribeChannel());
        Assert.Contains("HTTP", Browser(MemberPage(1, 1, 1)).DescribeChannel());
        // 域名也要报出来：换过一次之后，日志不写清楚就没法从事后的日志判断当时打的是哪个
        Assert.Contains("pushguest", Http(MemberPage(1, 1, 1)).DescribeChannel());
        Assert.Contains("pushguest", Browser(MemberPage(1, 1, 1)).DescribeChannel());
    }
}
