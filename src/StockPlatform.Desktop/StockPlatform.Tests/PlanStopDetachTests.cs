using StockPlatform.Data.Orchestration;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【停止计划】的语义（2026-09-08 用户定的）：**只停"还要不要挑下一项"，不碰正在跑的任务**。
///
/// ════ 为什么要专门钉住 ════
/// 这套语义有三个反直觉的地方，每一个错了都很难从界面上看出来：
///   ① 停止时正在跑的那一项**不能**被取消——它的令牌以前是从计划循环那个令牌链下来的，
///      一改回去（比如某天觉得"停止就该停干净"）就会把抓了两小时的活掐在半路；
///   ② 循环**不能**等它跑完才退出——等的话按钮点下去要过几十分钟才变色，人只会再点几次；
///   ③ 再点【开始执行计划】时，那一项要被**接手回来**当当前项，而不是绕开它另跑一份，
///      也不是把它当成"今天已经跑过"直接跳过。
/// 三条都只在"停止"这个时机上成立，正常跑一遍任何测试都覆盖不到。
/// </summary>
public class PlanStopDetachTests
{
    private sealed record Harness(FetchPlan Plan, FetchPlanStore Store, FetchPaths Paths,
        FetchPlanItem First, FetchPlanItem Second, string Dir);

    private static Harness NewHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plandetach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 「仅一次」而不是日更：调度循环用的是真实的 DateTime.Now，日更项周末不该跑，
        // 那样一项都挑不中、测试会空等（见 PlanRobustnessTests.NewHarness 里那段说明）。
        var group = new FetchPlanGroup
        {
            Name = "测试组", Enabled = true,
            Repeat = RepeatKind.Once, Pacing = RunPacing.Immediate,
        };
        var a = new FetchPlanItem { Action = FetchActionId.StepStockDayBars, Enabled = true, Owner = group };
        var b = new FetchPlanItem { Action = FetchActionId.StepNetInflow, Enabled = true, Owner = group };
        group.Items.Add(a);
        group.Items.Add(b);
        var plan = new FetchPlan { Groups = { group } };
        plan.LinkOwners();
        return new Harness(plan, new FetchPlanStore(Path.Combine(dir, "fetch-plan.json")),
            new FetchPaths(dir), a, b, dir);
    }

    private static async Task RunUntilDone(Task run)
    {
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10))) != run)
            Assert.Fail("计划 10 秒还没收工——多半是停止之后还在等那一项跑完（那正是这组测试要防的）。");
        await run;
    }

    /// <summary>
    /// 核心那条：停止时正在跑的那一项**没被取消**，而且循环**没有等它**就退出了。
    /// </summary>
    [Fact]
    public async Task 停止计划_不取消正在跑的那一项_也不等它()
    {
        var h = NewHarness();
        try
        {
            using var stop = new CancellationTokenSource();
            var 开跑了 = new TaskCompletionSource();
            var 放行 = new TaskCompletionSource();
            CancellationToken 任务拿到的令牌 = default;

            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: async (item, _, _, ct) =>
                {
                    任务拿到的令牌 = ct;
                    开跑了.TrySetResult();
                    await 放行.Task;                 // 一直卡着，直到测试放行
                    return new FetchResult();
                },
                log: _ => { }, onState: _ => { });

            var run = Task.Run(async () =>
            {
                try { await runner.RunAsync(stop.Token); }
                catch (OperationCanceledException) { }
            });

            await 开跑了.Task;
            stop.Cancel();                            // ← 相当于点【停止计划】

            // ① 循环立刻退出（不等那一项）——这是"点了就说未运行"的前提
            await RunUntilDone(run);

            // ② 那一项没有被取消，还在跑
            Assert.False(任务拿到的令牌.IsCancellationRequested);
            Assert.False(放行.Task.IsCompleted);

            // ③ 放它跑完，账要自己记上（没人认领时由后台延续收尾）
            放行.SetResult();
            var deadline = DateTime.Now.AddSeconds(5);
            while (h.First.LastOutcome == RunOutcome.None && DateTime.Now < deadline)
                await Task.Delay(20);
            Assert.Equal(RunOutcome.Ok, h.First.LastOutcome);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>
    /// 停了之后再开：那一项要被**接手回来**——不重跑一份，账只记一次，
    /// 等它跑完之后计划继续往下走（第二项照样跑到）。
    /// </summary>
    [Fact]
    public async Task 再开计划_接手上一轮还在跑的那一项_不重跑不重复记账()
    {
        var h = NewHarness();
        try
        {
            var detached = new DetachedPlanRuns();
            var 放行 = new TaskCompletionSource();
            var 开跑了 = new TaskCompletionSource();
            int 第一项被调用次数 = 0;

            Func<FetchPlanItem, DateTime?, IProgress<string>, CancellationToken, Task<FetchResult>> execute =
                async (item, _, _, _) =>
                {
                    if (item.Action == h.First.Action)
                    {
                        Interlocked.Increment(ref 第一项被调用次数);
                        开跑了.TrySetResult();
                        await 放行.Task;
                    }
                    return new FetchResult();
                };

            // 第一轮：跑起来就停
            using (var stop1 = new CancellationTokenSource())
            {
                var r1 = new PlanRunner(h.Plan, h.Store, h.Paths, execute,
                    log: _ => { }, onState: _ => { }, detached: detached);
                var run1 = Task.Run(async () =>
                {
                    try { await r1.RunAsync(stop1.Token); }
                    catch (OperationCanceledException) { }
                });
                await 开跑了.Task;
                stop1.Cancel();
                await RunUntilDone(run1);
            }

            Assert.Equal(1, detached.Count);                 // 交出去了，等人认领

            // 第二轮：接手它，等它跑完，再往下跑第二项
            using (var stop2 = new CancellationTokenSource())
            {
                var 跑过的 = new List<FetchActionId>();
                var r2 = new PlanRunner(h.Plan, h.Store, h.Paths,
                    execute: (item, d, p, ct) =>
                    {
                        跑过的.Add(item.Action);
                        if (item.Action == h.Second.Action) stop2.Cancel();   // 第二项也跑到了，收工
                        return execute(item, d, p, ct);
                    },
                    log: _ => { }, onState: _ => { }, detached: detached);
                var run2 = Task.Run(async () =>
                {
                    try { await r2.RunAsync(stop2.Token); }
                    catch (OperationCanceledException) { }
                });

                await Task.Delay(50);
                放行.SetResult();                             // 让上一轮那一项跑完
                await RunUntilDone(run2);

                Assert.DoesNotContain(h.First.Action, 跑过的);  // ← 没有重跑一份
                Assert.Contains(h.Second.Action, 跑过的);       // ← 接手完照常往下走
            }

            Assert.Equal(1, 第一项被调用次数);                 // 全程只跑过一次
            Assert.Equal(RunOutcome.Ok, h.First.LastOutcome);
            Assert.Equal(0, detached.Count);                  // 认领完清空
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>
    /// 单独停掉某一项（右上角那一行的【停止】）：
    ///   ① 记「已取消」而不是「失败」——2026-09-08 之前它落到最后那个兜底 catch 里，
    ///      界面上显示成「✘ 失败」，可人明明是自己停的；
    ///   ② **计划照常往下跑**，不被这一项掀掉；
    ///   ③ 停过之后它**照样会被再排一次**（用户 2026-09-08 定的）：勾着启用、又符合计划，
    ///      那它就该跑——【停止】停的是"这一次执行"，不是"今天别再跑了"。
    ///      不想让它跑就取消那一行的勾选，那才是表达"别跑"的地方。
    /// </summary>
    [Fact]
    public async Task 单独停掉一项_记已取消_计划继续且之后还会再排它()
    {
        var h = NewHarness();
        try
        {
            using var stop = new CancellationTokenSource();
            var 跑过的 = new List<FetchActionId>();
            var 日志 = new List<string>();
            int 第一项跑了几次 = 0;

            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: (item, _, _, ct) =>
                {
                    跑过的.Add(item.Action);
                    if (item.Action == h.First.Action && ++第一项跑了几次 == 1)
                    {
                        // 真实路径：执行入口发现是**自己这一项**的取消源被点了（占用表里那个
                        // itemCts），就翻译成这个明确的异常再往上抛，见 PlanItemStoppedException。
                        throw new PlanItemStoppedException("个股日K·前复权");
                    }
                    if (item.Action == h.Second.Action) stop.Cancel();   // 第二项也跑到了，收工
                    return Task.FromResult(new FetchResult());
                },
                log: 日志.Add, onState: _ => { });

            var run = Task.Run(async () =>
            {
                try { await runner.RunAsync(stop.Token); }
                catch (OperationCanceledException) { }
            });
            await RunUntilDone(run);

            Assert.Contains(日志, l => l.Contains("被单独停止"));          // ① 认出是人停的
            Assert.DoesNotContain(日志, l => l.Contains("✘ 计划：【个股日K·前复权】失败"));
            Assert.Equal(2, 第一项跑了几次);                               // ③ 停过之后又排了一次
            Assert.Contains(h.Second.Action, 跑过的);                      // ② 计划没被掀掉
            Assert.Equal(RunOutcome.Ok, h.Second.LastOutcome);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }
}
