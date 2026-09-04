using System.Diagnostics;
using StockPlatform.Data.Remote;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 请求间隔的随机抖动（2026-09-04）。
///
/// 为什么要它：整条 push2 通道都在模仿浏览器（走 WebView2、真实 TLS 指纹、真实 Cookie），
/// 而**固定间隔是机器行为里最好认的特征**——人不会精确每 4.000 秒点一次。
/// 代价只是单次有快有慢，平均耗时不变。
/// </summary>
public class RateLimiterJitterTests
{
    private readonly ITestOutputHelper _out;
    public RateLimiterJitterTests(ITestOutputHelper o) => _out = o;

    /// <summary>连发若干次，量出每两次之间的实际间隔。</summary>
    private static async Task<List<double>> MeasureGapsAsync(RateLimiter limiter, int n)
    {
        var gaps = new List<double>();
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        for (int i = 0; i < n; i++)
        {
            await limiter.RunAsync(() => Task.FromResult(1));
            var now = sw.Elapsed;
            gaps.Add((now - last).TotalMilliseconds);
            last = now;
        }
        return gaps;
    }

    [Fact]
    public async Task 不配抖动时间隔是固定的()
    {
        // 默认行为不能变：没显式要抖动的地方（datacenter、新浪那些）还是老样子
        var limiter = new RateLimiter(maxConcurrency: 1,
            delayBetweenRequests: TimeSpan.FromMilliseconds(120), batchSize: 1000);
        var gaps = await MeasureGapsAsync(limiter, 6);

        _out.WriteLine(string.Join(", ", gaps.Select(g => $"{g:0}ms")));
        // 定时器本身有几毫秒误差，给 60ms 的宽容；关键是不该出现 ±30% 那种量级的散布
        Assert.All(gaps, g => Assert.InRange(g, 120 - 60, 120 + 60));
    }

    [Fact]
    public async Task 配了抖动之后间隔不再整齐划一()
    {
        var limiter = new RateLimiter(maxConcurrency: 1,
            delayBetweenRequests: TimeSpan.FromMilliseconds(200), batchSize: 1000, jitter: 0.3);
        var gaps = await MeasureGapsAsync(limiter, 12);

        _out.WriteLine(string.Join(", ", gaps.Select(g => $"{g:0}ms")));
        // 12 次里至少要出现几个不同的值——固定间隔的话它们会挤在一起
        var spread = gaps.Max() - gaps.Min();
        Assert.True(spread > 30, $"间隔几乎没有散布（极差 {spread:0}ms），抖动没生效？");
    }

    [Fact]
    public async Task 抖动幅度限定在设定的比例内()
    {
        // 抖过头就不是"像人"而是"忽快忽慢"了：太快会撞限流，太慢白白拖长总耗时
        var limiter = new RateLimiter(maxConcurrency: 1,
            delayBetweenRequests: TimeSpan.FromMilliseconds(200), batchSize: 1000, jitter: 0.3);
        var gaps = await MeasureGapsAsync(limiter, 12);

        // 200ms ±30% = 140~260ms，再给 60ms 的定时器误差
        Assert.All(gaps, g => Assert.InRange(g, 140 - 60, 260 + 60));
        _out.WriteLine($"平均 {gaps.Average():0}ms（设定 200ms）");
    }

    [Theory]
    [InlineData(-1.0)]   // 负数
    [InlineData(5.0)]    // 大于 1
    public async Task 抖动比例越界时不会把间隔搞成负数或离谱值(double jitter)
    {
        var limiter = new RateLimiter(maxConcurrency: 1,
            delayBetweenRequests: TimeSpan.FromMilliseconds(100), batchSize: 1000, jitter: jitter);
        var gaps = await MeasureGapsAsync(limiter, 5);

        Assert.All(gaps, g => Assert.True(g >= 0, $"出现了负间隔 {g}ms"));
        Assert.All(gaps, g => Assert.True(g < 1000, $"间隔离谱地长：{g}ms"));
    }
}
