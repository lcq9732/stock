using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 浏览器通道的**回退**行为（2026-09-04）。
///
/// 通道本身的价值是实测出来的：同一 IP、同一接口、相近时间，浏览器 JSONP 18/20 成功
/// （27 个/分钟），而 HttpClient 第 7 个请求就被切（4.8 个/分钟）。差别在 TLS 指纹。
///
/// 但这里守的是另一头：**它用不了的时候不能把整项抓取拖垮**。
/// 没装 WebView2 运行时、被组策略拦、东财页面打不开——这些在别人机器上都可能发生，
/// 一律要安静地退回 HttpClient，慢点没关系，不能变成"一个板块都抓不了"。
/// </summary>
public class BrowserChannelFallbackTests
{
    private sealed class FakeBrowser : IBrowserJsonFetcher
    {
        private readonly bool _ready;
        private readonly Exception? _throwOnReady;

        public FakeBrowser(bool ready, Exception? throwOnReady = null)
        {
            _ready = ready;
            _throwOnReady = throwOnReady;
        }

        public bool IsReady { get; private set; }

        public Task<bool> EnsureReadyAsync(CancellationToken ct = default)
        {
            if (_throwOnReady != null) throw _throwOnReady;
            IsReady = _ready;
            return Task.FromResult(_ready);
        }

        public Task<string> GetJsonAsync(string url, CancellationToken ct = default)
            => Task.FromResult("{}");

        public Task ShowWorkWindowAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HideWorkWindowAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static EastMoneyBoardFetcher New(IBrowserJsonFetcher? browser)
        => new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero),
               browser: browser);

    [Fact]
    public async Task 没配浏览器通道时安静地走普通抓取()
    {
        var f = New(null);
        Assert.False(await f.PrepareAsync());
        Assert.Contains("普通 HTTP", f.DescribeChannel());
    }

    [Fact]
    public async Task 通道就绪时日志说明走的是浏览器()
    {
        // 这条得能在日志里看出来——否则"今天怎么快了/慢了"没法归因
        var f = New(new FakeBrowser(ready: true));
        Assert.True(await f.PrepareAsync());
        Assert.Contains("浏览器通道", f.DescribeChannel());
    }

    [Fact]
    public async Task 通道初始化返回失败时退回普通抓取()
    {
        var f = New(new FakeBrowser(ready: false));
        Assert.False(await f.PrepareAsync());
        Assert.Contains("普通 HTTP", f.DescribeChannel());
    }

    [Fact]
    public async Task 通道初始化抛异常也不能把抓取带崩()
    {
        // 没装 WebView2 运行时就是这条路径——别人机器上很可能发生
        var msgs = new List<string>();
        var f = New(new FakeBrowser(ready: true, throwOnReady: new InvalidOperationException("没装运行时")));
        f.OnStatus += msgs.Add;

        Assert.False(await f.PrepareAsync());     // 返回 false，不是抛出去
        Assert.Contains("普通 HTTP", f.DescribeChannel());
        // 而且要说一声，不能静默降级——否则没人知道为什么变慢了
        Assert.Contains(msgs, m => m.Contains("浏览器通道") && m.Contains("退回"));
    }

    [Fact]
    public async Task 用户取消时如实抛出而不是当成通道故障()
    {
        // 点了停止就该立刻停，不该被"退回普通抓取"吞掉、继续跑下去
        var f = New(new FakeBrowser(ready: true, throwOnReady: new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.PrepareAsync());
    }
}
