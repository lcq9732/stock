using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 数据源占用表（2026-09-04）——"手动执行能不能跟计划并发"全靠它。
///
/// 缘起：原来靠一个全局 IsBusy，"有任务在跑就一律不许再开"。可【板块成分股】走东财 push2、
/// 要人守着过图片验证码，【个股日K】走腾讯要跑一个半小时——两个压根不抢同一个源却只能排队，
/// 板块几天都追不上时效性（1000 个板块跑一天才拿下 207 个）。
/// </summary>
public class SourceOccupancyTests
{
    private static CancellationTokenSource Cts() => new();

    private static IReadOnlySet<DataSourceId> S(params DataSourceId[] ids) => ids.ToHashSet();

    [Fact]
    public void 同一个源_第二个拿不到并说出被谁占着()
    {
        var occ = new SourceOccupancy();
        var first = occ.TryAcquire("概念和行业板块", S(DataSourceId.EmPush2), false, Cts(), out _, out _);
        Assert.NotNull(first);

        var second = occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), true, Cts(),
                                    out var blockedBy, out var blockedSource);
        Assert.Null(second);
        Assert.Equal("概念和行业板块", blockedBy!.Name);      // 界面要拿它写"被谁占着"
        Assert.Equal(DataSourceId.EmPush2, blockedSource);
    }

    /// <summary>这条就是整件事的目的：源不重叠就能同时跑。</summary>
    [Fact]
    public void 不同源_可以同时拿到()
    {
        var occ = new SourceOccupancy();
        var kline = occ.TryAcquire("个股日K·前复权", S(DataSourceId.Tencent, DataSourceId.Sina), false, Cts(), out _, out _);
        var board = occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), true, Cts(), out _, out _);

        Assert.NotNull(kline);
        Assert.NotNull(board);
        Assert.Equal(2, occ.Snapshot().Count);
    }

    /// <summary>东财三个域名各算一个源——合成一个"东财"会把能并行的组合也挡掉。</summary>
    [Fact]
    public void 东财三个域名互不冲突()
    {
        var occ = new SourceOccupancy();
        Assert.NotNull(occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), false, Cts(), out _, out _));
        Assert.NotNull(occ.TryAcquire("分档资金流", S(DataSourceId.EmPush2His), false, Cts(), out _, out _));
        Assert.NotNull(occ.TryAcquire("市场事件", S(DataSourceId.EmDataCenter), false, Cts(), out _, out _));
    }

    /// <summary>多源任务只要沾上一个就冲突。</summary>
    [Fact]
    public void 多源任务_沾上一个就冲突()
    {
        var occ = new SourceOccupancy();
        occ.TryAcquire("融资余额", S(DataSourceId.Exchange), false, Cts(), out _, out _);

        // 退市股收尾要 交易所 + 腾讯，交易所已被占 → 拿不到
        var t = occ.TryAcquire("退市股收尾", S(DataSourceId.Exchange, DataSourceId.Tencent), false, Cts(),
                               out var by, out var src);
        Assert.Null(t);
        Assert.Equal("融资余额", by!.Name);
        Assert.Equal(DataSourceId.Exchange, src);
    }

    /// <summary>不占源的（本地计算）永远拿得到——重算回测序列、板块指数合成这些。</summary>
    [Fact]
    public void 本地任务_永不冲突()
    {
        var occ = new SourceOccupancy();
        occ.TryAcquire("个股日K", S(DataSourceId.Tencent, DataSourceId.Sina), false, Cts(), out _, out _);
        occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), false, Cts(), out _, out _);

        Assert.NotNull(occ.TryAcquire("重算回测序列", S(), false, Cts(), out _, out _));
        Assert.NotNull(occ.TryAcquire("板块指数合成", S(), false, Cts(), out _, out _));
    }

    [Fact]
    public void 释放之后同源可以再拿()
    {
        var occ = new SourceOccupancy();
        var first = occ.TryAcquire("概念和行业板块", S(DataSourceId.EmPush2), false, Cts(), out _, out _);
        Assert.Null(occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), true, Cts(), out _, out _));

        occ.Release(first);
        Assert.False(occ.AnyRunning);
        Assert.NotNull(occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), true, Cts(), out _, out _));
    }

    /// <summary>
    /// 「检查 + 登记」必须是**一个原子动作**：分两步做的话，两个线程会同时通过检查、
    /// 同时占住同一个源，然后一起去撞数据源的限流——那正是这张表要防的事。
    /// </summary>
    [Fact]
    public void 并发抢同一个源_只有一个成功()
    {
        var occ = new SourceOccupancy();
        int won = 0;
        var start = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, 16).Select(i => new Thread(() =>
        {
            start.Wait();
            var got = occ.TryAcquire($"任务{i}", S(DataSourceId.EmPush2), false, Cts(), out _, out _);
            if (got != null) Interlocked.Increment(ref won);
        })).ToList();

        foreach (var t in threads) t.Start();
        start.Set();
        foreach (var t in threads) t.Join();

        Assert.Equal(1, won);
        Assert.Single(occ.Snapshot());
    }

    /// <summary>停止：CancelAll 只发信号；"全部停干净"由占用表清空来表达。</summary>
    [Fact]
    public async Task 停止时先发信号_表清空才算停干净()
    {
        var occ = new SourceOccupancy();
        var cts = Cts();
        var lease = occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), true, cts, out _, out _);

        Assert.Equal(1, occ.CancelAll());
        Assert.True(cts.IsCancellationRequested);       // 信号发到了
        Assert.True(occ.AnyRunning);                    // 但还没收尾，不算停干净

        // 模拟任务收尾完毕（真实代码里这一步在 finally 里）
        occ.Release(lease);
        Assert.True(await occ.WaitAllStoppedAsync(TimeSpan.FromSeconds(2)));
    }

    /// <summary>不响应停止的任务：等到超时要如实返回 false，不能假装停干净了。</summary>
    [Fact]
    public async Task 不响应停止的任务_等到超时返回false()
    {
        var occ = new SourceOccupancy();
        occ.TryAcquire("卡住的任务", S(DataSourceId.EmPush2), false, Cts(), out _, out _);
        occ.CancelAll();

        Assert.False(await occ.WaitAllStoppedAsync(TimeSpan.FromMilliseconds(400)));
        Assert.Single(occ.Snapshot());                  // 还占着，界面要照实说
    }

    [Fact]
    public void 占用变化会通知界面()
    {
        var occ = new SourceOccupancy();
        int changed = 0;
        occ.Changed += () => changed++;

        var lease = occ.TryAcquire("板块成分股", S(DataSourceId.EmPush2), true, Cts(), out _, out _);
        Assert.Equal(1, changed);
        occ.Release(lease);
        Assert.Equal(2, changed);

        // 抢失败不算变化——表没动
        occ.TryAcquire("A", S(DataSourceId.Sina), false, Cts(), out _, out _);
        int before = changed;
        occ.TryAcquire("B", S(DataSourceId.Sina), false, Cts(), out _, out _);
        Assert.Equal(before, changed);
    }
}
