using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 准入裁决：让路 / 抢占 / 抢不动（2026-09-05）。
///
/// ════ 为什么这组测试非有不可 ════
/// 抢占是整个调度里**唯一有权把用户正在跑的任务停掉**的机制。它出错的后果不对称：
///   · 该抢不抢 → 计划被手工任务饿死（2026-09-05 实测停摆 2 小时 41 分）；
///   · 不该抢却抢了 → 用户主动点的任务被程序掐掉，而人根本不知道为什么；
///   · 抢了但对方没停就硬上 → 两个任务同时打同一个数据源、同时写同一张表。
/// 这段逻辑原来长在 MainViewModel 里，一行测试都盖不到（那个类要 17 个依赖的
/// FetchOrchestrator，项目又是 WinExe 单文件、测试引用不了），所以搬进了
/// <see cref="SourceAdmission"/>。
///
/// 下面全部用**假任务**跑：占用表里登记一个名字和一个 CTS 就是"有任务在跑"，
/// 取消它并从表里注销就是"它停下来了"——不碰网络、不碰界面，毫秒级跑完。
/// </summary>
public class SourceAdmissionTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    /// <summary>测试用的准入器：等待上限和轮询间隔都压到毫秒级。</summary>
    private static SourceAdmission NewAdmission(SourceOccupancy occ, List<string>? log = null,
                                                TimeSpan? waitLimit = null, TimeSpan? cooldown = null)
        => new(occ, log == null ? null : s => { lock (log) log.Add(s); },
               waitLimit: waitLimit ?? TimeSpan.FromMilliseconds(300),
               cooldown: cooldown ?? TimeSpan.FromMinutes(5),
               pollInterval: Fast);

    private static IReadOnlySet<DataSourceId> Src(params DataSourceId[] ids) => ids.ToHashSet();

    /// <summary>
    /// 造一个"正在跑的任务"：占住源，返回它的 CTS 和一个"让它收尾"的动作。
    /// 收尾＝取消 + 从占用表注销，跟真实任务在 finally 里做的事一样。
    /// </summary>
    private static (RunningTask Task, Action Finish) StartFake(
        SourceOccupancy occ, string name, params DataSourceId[] sources)
    {
        var cts = new CancellationTokenSource();
        var t = occ.TryAcquire(name, sources.ToHashSet(), manual: true, cts, out _, out _);
        Assert.NotNull(t);
        return (t!, () => occ.Release(t));
    }

    // ─────────────── 没冲突：直接拿 ───────────────

    [Fact]
    public async Task 源没被占_直接拿到()
    {
        var occ = new SourceOccupancy();
        var r = await NewAdmission(occ).AcquireAsync(
            "板块成分股", Src(DataSourceId.EmPush2),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.Acquired, r.Kind);
        Assert.NotNull(r.Lease);
    }

    [Fact]
    public async Task 源不重叠_可以并行不算冲突()
    {
        var occ = new SourceOccupancy();
        StartFake(occ, "分档资金流", DataSourceId.EmPush2His);      // 占的是 push2his

        var r = await NewAdmission(occ).AcquireAsync(
            "板块成分股", Src(DataSourceId.EmPush2),                // 要的是 push2
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.Acquired, r.Kind);
    }

    // ─────────────── 让路：三种不该抢的情形 ───────────────

    [Fact]
    public async Task 手动触发的项不抢占_只让路()
    {
        var occ = new SourceOccupancy();
        var (blocker, _) = StartFake(occ, "板块成分股", DataSourceId.EmPush2);

        var r = await NewAdmission(occ).AcquireAsync(
            "重新拉取失败", Src(DataSourceId.EmPush2),
            isTimedItem: true, fromPlan: false,           // ← 手动点的
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.GaveWay, r.Kind);
        Assert.Null(r.Lease);
        Assert.False(blocker.Cts.IsCancellationRequested, "手动触发的项不该把别人停掉");
        Assert.Contains("手动触发的项不抢占", r.Reason);
    }

    [Fact]
    public async Task 空闲项不抢占_只让路()
    {
        // 2026-09-05 被误伤的【拉取分档资金流】【拉取财务报表】恰好都是空闲项
        var occ = new SourceOccupancy();
        var (blocker, _) = StartFake(occ, "重新拉取失败", DataSourceId.Sina);

        var r = await NewAdmission(occ).AcquireAsync(
            "拉取财务报表", Src(DataSourceId.Sina),
            isTimedItem: false, fromPlan: true,           // ← 空闲项
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.GaveWay, r.Kind);
        Assert.False(blocker.Cts.IsCancellationRequested, "空闲项不该掐掉用户主动点的任务");
        Assert.Contains("空闲项不抢占", r.Reason);
    }

    [Fact]
    public async Task 让路不算失败_原因要能说清在等谁等哪个源()
    {
        var occ = new SourceOccupancy();
        StartFake(occ, "重新拉取失败", DataSourceId.Sina);

        var r = await NewAdmission(occ).AcquireAsync(
            "拉取财务报表", Src(DataSourceId.Sina),
            isTimedItem: false, fromPlan: true,
            new CancellationTokenSource());

        Assert.Contains("重新拉取失败", r.Reason);      // 在等谁
        Assert.Contains("新浪", r.Reason);              // 等哪个源
        Assert.Equal("重新拉取失败", r.Blocker);
        Assert.Equal(DataSourceId.Sina, r.BlockedSource);
    }

    // ─────────────── 抢占：定时项该抢就抢 ───────────────

    [Fact]
    public async Task 定时项抢占手工任务_对方停下后接着跑()
    {
        var occ = new SourceOccupancy();
        var (blocker, finish) = StartFake(occ, "重新拉取失败", DataSourceId.Sina);

        // 模拟真实任务：收到取消信号后收尾、从占用表注销
        blocker.Cts.Token.Register(() => Task.Run(async () =>
        {
            await Task.Delay(30);       // 收尾要一点时间
            finish();
        }));

        var log = new List<string>();
        var started = new List<string>();
        var r = await NewAdmission(occ, log).AcquireAsync(
            "板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource(), onPreemptStart: started.Add);

        Assert.Equal(AdmissionKind.AcquiredAfterPreempt, r.Kind);
        Assert.NotNull(r.Lease);
        Assert.True(blocker.Cts.IsCancellationRequested, "该给被抢者发停止信号");
        Assert.Equal(["重新拉取失败"], started);          // 界面钩子收到了通知
        Assert.Contains(log, l => l.Contains("计划抢占") && l.Contains("重新拉取失败"));
        Assert.Contains(log, l => l.Contains("已停止") && l.Contains("接着跑"));
    }

    [Fact]
    public async Task 抢占日志要写清对方跑了多久()
    {
        var occ = new SourceOccupancy();
        var (blocker, finish) = StartFake(occ, "重新拉取失败", DataSourceId.Sina);
        blocker.Cts.Token.Register(() => finish());

        var log = new List<string>();
        await NewAdmission(occ, log).AcquireAsync(
            "板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());

        // 出问题时要能复盘：抢了谁、因为哪个源、对方跑了多久
        var line = Assert.Single(log, l => l.Contains("计划抢占"));
        Assert.Contains("新浪", line);
        Assert.Contains("已跑", line);
        Assert.Contains("数据不会丢", line);
    }

    // ─────────────── 抢不动：绝不硬上 ───────────────

    [Fact]
    public async Task 对方停不下来_放弃这一轮而不是硬上()
    {
        var occ = new SourceOccupancy();
        // 这个假任务**故意不收尾**——模拟死锁（今天验证按钮那种）
        var (blocker, _) = StartFake(occ, "卡死的任务", DataSourceId.Sina);

        var log = new List<string>();
        var r = await NewAdmission(occ, log, waitLimit: TimeSpan.FromMilliseconds(150))
            .AcquireAsync("板块成分股", Src(DataSourceId.Sina),
                isTimedItem: true, fromPlan: true,
                new CancellationTokenSource());

        Assert.Equal(AdmissionKind.PreemptTimedOut, r.Kind);
        Assert.Null(r.Lease);                             // ← 关键：没拿到就是没拿到，不硬上

        // 占用表里那个卡死的任务还在，没被强行抹掉
        Assert.Contains(occ.Snapshot(), t => t.Name == "卡死的任务");

        // 日志要指出这是个 bug，不能只说"失败了"
        Assert.Contains(log, l => l.Contains("抢占失败") && l.Contains("卡死")
                              && l.Contains("需要修的 bug"));
    }

    [Fact]
    public async Task 抢占超时后_同一个占用者进入冷却不再反复抢()
    {
        var occ = new SourceOccupancy();
        StartFake(occ, "卡死的任务", DataSourceId.Sina);

        var admission = NewAdmission(occ, waitLimit: TimeSpan.FromMilliseconds(100),
                                     cooldown: TimeSpan.FromMinutes(5));

        var first = await admission.AcquireAsync("板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());
        Assert.Equal(AdmissionKind.PreemptTimedOut, first.Kind);

        // 第二次：冷却期内，直接让路，不该再抢一遍（否则每分钟撞一次、日志刷屏）
        var second = await admission.AcquireAsync("板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.GaveWay, second.Kind);
        Assert.Contains("刚被抢占过", second.Reason);
    }

    [Fact]
    public async Task 冷却过期后_可以再抢一次()
    {
        var occ = new SourceOccupancy();
        var (blocker, finish) = StartFake(occ, "重新拉取失败", DataSourceId.Sina);

        var admission = NewAdmission(occ, waitLimit: TimeSpan.FromMilliseconds(80),
                                     cooldown: TimeSpan.FromMilliseconds(50));

        var first = await admission.AcquireAsync("板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());
        Assert.Equal(AdmissionKind.PreemptTimedOut, first.Kind);

        await Task.Delay(80);       // 等冷却过去
        blocker.Cts.Token.Register(() => finish());
        finish();                    // 这次它收尾了

        var second = await admission.AcquireAsync("板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.Acquired, second.Kind);
    }

    // ─────────────── 边界 ───────────────

    [Fact]
    public async Task 等待期间被第三方占走_不连环抢占()
    {
        var occ = new SourceOccupancy();
        var (blocker, finish) = StartFake(occ, "重新拉取失败", DataSourceId.Sina);

        // 被抢者一停，立刻有**第三个**任务抢占同一个源
        blocker.Cts.Token.Register(() => Task.Run(async () =>
        {
            await Task.Delay(30);
            finish();
            StartFake(occ, "半路杀出的任务", DataSourceId.Sina);
        }));

        var r = await NewAdmission(occ, waitLimit: TimeSpan.FromMilliseconds(400))
            .AcquireAsync("板块成分股", Src(DataSourceId.Sina),
                isTimedItem: true, fromPlan: true,
                new CancellationTokenSource());

        // 不该把第三方也一起掐了——让路，下一轮重来
        Assert.Equal(AdmissionKind.GaveWay, r.Kind);
        Assert.Contains("半路杀出的任务", r.Reason);
        Assert.Contains(occ.Snapshot(), t => t.Name == "半路杀出的任务");
    }

    [Fact]
    public async Task 本地任务不占源_永远拿得到()
    {
        var occ = new SourceOccupancy();
        StartFake(occ, "重新拉取失败", DataSourceId.Sina, DataSourceId.Tencent);

        var r = await NewAdmission(occ).AcquireAsync(
            "板块指数合成", Src(),                       // 本地计算，不占任何源
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource());

        Assert.Equal(AdmissionKind.Acquired, r.Kind);
    }

    [Fact]
    public async Task 用户点停止_抢占等待要立刻退出()
    {
        var occ = new SourceOccupancy();
        StartFake(occ, "卡死的任务", DataSourceId.Sina);     // 不收尾

        using var userStop = new CancellationTokenSource();
        var admission = NewAdmission(occ, waitLimit: TimeSpan.FromSeconds(30));   // 上限很长

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = admission.AcquireAsync("板块成分股", Src(DataSourceId.Sina),
            isTimedItem: true, fromPlan: true,
            new CancellationTokenSource(), ct: userStop.Token);

        await Task.Delay(50);
        userStop.Cancel();          // 人点了【停止全部】

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"点停止之后应该立刻退出抢占等待，实际等了 {sw.Elapsed.TotalSeconds:F1} 秒");
    }
}
