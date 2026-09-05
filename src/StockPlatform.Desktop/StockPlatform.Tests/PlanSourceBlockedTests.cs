using System.IO;
using StockPlatform.Data.Orchestration;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 数据源被别人占着时，计划该怎么表现（2026-09-05）。
///
/// ════ 这组测试防的是什么 ════
/// 用户手动跑了一项占用多个数据源的任务，计划里的项就通不过 <c>IsSourceBusy</c>。
/// 让路本身是对的——占用是临时的，记成失败/跳过反而会让这一项今天再也不跑。
/// 出事的是**界面什么都不说**：状态栏只剩一句「计划已在待命——今天没有待执行的项了」，
/// 而实际上有好几项正等着那个手动任务释放数据源。
///
/// 2026-09-05 实测后果：用户手动跑【重新拉取失败】（那时它没声明 Sources，
/// Mixed 兜底成**全部 9 个源**），计划停摆 2 小时 41 分，界面全程显示"没有待执行的项了"。
/// 期间两个空闲项排队 30 分钟后被硬超时掐断、记成"失败"——它们一个请求都没发过。
///
/// 所以这里钉两件事：① 被占住时状态文案要说清在等谁；② 占用一解除，项要立刻能跑。
/// </summary>
public class PlanSourceBlockedTests
{
    private sealed record Harness(FetchPlan Plan, FetchPlanStore Store, FetchPaths Paths,
                                  FetchPlanItem Item, string Dir);

    /// <summary>一项、已到点的「仅一次」项——够用来看"被占住时说什么"。</summary>
    private static Harness NewHarness(FetchActionId action)
    {
        var dir = Path.Combine(Path.GetTempPath(), "planblocked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var group = new FetchPlanGroup
        {
            Name = "测试组", Enabled = true,
            // ⚠ 必须是 Once：调度循环用真实 DateTime.Now，日更项周末不该跑（见 PlanRobustnessTests）
            Repeat = RepeatKind.Once, Pacing = RunPacing.Immediate,
        };
        var item = new FetchPlanItem { Action = action, Enabled = true, Owner = group };
        group.Items.Add(item);
        var plan = new FetchPlan { Groups = { group } };
        plan.LinkOwners();
        return new Harness(plan, new FetchPlanStore(Path.Combine(dir, "fetch-plan.json")),
                           new FetchPaths(dir), item, dir);
    }

    private static CancellationTokenSource Cts() => new();

    [Fact]
    public async Task 源被占住时状态要说清在等谁_不能说没活了()
    {
        // 【板块成分股】占 push2，测试项也占 push2 → 必然冲突
        var h = NewHarness(FetchActionId.StepBoardMembers);
        try
        {
            var occ = new SourceOccupancy();
            var states = new List<string>();
            using var cts = new CancellationTokenSource();

            // 先让别人占住 push2
            var blocker = occ.TryAcquire("板块成分股", FetchTaskCatalog.Info(FetchActionId.StepBoardMembers).EffectiveSources,
                                         manual: true, Cts(), out _, out _);
            Assert.NotNull(blocker);

            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: (_, _, _, _) =>
                {
                    Assert.Fail("源被占着，这一项不该开跑");
                    return Task.FromResult(new FetchResult());
                },
                log: _ => { },
                onState: s =>
                {
                    lock (states) states.Add(s.Text);
                    if (states.Count >= 1) cts.Cancel();   // 拿到一条状态就够了
                },
                occupancy: occ);

            try { await runner.RunAsync(cts.Token); } catch (OperationCanceledException) { }

            var text = string.Join(" | ", states);
            // 关键断言：不能再说"没有待执行的项了"
            Assert.DoesNotContain("没有待执行的项", text);
            // 要说清在让路、在等谁
            Assert.Contains("让路", text);
            Assert.Contains("板块成分股", text);      // 占用者的名字
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    [Fact]
    public async Task 没人占源时仍然报待命()
    {
        var h = NewHarness(FetchActionId.StepBoardMembers);
        try
        {
            // 先把这一项标成今天跑过了，这样它不该被挑中，调度循环会走到"空窗"分支
            h.Item.LastEnd = DateTime.Now;
            h.Item.LastOutcome = RunOutcome.Ok;

            var occ = new SourceOccupancy();          // 没有任何占用
            var states = new List<string>();
            using var cts = new CancellationTokenSource();

            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: (_, _, _, _) => Task.FromResult(new FetchResult()),
                log: _ => { },
                onState: s =>
                {
                    lock (states) states.Add(s.Text);
                    if (states.Count >= 1) cts.Cancel();
                },
                occupancy: occ);

            try { await runner.RunAsync(cts.Token); } catch (OperationCanceledException) { }

            var text = string.Join(" | ", states);
            Assert.Contains("待命", text);
            Assert.DoesNotContain("让路", text);      // 没人占源，别乱报让路
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    [Fact]
    public void 重新拉取失败不再占满全部数据源()
    {
        // 2026-09-05 的祸首：它没声明 Sources，Mixed 兜底成全部 9 个联网源，
        // 于是它一跑，计划里没有任何一项能通过 IsSourceBusy。
        var retry = FetchTaskCatalog.Info(FetchActionId.RetryFailed).EffectiveSources;

        Assert.True(retry.Count < DataSourceCatalog.AllOnline.Length,
            "【重新拉取失败】又变回占满全部数据源了——它一跑计划就会整个停摆");

        // 它真正会用到的三个（K线主源、新浪那一堆、指数权重）
        Assert.Contains(DataSourceId.Tencent, retry);
        Assert.Contains(DataSourceId.Sina, retry);
        Assert.Contains(DataSourceId.CsIndex, retry);
    }

    [Theory]
    // 这三项是收窄之后应该能跟【重新拉取失败】并行的——它们各自走东财的不同域名
    [InlineData(FetchActionId.StepBoardMembers)]   // push2
    [InlineData(FetchActionId.FetchMoneyFlowDetail)] // push2his
    [InlineData(FetchActionId.StepBoardList)]      // quote（菜单 JSON）
    public void 东财那几项能跟重新拉取失败并行(FetchActionId action)
    {
        var retry = FetchTaskCatalog.Info(FetchActionId.RetryFailed).EffectiveSources;
        var other = FetchTaskCatalog.Info(action).EffectiveSources;

        Assert.False(retry.Overlaps(other),
            $"【{FetchTaskCatalog.Info(action).Name}】跟【重新拉取失败】又冲突了——"
            + "2026-09-05 就是因为这个，计划停摆了 2 小时 41 分");
    }
}
