using StockPlatform.Data.Orchestration;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 计划引擎的**兜底行为**：每一项该跑的时候跑得到、跑完了不再跑、出岔子不拖累别人。
///
/// 为什么要有这一整套：2026-09-02 上线当晚出了个死循环——【重取前复权】跑完之后
/// <see cref="FetchPlanItem.AlreadyRanOn"/> 判不出"跑过了"，四秒一轮无限重跑，
/// 把整晚的抓取窗口全占了。判据本身只有三行，错在**没有对着全部任务逐项验证过**：
/// 单看一两个样本任务是对的，换成"支持分批 + 立即执行"这个组合就塌了。
/// 所以这里不再挑样本——直接遍历默认计划的每一项，把两个方向都钉住：
///   ① 不多跑：成功跑完一轮，必须判成"今天跑过了"；
///   ② 不漏跑：从没跑过的项，到点必须判成待办。
/// 加一项新任务、改一次 Pacing/SupportsPartialRun，这里就会替你把两个方向都过一遍。
/// </summary>
public class PlanRobustnessTests
{
    public static IEnumerable<object[]> 默认计划所有项()
        => FetchPlan.CreateDefault().AllItems
            .Select(i => new object[] { i.Action })
            .ToList();

    /// <summary>从默认计划里取出某一项（连着它所在的组，组决定重复规则和触发时刻）。</summary>
    private static FetchPlanItem Pick(FetchActionId action)
    {
        var plan = FetchPlan.CreateDefault();
        plan.LinkOwners();
        return plan.AllItems.Single(i => i.Action == action);
    }

    // ══ ① 不多跑：跑完一轮就得算跑过 ══════════════════════════════════════

    /// <summary>
    /// 这就是死循环那道口子。注意 <c>LastNothingToDo = false</c>——这是最坏的情况：
    /// 任务干了活但**没说自己干完了**（大多数任务都不报这个）。这种情况下：
    ///   · 【立即执行】的项一律算跑过。它们没有冷却挡着，判不出来就是几秒一轮地重跑；
    ///     真还剩点尾巴，明天到点接着做，反正水位线记着。
    ///   · 【空闲时】且**真会分批**的项例外，按设计要在同一天里接着补（有 20 分钟冷却兜着）。
    /// </summary>
    [Theory]
    [MemberData(nameof(默认计划所有项))]
    public void 每一项成功跑完一轮后都不该再跑(FetchActionId action)
    {
        var it = Pick(action);
        var day = new DateTime(2026, 9, 2);
        var due = it.DueTimeOn(day);

        it.LastOutcome = RunOutcome.Ok;
        it.LastStart = due.AddMinutes(5);
        it.LastEnd = due.AddMinutes(40);
        it.LastNothingToDo = false;   // 最坏情况：没报"已经齐了"

        bool 按设计要接着补 = it.Pacing == RunPacing.WhenIdle && it.SupportsPartialRunNow;
        Assert.Equal(!按设计要接着补, it.AlreadyRanOn(due.AddMinutes(45)));
    }

    /// <summary>分批补的项报了"已经齐了"就必须收工，否则冷却一到又来一轮，空转到天亮。</summary>
    [Theory]
    [MemberData(nameof(默认计划所有项))]
    public void 每一项报了已经齐了就必须算跑过(FetchActionId action)
    {
        var it = Pick(action);
        var day = new DateTime(2026, 9, 2);
        var due = it.DueTimeOn(day);

        it.LastOutcome = RunOutcome.Ok;
        it.LastStart = due.AddMinutes(5);
        it.LastEnd = due.AddMinutes(10);
        it.LastNothingToDo = true;

        Assert.True(it.AlreadyRanOn(due.AddMinutes(15)), $"【{it.Info.Name}】报了没什么可做，却还算待办");
    }

    // ══ ② 不漏跑：到点了就得排上 ══════════════════════════════════════════

