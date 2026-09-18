using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 静默看门狗：判据是「多久没有进展」，不是「跑了多久」（2026-09-08 换的，缘由见类注释）。
///
/// 这几条钉的是换判据之后最要紧的四件事：长任务不被误杀、真哑掉能被抓到、
/// 用户按停止不能被认成卡死、跑得久只提醒不动手。
/// </summary>
public class QuietWatchdogTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(30);

    /// <summary>一直在报进度的任务，跑到多久都不该被掐——这就是这次改造的全部意义。</summary>
    [Fact]
    public async Task 一直有心跳就不掐()
    {
        using var dog = new QuietWatchdog(Quiet, CancellationToken.None, checkInterval: Tick);

        // 跑满 5 倍阈值，期间每 50ms 一次心跳
        for (int i = 0; i < 30; i++)
        {
            dog.Beat($"处理中 {i}");
            await Task.Delay(50);
        }

        Assert.False(dog.Starved);
        Assert.False(dog.Token.IsCancellationRequested);
    }

    /// <summary>彻底不出声超过阈值＝判定卡死，取消令牌并立旗。</summary>
    [Fact]
    public async Task 哑太久就掐并立旗()
    {
        using var dog = new QuietWatchdog(Quiet, CancellationToken.None, checkInterval: Tick);
        dog.Beat("扫到第 360 个板块");

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.True(dog.Starved);
        Assert.True(dog.Token.IsCancellationRequested);
        // 最后一句要留住——失败原因里就靠它定位卡在哪一步
        Assert.Equal("扫到第 360 个板块", dog.LastMessage);
    }

    /// <summary>
    /// 外层取消（用户按了停止）也会让 Token 取消，但 <see cref="QuietWatchdog.Starved"/> 必须是 false。
    /// PlanRunner 的两个 catch 就靠这个旗子分辨"被停止"和"卡死了"——认反了会把手动停止
    /// 记成失败、然后继续跑下一项。
    /// </summary>
    [Fact]
    public void 外层取消不算卡死()
    {
        using var outer = new CancellationTokenSource();
        using var dog = new QuietWatchdog(TimeSpan.FromMinutes(10), outer.Token, checkInterval: Tick);

        outer.Cancel();

        Assert.True(dog.Token.IsCancellationRequested);
        Assert.False(dog.Starved);
    }

    /// <summary>跑得久只提醒、绝不动手——整段回补本来就要跑几小时，那不是错。</summary>
    [Fact]
    public async Task 跑得久只提醒不掐()
    {
        int 提醒次数 = 0;
        using var dog = new QuietWatchdog(
            TimeSpan.FromMinutes(10), CancellationToken.None,
            onLongRun: _ => Interlocked.Increment(ref 提醒次数),
            checkInterval: Tick, longRunNotice: TimeSpan.FromMilliseconds(100));

        // 一直有心跳，跑过"长跑线"好几倍
        for (int i = 0; i < 20; i++)
        {
            dog.Beat($"处理中 {i}");
            await Task.Delay(30);
        }

        Assert.Equal(1, 提醒次数);                    // 只说一次，别刷屏
        Assert.False(dog.Starved);                    // 提醒归提醒，不掐
        Assert.False(dog.Token.IsCancellationRequested);
    }

    /// <summary><see cref="QuietWatchdog.Wrap"/> 包出来的 progress 要同时记心跳和转发日志。</summary>
    [Fact]
    public async Task Wrap出来的进度既记心跳也转发()
    {
        var 收到的 = new List<string>();
        using var dog = new QuietWatchdog(Quiet, CancellationToken.None, checkInterval: Tick);

        var progress = dog.Wrap(s => { lock (收到的) 收到的.Add(s); });
        progress.Report("处理中 1/10");

        // 心跳是同步记的，日志走 Progress<T> 异步派发——所以后者要等一下
        Assert.Equal("处理中 1/10", dog.LastMessage);
        await Task.Delay(200);
        lock (收到的) Assert.Contains("处理中 1/10", 收到的);
    }

    // ── 看门狗自己不能把进程掀了 ──────────────────────────────────

    /// <summary>
    /// <see cref="QuietWatchdog"/> 的 Check() 是 <c>Timer</c> 回调、跑在**线程池线程**上，
    /// 那里的未捕获异常不是"测试失败"，是**整个进程直接没了**。
    ///
    /// 这一条走**掐断**那条路：<c>Cancel()</c> 会同步执行注册在令牌上的所有回调，
    /// 任一抛出都会被包成 <c>AggregateException</c> 扔回来——修之前那里只 catch
    /// <c>ObjectDisposedException</c>，于是异常从 Timer 回调逃出去、把宿主一起带走。
    ///
    /// ⚠ 这条测试**红起来的样子是 testhost 崩掉**（Test Run Aborted），不是断言失败。
    /// </summary>
    [Fact]
    public async Task 取消回调抛异常_不掀进程也不影响立旗()
    {
        using var dog = new QuietWatchdog(Quiet, CancellationToken.None, checkInterval: Tick);
        using var reg = dog.Token.Register(() => throw new InvalidOperationException("注册方故意抛"));

        await Task.Delay(TimeSpan.FromMilliseconds(900));   // 一直不 Beat，等它判定卡死

        Assert.True(dog.Starved);                            // 旗子照立
        Assert.True(dog.Token.IsCancellationRequested);      // 掐断照常生效
    }

    /// <summary>
    /// 同上，走**长跑提醒**那条路：提醒回调是外部代码（PlanRunner 传的是写日志的委托），
    /// 它抛出来既不该掀进程、也不该耽误后面的掐断判定。
    /// </summary>
    [Fact]
    public async Task 长跑提醒回调抛异常_不掀进程也不影响掐断()
    {
        using var dog = new QuietWatchdog(
            Quiet, CancellationToken.None,
            onLongRun: _ => throw new InvalidOperationException("提醒回调故意抛"),
            checkInterval: Tick,
            longRunNotice: TimeSpan.FromMilliseconds(50));

        await Task.Delay(TimeSpan.FromMilliseconds(900));

        Assert.True(dog.Starved);
    }
}
