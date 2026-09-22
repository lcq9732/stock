using System.Net;
using System.Net.Http;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【分档资金流快照】的**浏览器通道**（2026-09-21 接上）。
///
/// 为什么值得专门钉：东财给浏览器和给程序的待遇差一个数量级（见 <see cref="IBrowserJsonFetcher"/>
/// 上那张实测表）。09-21 晚上 HttpClient 这条已经一个请求都过不去——19:30 打到第 16 页被切、
/// 20:22 第 1 页就被切——而同一时刻在东财页面里用 JSONP 照样连抓 7 页；一条全新的 4G 出口
/// 跑网页版反而一次都没成，所以不是出口配额，是请求形态。
///
/// 于是这条通道成了这一项**唯一抓得到数据的路**。它要是悄悄退回 HttpClient（比如接线漏了、
/// 或者 IsReady 判断写反），表现出来只是"今天又没抓到"，跟被限流一模一样，查不出区别——
/// 所以拿测试钉住"有浏览器就必须走浏览器"。
/// </summary>
public class MoneyFlowSnapshotBrowserChannelTests
{
    // 000006 深振业A，2026-09-04 收盘后那一版（跟 MoneyFlowSnapshotTests 用的是同一条真实报文）。
    private const string OnePage = """
        {"rc":0,"data":{"total":1,"diff":[
          {"f2":7.01,"f3":0.43,"f12":"000006","f62":-878110.0,"f66":-2844000.0,"f69":-2.16,
           "f72":1965890.0,"f75":1.49,"f78":-5148156.0,"f81":-3.91,"f84":6026266.0,"f87":4.58,
           "f124":1788507240,"f184":-0.67}]}}
        """;

    /// <summary>假浏览器通道：记下被问过哪些 URL，照单返回预置报文。</summary>
    private sealed class FakeBrowser : IBrowserJsonFetcher
    {
        private readonly string _body;
        private readonly bool _ready;
        public FakeBrowser(string body, bool ready = true) { _body = body; _ready = ready; }

        public List<string> Asked { get; } = [];
        public int PrepareCalls { get; private set; }
        public int ShowCalls { get; private set; }
        public bool IsReady { get; private set; }

        public Task<bool> EnsureReadyAsync(CancellationToken ct = default)
        {
            PrepareCalls++;
            IsReady = _ready;
            return Task.FromResult(_ready);
        }
        public Task<string> GetJsonAsync(string url, CancellationToken ct = default)
        {
            Asked.Add(url);
            return Task.FromResult(_body);
        }
        public Task ShowWorkWindowAsync(CancellationToken ct = default) { ShowCalls++; return Task.CompletedTask; }
        public Task HideWorkWindowAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>HttpClient 这条一旦被碰就炸——用来证明"真的没走它"，而不是靠猜。</summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            throw new HttpRequestException("不该走 HttpClient——这一项现在只有浏览器通道抓得到。");
        }
    }

    private static RateLimiter NoWait() =>
        new(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero, batchSize: 10_000);

    [Fact]
    public async Task 浏览器就绪时整轮都走浏览器_一个请求都不落到HttpClient()
    {
        var browser = new FakeBrowser(OnePage);
        var handler = new ExplodingHandler();
        var provider = new EastMoneyMoneyFlowSnapshotProvider(
            NoWait(), new HttpClient(handler), browser: browser);

        Assert.True(await provider.PrepareAsync());

        var snap = await provider.FetchAllAsync();

        Assert.Equal(0, handler.Calls);                       // HttpClient 一次都没被碰
        Assert.NotEmpty(browser.Asked);
        Assert.Equal(new DateTime(2026, 9, 4), snap.TradeDate);
        var row = Assert.Single(snap.Rows);
        Assert.Equal("000006", row.Code);
        Assert.Equal(-878110.0, row.MainNet);
    }

    [Fact]
    public async Task 打的URL跟HttpClient那条一模一样()
    {
        var browser = new FakeBrowser(OnePage);
        var provider = new EastMoneyMoneyFlowSnapshotProvider(
            NoWait(), new HttpClient(new ExplodingHandler()), browser: browser);
        await provider.PrepareAsync();

        await provider.FetchAllAsync();

        var url = browser.Asked[0];
        // 换通道只换"谁去发"，不换"发什么"——市场过滤、字段、排序键、每页条数都得原样。
        Assert.StartsWith($"https://{EastMoneyMoneyFlowSnapshotProvider.DefaultHost}/api/qt/clist/get?", url);
        Assert.Contains("fid=f12", url);        // 排序键必须是代码：涨跌幅盘中在变，翻页会重复+遗漏
        Assert.Contains("pz=100", url);         // 服务端硬上限
        Assert.Contains("fs=m:0+t:6,m:0+t:80,m:1+t:2,m:1+t:23,m:0+t:81+s:2048", url);   // 沪深京全 A
        Assert.Contains("f62", url);
        Assert.Contains("f124", url);           // 行情时间戳，交易日和"收盘了没"都从它推
    }

    [Fact]
    public async Task 浏览器起不来就退回HttpClient_并且说清楚走的是哪条()
    {
        var browser = new FakeBrowser(OnePage, ready: false);
        var handler = new ExplodingHandler();
        var provider = new EastMoneyMoneyFlowSnapshotProvider(
            NoWait(), new HttpClient(handler), browser: browser);

        Assert.False(await provider.PrepareAsync());
        Assert.Contains("HTTP 直连", provider.DescribeChannel());
        Assert.Contains("浏览器通道没就绪", provider.DescribeChannel());

        // 退回之后照常打 HttpClient——这里它会炸。炸了不再整轮抛异常（2026-09-21 改），
        // 而是把页记进缺页清单留给下一轮；但**绝不能报成"这天抓完了"**。
        var snap = await provider.FetchAllAsync();
        Assert.True(handler.Calls > 0);
        Assert.False(snap.Complete);
        Assert.Empty(snap.Rows);
        Assert.Contains(1, snap.MissingPages);
    }

    [Fact]
    public async Task 没配浏览器就跟以前完全一样()
    {
        var provider = new EastMoneyMoneyFlowSnapshotProvider(
            NoWait(), new HttpClient(new ExplodingHandler()));

        Assert.False(await provider.PrepareAsync());          // 没配＝没有浏览器可准备
        Assert.Equal($"HTTP 直连 {EastMoneyMoneyFlowSnapshotProvider.DefaultHost}",
                     provider.DescribeChannel());
    }

    [Fact]
    public async Task 浏览器返回空要当成限流_不能当成翻完了()
    {
        // 空响应是限流最典型的表现。当成"翻完了"会让这一轮只写半个市场的数据，
        // 而缺的那些票事后完全看不出来——这一项漏一天就永久补不回来。
        var browser = new FakeBrowser("");
        var provider = new EastMoneyMoneyFlowSnapshotProvider(
            NoWait(), new HttpClient(new ExplodingHandler()), browser: browser);
        await provider.PrepareAsync();

        var snap = await provider.FetchAllAsync();

        // 不再整轮抛异常（2026-09-21），但"不能当成翻完了"这条没松：
        // 一行都没拿到、第 1 页记进缺页、Complete 为假。
        Assert.False(snap.Complete);
        Assert.Empty(snap.Rows);
        Assert.Contains(1, snap.MissingPages);
    }
}