    /// <summary>
    /// 一次没跑过的项，在它自己的应跑日必须判成待办。漏跑比多跑更难发现——
    /// 界面上安安静静，只有过几天对数据日期才看得出来。
    /// </summary>
    [Theory]
    [MemberData(nameof(默认计划所有项))]
    public void 从没跑过的项在应跑日必须算待办(FetchActionId action)
    {
        var it = Pick(action);
        if (it.Repeat == RepeatKind.Manual) return;   // 手动项本来就等人点

        // 挑一个对三种重复规则都成立的日子：2026-09-07 是周一，也在月度应跑日之后
        var day = new DateTime(2026, 9, 7);
        Assert.True(it.IsDueOn(day), $"【{it.Info.Name}】（{it.RepeatText}）在 {day:M/d} 没被当成待办");
        Assert.False(it.AlreadyRanOn(day), $"【{it.Info.Name}】从没跑过，却被当成跑过了");
    }

    /// <summary>昨天跑过不算今天跑过——日更项的命门，跨午夜那个坑就是从这儿漏的。</summary>
    [Theory]
    [MemberData(nameof(默认计划所有项))]
    public void 日更项昨天跑过不算今天跑过(FetchActionId action)
    {
        var it = Pick(action);
        if (it.Repeat != RepeatKind.EveryWorkday) return;

        // 判据要传**当时的时刻**：一轮的归属看「当期锚点」（最近一个已经过去的到点时刻），
        // 不看自然日。所以"今天"要取今天到点之后的那一刻，才是真的进了新一轮。
        var today = new DateTime(2026, 9, 2);
        var 昨天到点 = it.DueTimeOn(today.AddDays(-1));
        var 今天到点 = it.DueTimeOn(today);
        it.LastOutcome = RunOutcome.Ok;
        it.LastStart = 昨天到点.AddMinutes(5);
        it.LastEnd = 昨天到点.AddMinutes(50);
        it.LastNothingToDo = true;

        Assert.True(it.AlreadyRanOn(今天到点.AddMinutes(-1)),
            $"【{it.Info.Name}】今天还没到点，昨晚那一轮却不算数了");
        Assert.False(it.AlreadyRanOn(今天到点), $"【{it.Info.Name}】昨天跑过就把今天顶掉了");
    }

    // ══ ③ 超时 ≠ 手动停止 ════════════════════════════════════════════════

    private sealed record Harness(FetchPlan Plan, FetchPlanStore Store, FetchPaths Paths,
        FetchPlanItem First, FetchPlanItem Second, string Dir);

    /// <summary>
    /// 跑到被停止为止。停止的收尾有两种：调度循环自己看到 ct 就干净退出，或者正在跑的那项
    /// 把取消抛出来——两种都算正常，测试要看的是**停之前跑了哪些项、各自记了什么结果**。
    /// </summary>
    private static async Task RunUntilStopped(PlanRunner runner, CancellationToken ct)
    {
        try { await runner.RunAsync(ct); }
        catch (OperationCanceledException) { }
    }

