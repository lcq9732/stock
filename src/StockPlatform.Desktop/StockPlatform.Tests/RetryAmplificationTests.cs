using StockPlatform.Data.Remote;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 重试放大（2026-09-04 实测踩到的坑）。
///
/// 浏览器通道内部为了等验证脚本跑完，自己会重试 3 次（5 秒、15 秒）。
/// 如果外层 <see cref="RateLimiter"/> 也重试 3 次，一个逻辑请求就变成 3×3＝9 个实际请求——
/// 被限流的时候等于火上浇油，而且日志里看着像"试了很多次"，其实全是自己打自己。
///
/// 实测那一轮的日志长这样，同一页出现了三组"第 1 次/第 2 次"：
///   [12:39:48] 第 1 次没拿到数据，等 5 秒再试
///   [12:40:10] 第 1 次没拿到数据，等 5 秒再试   ← 外层重试带来的第二组
///   [12:40:41] 第 1 次没拿到数据，等 5 秒再试   ← 第三组
///
/// 所以凡是**调用方自己已经重试过**的通道，限流器就不该再重试一遍。
/// </summary>
public class RetryAmplificationTests
{
    private readonly ITestOutputHelper _out;
    public RetryAmplificationTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task 配了空重试列表时一次失败只发一个请求()
    {
        int calls = 0;
        var limiter = new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
                                      retryDelays: []);

        await Assert.ThrowsAsync<RateLimitedException>(() =>
            limiter.RunAsync<int>(() => { calls++; throw new RateLimitedException("被拒"); }));

        Assert.Equal(1, calls);       // ← 不是 3
        _out.WriteLine($"实际发出 {calls} 个请求");
    }

    [Fact]
    public async Task 默认仍然重试两次_别的数据源不受影响()
    {
        // 这个改动只针对浏览器通道那一条线，datacenter、新浪那些还靠限流器的重试兜底
        int calls = 0;
        var limiter = new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
                                      retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

        await Assert.ThrowsAsync<RateLimitedException>(() =>
            limiter.RunAsync<int>(() => { calls++; throw new RateLimitedException("被拒"); }));

        Assert.Equal(3, calls);       // 首次 + 两次重试
    }

    [Fact]
    public async Task 空重试列表下成功的请求照常返回()
    {
        var limiter = new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
                                      retryDelays: []);
        Assert.Equal(42, await limiter.RunAsync(() => Task.FromResult(42)));
    }
}
