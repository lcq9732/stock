using StockPlatform.Data.Remote;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 限流熔断的指数退避（2026-09-04）。
///
/// 依据是那天的实测日志：东财 push2/push2his 连发 16~35 个请求就被切断
/// （库里 fetched_at 反推出三串成功：25只/61秒、16只/31秒、33只/97秒），
/// 而原来固定 5~15 分钟的暂停明显不够——按 24 分钟一轮连撞 7 次全空，
/// 第一次被切 26 分钟就恢复，连撞之后花了 3 小时 14 分才恢复。
///
/// 所以这里守两件事：**连续熔断必须逐次拉长**，以及**一次侥幸成功不能把退避打回起点**
/// （被切之后能再抓十几个是常态，一成功就归零等于没有递增）。
/// </summary>
public class RateLimiterBackoffTests
{
    private readonly ITestOutputHelper _out;
    public RateLimiterBackoffTests(ITestOutputHelper o) => _out = o;

    /// <summary>
    /// 退避真值是 15 分钟起步，测试当然不能真等——用毫秒级的 baseBackoff 和重试延迟跑同一套逻辑。
    /// 关心的是「翻倍」这个关系，不是具体分钟数。
    /// </summary>
    private static RateLimiter NewFast() => new(
        maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
        baseBackoff: TimeSpan.FromMilliseconds(200), maxBackoff: TimeSpan.FromSeconds(5),
        retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);

    /// <summary>
    /// 把限流器逼到熔断一次，返回它这次 announce 的暂停秒数。
    /// 要连撞 15 次——单次失败不再熔断了（2026-09-04），被拒是常态不是异常。
    /// </summary>
    private static async Task<double> TripOnceAsync(RateLimiter limiter, List<string> log)
    {
        for (int i = 0; i < 15; i++)
        {
            try
            {
                await limiter.RunAsync<int>(() => throw new RateLimitedException("模拟被限流"));
            }
            catch (RateLimitedException) { }
        }
        var line = log.LastOrDefault(l => l.Contains("主动暂停约"));
        Assert.NotNull(line);
        var until = limiter.PausedUntil;
        Assert.NotNull(until);
        return (until!.Value - DateTime.Now).TotalSeconds;
    }

    [Fact]
    public async Task 连续熔断的等待时间逐次翻倍()
    {
        var log = new List<string>();
        var limiter = NewFast();
        limiter.OnStatus += log.Add;

        var first = await TripOnceAsync(limiter, log);
        // 第一次的暂停要先过去，否则第二次调用会卡在等待里
        await Task.Delay(TimeSpan.FromSeconds(first + 0.3));
        var second = await TripOnceAsync(limiter, log);
        _out.WriteLine($"第 1 次 {first:F2} 秒 → 第 2 次 {second:F2} 秒");

        // 起步值 0.2 秒、第二次 0.4 秒，各带 ±20% 抖动（抖动是为了并发熔断时别一起醒来）
        Assert.InRange(first, 0.10, 0.30);
        Assert.InRange(second, 0.25, 0.55);
        Assert.True(second > first, $"第二次({second:F2}s)应该比第一次({first:F2}s)等得久");
    }

    [Fact]
    public async Task 熔断日志会点明这是第几次连续熔断()
    {
        var log = new List<string>();
        var limiter = NewFast();
        limiter.OnStatus += log.Add;

        var first = await TripOnceAsync(limiter, log);
        await Task.Delay(TimeSpan.FromSeconds(first + 0.3));
        await TripOnceAsync(limiter, log);

        // 人看日志得能判断"是不是我撞得太勤"，光报分钟数看不出趋势
        var last = log.Last(l => l.Contains("主动暂停约"));
        Assert.Contains("连续第 2 次", last);
        Assert.Contains("翻倍", last);
        _out.WriteLine(last);
    }

    [Fact]
    public async Task 暂停期间能查到还要等到几点()
    {
        var log = new List<string>();
        var limiter = new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
            baseBackoff: TimeSpan.FromSeconds(30), retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);
        limiter.OnStatus += log.Add;

        Assert.Null(limiter.PausedUntil);          // 没熔断时不该报暂停

        await TripOnceAsync(limiter, log);

        var until = limiter.PausedUntil;
        Assert.NotNull(until);
        // 暂停期里再点【执行】要能直接回绝，而不是干等到超时（实测干等过 8 分钟）
        Assert.True(until! > DateTime.Now.AddSeconds(10), $"应该还要等一阵：{until}");
    }

    [Fact]
    public void 没跑过任何请求时不处于暂停()
        => Assert.Null(new RateLimiter(maxConcurrency: 1).PausedUntil);

    [Fact]
    public async Task 单次失败不熔断()
    {
        // 实测两轮 60 个请求：push2 成功 42%、push2his 成功 28%，成败都呈周期交替
        // （连续失败最多 7 个、6 个），60 个打完始终没进长封禁——被拒是令牌桶空了的
        // 正常表现，不是异常。原来一次失败就睡 15 分钟，桶明明 20 秒就补上了，
        // 实际吞吐被砍到接近零。
        var log = new List<string>();
        var limiter = NewFast();
        limiter.OnStatus += log.Add;

        for (int i = 0; i < 14; i++)         // 14 次，差一个到阈值
        {
            try { await limiter.RunAsync<int>(() => throw new RateLimitedException("桶空了")); }
            catch (RateLimitedException) { }
        }

        Assert.Null(limiter.PausedUntil);
        Assert.DoesNotContain(log, l => l.Contains("主动暂停"));
    }

    [Fact]
    public async Task 中间成功一次就重新数()
    {
        // 成败交替是常态，不能把"零零散散的失败"累加成熔断——
        // 那样跑上几分钟总会凑够 10 个，等于又回到动不动就睡 15 分钟。
        var log = new List<string>();
        var limiter = NewFast();
        limiter.OnStatus += log.Add;

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 14; i++)
            {
                try { await limiter.RunAsync<int>(() => throw new RateLimitedException("桶空了")); }
                catch (RateLimitedException) { }
            }
            await limiter.RunAsync(() => Task.FromResult(1));   // 成功一次，连续中断
        }

        Assert.Null(limiter.PausedUntil);
    }

    [Fact]
    public async Task 连续十五次失败才熔断()
    {
        var log = new List<string>();
        var limiter = NewFast();
        limiter.OnStatus += log.Add;

        for (int i = 0; i < 15; i++)
        {
            try { await limiter.RunAsync<int>(() => throw new RateLimitedException("真被封了")); }
            catch (RateLimitedException) { }
        }

        Assert.NotNull(limiter.PausedUntil);
        Assert.Contains(log, l => l.Contains("连续 15 个请求被拒"));
    }
}