    /// <summary>两项、一个组、都已到点——够用来看"第一项出事之后第二项还跑不跑"。</summary>
    private static Harness NewHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "planrobust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var group = new FetchPlanGroup
        {
            Name = "测试组", Enabled = true,
            Repeat = RepeatKind.EveryWorkday, Pacing = RunPacing.Immediate,
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

    /// <summary>
    /// HttpClient 超时抛的是 TaskCanceledException，它也是 OperationCanceledException。
    /// 要是引擎不看 CancellationToken 就一律当成"用户点了停止"，一次网络抖动会把
    /// 整份计划掀掉——夜里没人看着，后面十几项全不跑，第二天才发现没数据。
    /// 这里让第一项超时，断言：它记成失败，**后面的项照跑**。
    /// </summary>
    [Fact]
    public async Task 抓取超时只算这一项失败_计划继续往下跑()
    {
        var h = NewHarness();
        try
        {
            using var cts = new CancellationTokenSource();
            var 跑过的 = new List<FetchActionId>();
            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: (item, deadline, progress, ct) =>
                {
                    跑过的.Add(item.Action);
                    if (item.Action == h.First.Action)
                        throw new TaskCanceledException(
                            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                    cts.Cancel();   // 第二项也跑到了，够了——收工
                    return Task.FromResult(new FetchResult());
                },
                log: _ => { }, onState: _ => { });

            await RunUntilStopped(runner, cts.Token);

            Assert.Contains(h.Second.Action, 跑过的);                  // ← 关键：没被超时掀掉
            Assert.Equal(RunOutcome.Failed, h.First.LastOutcome);      // 记失败，不是"被停止"
            Assert.Equal(RunOutcome.Ok, h.Second.LastOutcome);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>
    /// 超时记成失败还有一层意思：当天不再重试它（<see cref="FetchPlanItem.AlreadyFailedOn"/>），
    /// 明天到点自己会来。要是记成"被停止"，那既不算跑过也不算失败——立即执行的项
    /// 没有冷却挡着，会立刻被重新挑中，又是一个死循环。
    /// </summary>
    [Fact]
    public async Task 超时失败的项当天不会被反复重挑()
    {
        var h = NewHarness();
        try
        {
            using var cts = new CancellationTokenSource();
            int 第一项跑了几次 = 0;
            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: (item, deadline, progress, ct) =>
                {
                    if (item.Action == h.First.Action)
                    {
                        第一项跑了几次++;
                        throw new TaskCanceledException("timeout");
                    }
                    cts.Cancel();
                    return Task.FromResult(new FetchResult());
                },
                log: _ => { }, onState: _ => { });

            await RunUntilStopped(runner, cts.Token);
            Assert.Equal(1, 第一项跑了几次);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>
    /// 反过来也要成立：**真**按了停止，就得立刻收手，不能再挑下一项。
    /// 这一条守着上一条别修过头——把真取消也当成"这一项失败"就永远停不下来了。
    /// </summary>
    [Fact]
    public async Task 真的按了停止就不再挑下一项()
    {
        var h = NewHarness();
        try
        {
            using var cts = new CancellationTokenSource();
            var 跑过的 = new List<FetchActionId>();
            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: (item, deadline, progress, ct) =>
                {
                    跑过的.Add(item.Action);
                    cts.Cancel();               // 用户在第一项跑着的时候点了停止
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(new FetchResult());
                },
                log: _ => { }, onState: _ => { });

            await RunUntilStopped(runner, cts.Token);

            Assert.Single(跑过的);                                   // 第二项没被碰过
            Assert.Equal(RunOutcome.Cancelled, h.First.LastOutcome);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    // ══ ④ 今天的清单要说实话 ══════════════════════════════════════════════

    /// <summary>
    /// 计划一开始会打一份"今天的清单"。它按 IsDueOn 列项，而 IsDueOn 只管"今天该不该跑"，
    /// 不管"是不是已经跑过了"——2026-09-02 晚上换新版 exe 接着跑，清单写着"今天要跑 28 项"、
    /// 预计加起来十几个小时，其实前三项在换版之前就做完了（用户反馈：容易误以为还有一整夜的活）。
    /// 所以清单要把已完成的分出来，并且**剩余预计只算还要跑的**。
    /// </summary>
    [Fact]
    public async Task 今日清单把已完成的和还要跑的分开算()
    {
        var h = NewHarness();
        try
        {
            // 第一项当成换版之前就跑完了
            var day = DateTime.Today;
            h.First.LastOutcome = RunOutcome.Ok;
            h.First.LastStart = day.AddHours(9);
            h.First.LastEnd = day.AddHours(9).AddMinutes(30);
            h.First.LastNothingToDo = true;

            // 预计值要在跑之前取——跑完一轮"耗时自学"就把它改写成实测值了（测试里几毫秒）
            var 第二项预计 = Describe(h.Second.EffectiveEstimate);
            var 清单 = await 取今日清单(h);

            Assert.Contains("已完成 1 项", 清单);
            Assert.Contains("还要跑 1 项", 清单);
            Assert.Contains("✔ 已完成", 清单);
            // 剩余预计只能算还要跑的那一项，不能把已完成的也加进去
            Assert.Contains($"预计约 {第二项预计}", 清单);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>都跑完了就别再报个"还要跑 0 项（预计约 0秒）"，直说没有了。</summary>
    [Fact]
    public async Task 今日清单在全跑完时明说没有了()
    {
        var h = NewHarness();
        try
        {
            var day = DateTime.Today;
            foreach (var it in new[] { h.First, h.Second })
            {
                it.LastOutcome = RunOutcome.Ok;
                it.LastStart = day.AddHours(9);
                it.LastEnd = day.AddHours(10);
                it.LastNothingToDo = true;
            }

            var 清单 = await 取今日清单(h);

            Assert.Contains("已完成 2 项", 清单);
            Assert.Contains("今天没有还要跑的了", 清单);
            Assert.DoesNotContain("还要跑", 清单.Replace("今天没有还要跑的了", ""));
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>启动计划、抓下开头那份清单（跑到第一项就停，清单是在那之前打的）。</summary>
    private static async Task<string> 取今日清单(Harness h)
    {
        using var cts = new CancellationTokenSource();
        var 日志 = new List<string>();
        var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
            execute: (item, deadline, progress, ct) =>
            {
                cts.Cancel();
                return Task.FromResult(new FetchResult());
            },
            log: 日志.Add, onState: _ => { });

        // 全跑完的那个用例里没有可执行的项，PlanRunner 会一直待命——给它一小会儿就够了
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await RunUntilStopped(runner, cts.Token);
        return 日志.FirstOrDefault(l => l.Contains("今天的计划共") || l.Contains("今天没有要跑的项"))
               ?? string.Join(" | ", 日志);
    }

    /// <summary>跟 PlanRunner 里同一套说法，免得测试写死"30分钟"这种字面量。</summary>
    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分钟"
        : $"{(int)t.TotalSeconds}秒";

    // ══ ⑤ 一轮跨过午夜要跑完，不能被推到第二天 ══════════════════════════════

    /// <summary>
    /// 2026-09-03 用户发现的漏跑，代价是一整天的数据：9/2 18:00 开工，跑到次日 01:24，
    /// 剩下七项（融资余额、龙虎榜、板块、公告、退市股…）再问"该跑了吗"，判据答的是
    /// "今天的到点是 9/3 18:00，还没到"——那七项就被推迟了一整天，9/2 的数据谁也没抓。
    /// 日志一条异常都没有，界面上还写着"18:00 → 19:35 待执行"，只有对着数据日期才看得出来。
    ///
    /// 修法是「当期锚点」：一轮的归属看**最近一个已经过去的到点时刻**，不看自然日。
    /// </summary>
    [Fact]
    public void 跨午夜之后本轮没跑完的项要接着跑()
    {
        var h = NewHarness();
        try
        {
            h.Plan.Groups[0].NotBefore = new TimeOnly(18, 0);
            var 昨天开工 = new DateTime(2026, 9, 2, 18, 0, 0);
            var 凌晨 = new DateTime(2026, 9, 3, 1, 24, 0);

            // 第一项昨晚跑完了；第二项还欠着
            h.First.LastOutcome = RunOutcome.Ok;
            h.First.LastStart = 昨天开工;
            h.First.LastEnd = 昨天开工.AddHours(1);
            h.First.LastNothingToDo = true;

            Assert.True(h.First.AlreadyRanOn(凌晨), "昨晚跑完的又要再来一遍");
            Assert.False(h.Second.AlreadyRanOn(凌晨), "本轮还欠着的被当成跑过了");
            Assert.Equal(昨天开工, h.Second.DueAnchorAt(凌晨));   // 凌晨仍属昨天 18:00 那一轮
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>
    /// 上一条的反面，同样重要：**那一轮压根没开过工**就不能拿它当借口提前跑。
    /// 一份全新的计划，日更组设了「不早于 18:00」，上午十点绝不能开抓——用户设 18:00
    /// 正是因为收盘前拿不到当天数据。闸门是"组里有没有哪一项在上一个到点之后开过工"。
    /// </summary>
    [Fact]
    public void 上一轮没开过工就不能提前跑()
    {
        var h = NewHarness();
        try
        {
            h.Plan.Groups[0].NotBefore = new TimeOnly(18, 0);
            var 上午十点 = new DateTime(2026, 9, 3, 10, 0, 0);

            // 两项都从没跑过 —— 昨天那一轮不存在
            Assert.Null(h.First.DueAnchorAt(上午十点));
            Assert.Null(h.Second.DueAnchorAt(上午十点));

            // 到了今天 18:00 才有锚点
            Assert.Equal(new DateTime(2026, 9, 3, 18, 0, 0),
                h.First.DueAnchorAt(new DateTime(2026, 9, 3, 18, 0, 0)));
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>周五 18:00 那轮跑到周六还没完，周六要接着跑——不能因为"周六不是工作日"就停摆。</summary>
    [Fact]
    public void 周五那轮跨到周末也要跑完()
    {
        var h = NewHarness();
        try
        {
            h.Plan.Groups[0].NotBefore = new TimeOnly(18, 0);
            var 周五开工 = new DateTime(2026, 9, 4, 18, 0, 0);      // 2026-09-04 是周五
            var 周六上午 = new DateTime(2026, 9, 5, 10, 0, 0);

            h.First.LastOutcome = RunOutcome.Ok;
            h.First.LastStart = 周五开工;
            h.First.LastEnd = 周五开工.AddHours(6);
            h.First.LastNothingToDo = true;

            Assert.Equal(周五开工, h.Second.DueAnchorAt(周六上午));
            Assert.False(h.Second.AlreadyRanOn(周六上午), "周五那轮欠着的项到周六不跑了");
            Assert.True(h.First.AlreadyRanOn(周六上午), "周五跑完的到周六又要来一遍");
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>
    /// 一项**卡住不返回**（不抛异常，就是不回来）必须被硬超时掐断，后面的项照跑。
    ///
    /// 这是 2026-09-04 补的兜底：调度循环串行 await、绝不并发，所以卡住一项就堵死整个循环——
    /// 不光当天后面的项不跑，第二天的轮次也开不了工，非得有人手动停一次。实测
    /// 【概念和行业板块】和【板块成分股】就这么卡过，一度只敢手动触发。
    ///
    /// 跟上面那个「抓取超时」测试的区别：那边任务**主动抛**了 TaskCanceledException，
    /// 这边任务什么都不抛、单纯不返回——只有引擎自己带表才掐得动。
    /// </summary>
    [Fact]
    public async Task 卡住不返回的项被掐断_计划继续往下跑()
    {
        var h = NewHarness();
        try
        {
            using var cts = new CancellationTokenSource();
            var 跑过的 = new List<FetchActionId>();
            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: async (item, deadline, progress, ct) =>
                {
                    跑过的.Add(item.Action);
                    if (item.Action == h.First.Action)
                        await Task.Delay(Timeout.Infinite, ct);   // 永远不返回，但响应 ct
                    cts.Cancel();                                  // 第二项也跑到了，收工
                    return new FetchResult();
                },
                log: _ => { }, onState: _ => { },
                hardBudgetOverride: TimeSpan.FromMilliseconds(300));

            await RunUntilStopped(runner, cts.Token);

            Assert.Contains(h.Second.Action, 跑过的);              // ← 关键：没被卡死的那项堵住
            Assert.Equal(RunOutcome.Failed, h.First.LastOutcome);  // 掐断算"这一项失败"，不是"被停止"
            Assert.Equal(RunOutcome.Ok, h.Second.LastOutcome);
            Assert.Contains("掐断", h.First.LastMessage ?? "");    // 失败原因要说清是超时，不是别的错
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }

    /// <summary>手动停止不能被误认成硬超时——两者抛的都是 OperationCanceledException，
    /// 靠各自的 token 区分。认错了会把"用户按了停止"当成超时、继续跑下一项。</summary>
    [Fact]
    public async Task 手动停止不会被误判成超时()
    {
        var h = NewHarness();
        try
        {
            using var cts = new CancellationTokenSource();
            var 跑过的 = new List<FetchActionId>();
            var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
                execute: async (item, deadline, progress, ct) =>
                {
                    跑过的.Add(item.Action);
                    cts.Cancel();                       // 模拟用户在这一项跑着时按了停止
                    await Task.Delay(Timeout.Infinite, ct);
                    return new FetchResult();
                },
                log: _ => { }, onState: _ => { },
                hardBudgetOverride: TimeSpan.FromMinutes(10));   // 预算很宽，不该是它触发

            await RunUntilStopped(runner, cts.Token);

            Assert.Single(跑过的);                                  // 停了就不再挑下一项
            Assert.Equal(RunOutcome.Cancelled, h.First.LastOutcome);
        }
        finally { try { Directory.Delete(h.Dir, true); } catch { } }
    }
}
