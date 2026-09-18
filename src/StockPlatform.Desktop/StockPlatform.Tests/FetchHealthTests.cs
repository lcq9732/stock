using StockPlatform.Data.Orchestration;
using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// **抓取行为健康**的判据（2026-09-18，见 doc/fetch-health-design.md）。
///
/// ════ 这一组守的是什么 ════
/// 【当日完整性体检】查"库里数据齐不齐"，这一条查"某一项连着几期没跑成"——后者至今没人管：
/// 计划页只显示**上一次**的结果，一项连着一周失败跟偶发失败长得一模一样。
///
/// 最容易写错、而且错得很安静的是**"期"的口径**：
/// 实测生产计划里季度组有 7 项距上次开跑 14~16 天，那全是正常的（月度项本期跑完就等下个月）。
/// 按天数一刀切：阈值给 3 天季度组全误报，给 40 天日更项停两周都不吭声。
/// 所以判据必须按期算，而"期"要经得起跨周末、跨月、跨年。
/// </summary>
public class FetchHealthTests
{
    private static DateTime D(string s) => DateTime.Parse(s);

    private static FetchPlanItem InGroup(RepeatKind repeat, TimeOnly? notBefore = null,
        int dayOfMonth = 1, bool enabled = true,
        FetchActionId action = FetchActionId.StepStockDayBars)
    {
        var g = new FetchPlanGroup
        {
            Name = "测试组", Enabled = true, Repeat = repeat, NotBefore = notBefore, DayOfMonth = dayOfMonth,
        };
        var item = new FetchPlanItem { Action = action, Enabled = enabled, Owner = g };
        g.Items.Add(item);
        return item;
    }

    /// <summary>
    /// 记一次运行。<paramref name="lastOk"/> 是**最后一次成功**的时刻（<c>LastOkAt</c>）——
    /// 引擎只在成功那一刻推进它，所以"最近一次失败、但上周成功过"是完全正常的一组取值。
    /// 不传就跟着这次的结局走（成功＝这次，失败＝没有基准）。
    /// </summary>
    private static FetchPlanItem Ran(FetchPlanItem it, string start, RunOutcome outcome,
                                     string? lastOk = null)
    {
        it.LastStart = D(start);
        it.LastEnd = D(start).AddMinutes(20);
        it.LastOutcome = outcome;
        it.LastOkAt = lastOk is not null ? D(lastOk)
                    : outcome == RunOutcome.Ok ? D(start) : null;
        return it;
    }

    // ══ 每工作日：期＝一个工作日 ══════════════════════════════════════════

    [Fact]
    public void 日更_本期成功过_算零期()
    {
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)), "2026-09-17 19:00", RunOutcome.Ok);

        // 09-18 上午：今天 18:00 还没到，当前仍处在"09-17 18:00 起"这一期里
        Assert.Equal(0, it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 日更_连着三期没成功_数得出三期()
    {
        // 这条是整套告警的**存在理由**：最近一次是失败，而上一次成功在三期之前。
        // 光看 LastOutcome 它跟"偶发失败一次"长得一模一样——分得开全靠 LastOkAt。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
                     start: "2026-09-17 19:00", outcome: RunOutcome.Failed, lastOk: "2026-09-14 19:00");

        // 本期是 09-17 18:00 起（09-18 上午问）；往回 09-16、09-15、09-14
        Assert.Equal(3, it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 日更_偶发失败一次但昨天成功过_不算不健康()
    {
        // 跟上一条的唯一差别是 LastOkAt。阈值是 3 期，这里 0 期——不该报。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
                     start: "2026-09-17 21:00", outcome: RunOutcome.Failed, lastOk: "2026-09-17 19:00");

        Assert.Equal(0, it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 日更_期要跳过周末()
    {
        // 周五 09-11 19:00 跑成功，下一个"期"是周一 09-14 —— 中间的周六周日不算期。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)), "2026-09-11 19:00", RunOutcome.Ok);

        // 周一 09-14 上午问：本期起点是 09-11 18:00（今天 18:00 还没到）⇒ 0 期
        Assert.Equal(0, it.PeriodsSinceLastOk(D("2026-09-14 09:00")));
        // 周二 09-15 上午问：本期起点 09-14 18:00，往回一期才是 09-11 ⇒ 1 期
        Assert.Equal(1, it.PeriodsSinceLastOk(D("2026-09-15 09:00")));
    }

    // ══ 每月：16 天没跑也可能完全正常 ═════════════════════════════════════

    [Fact]
    public void 月度_本期跑过就是健康_哪怕已经十六天()
    {
        // 这条是判据的**存在理由**：生产计划里 FetchIndustry 距上次开跑 16 天，完全正常。
        var it = Ran(InGroup(RepeatKind.Monthly, new TimeOnly(18, 0)), "2026-09-02 18:05", RunOutcome.Ok);

        Assert.Equal(0, it.PeriodsSinceLastOk(D("2026-09-18 10:00")));
    }

    [Fact]
    public void 月度_上上期才成功过_算两期()
    {
        var it = Ran(InGroup(RepeatKind.Monthly, new TimeOnly(18, 0)), "2026-07-03 18:05", RunOutcome.Ok);

        Assert.Equal(2, it.PeriodsSinceLastOk(D("2026-09-18 10:00")));
    }

    // ══ 不判的那些 ═══════════════════════════════════════════════════════

    [Fact]
    public void 没启用的项不判()
    {
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0), enabled: false),
                     start: "2026-09-17 19:00", outcome: RunOutcome.Failed, lastOk: "2026-08-01 19:00");

        Assert.Null(it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 手动项不判()
    {
        // 手动项没有"期"可言，谈不上连着几期没跑。
        var it = Ran(InGroup(RepeatKind.Manual),
                     start: "2026-09-17 10:00", outcome: RunOutcome.Failed, lastOk: "2026-01-01 10:00");

        Assert.Null(it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 刚加进计划还没轮到过的项不判()
    {
        // 一次都没跑过（连失败记录都没有）＝还没开始，不是"不健康"。
        var it = InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0));

        Assert.Null(it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 上次成功久到数不过来_返回上限加一()
    {
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
                     start: "2026-09-17 19:00", outcome: RunOutcome.Failed, lastOk: "2026-01-05 19:00");

        Assert.Equal(13, it.PeriodsSinceLastOk(D("2026-09-18 08:30"), max: 12));   // max + 1
    }

    [Fact]
    public void 没有成功基准的项_不下结论()
    {
        // 老计划升级后的空窗：LastOkAt 这一列还没有，而最近一次恰好不是成功。
        // 宁可空窗一轮等它下次成功把基准记上，也不能升级当天把一堆项报成"从没成功过"。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
                     start: "2026-09-17 19:00", outcome: RunOutcome.Failed);
        it.LastOkAt = null;

        Assert.Null(it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 老计划里最近一次就是成功_直接拿它当基准()
    {
        // 同样没有 LastOkAt，但最近一次是成功的——这时用 LastStart 等价且准确，不必空窗。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)), "2026-09-17 19:00", RunOutcome.Ok);
        it.LastOkAt = null;

        Assert.Equal(0, it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    // ══ 三种"没成功"都算没成功 ═══════════════════════════════════════════

    [Theory]
    [InlineData(RunOutcome.Failed)]
    [InlineData(RunOutcome.Skipped)]
    [InlineData(RunOutcome.Cancelled)]
    public void 失败跳过被停止_都算这一期没成功(RunOutcome outcome)
    {
        // 它们的**处理方式**不同（报的时候分开措辞），但"这一期数据没到位"是一样的。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
                     start: "2026-09-17 19:00", outcome: outcome, lastOk: "2026-09-14 19:00");

        Assert.Equal(3, it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
    }

    // ══ 阈值：日志和界面必须共用同一个 ═══════════════════════════════════

    [Fact]
    public void 阈值按频率给_日更三期月度两期()
    {
        Assert.Equal(3, InGroup(RepeatKind.EveryWorkday).HealthLimitPeriods);
        Assert.Equal(2, InGroup(RepeatKind.Weekly).HealthLimitPeriods);
        Assert.Equal(2, InGroup(RepeatKind.Monthly).HealthLimitPeriods);
    }

    [Fact]
    public void 不超阈值就不算不健康()
    {
        // 日更阈值 3：3 期还在容忍范围内，4 期才报。
        var it = Ran(InGroup(RepeatKind.EveryWorkday, new TimeOnly(18, 0)),
                     start: "2026-09-17 19:00", outcome: RunOutcome.Failed, lastOk: "2026-09-14 19:00");
        Assert.Equal(3, it.PeriodsSinceLastOk(D("2026-09-18 08:30")));
        Assert.Null(it.UnhealthyPeriods(D("2026-09-18 08:30")));

        it.LastOkAt = D("2026-09-11 19:00");        // 再往前一期
        Assert.Equal(4, it.UnhealthyPeriods(D("2026-09-18 08:30")));
    }

    [Fact]
    public void 手动项永远不算不健康()
    {
        var it = Ran(InGroup(RepeatKind.Manual),
                     start: "2026-09-17 10:00", outcome: RunOutcome.Failed, lastOk: "2026-01-01 10:00");

        Assert.Null(it.UnhealthyPeriods(D("2026-09-18 08:30")));
    }

    // ══ 报出来那一段：一轮收尾时的健康告警 ═══════════════════════════════
    //
    // ⚠ 这一段**必须在这里测，不能拿真程序跑**——造"连着几期失败"就得让那些项启用，
    //    而启用的项会被调度真的执行、真的发请求。2026-09-18 就这么误抓了一轮龙虎榜席位。
    //    用假的 execute 委托驱动一轮，既覆盖了编排，又一个请求都不发。

    private sealed record Harness(FetchPlan Plan, FetchPlanStore Store, FetchPaths Paths,
        FetchPlanItem Trigger, FetchPlanItem Sick, string Dir);

    /// <summary>
    /// 两个组：
    ///   · <c>Trigger</c> 在「仅一次」组里——只为把这一轮跑起来、走到收尾汇总；
    ///   · <c>Sick</c> 在**月度**组里——健康判据要"期"，而「仅一次」压根没有期
    ///     （<see cref="FetchPlanGroup.PeriodStartAt"/> 返回 null，判据直接不下结论）。
    ///
    /// ⚠ 触发那一项不能用日更：调度循环读的是真实的 <c>DateTime.Now</c>，而日更项周末不该跑，
    ///   周六周日跑测试就会一项都挑不中、永久干等（2026-09-05 踩过，代价是几小时）。
    /// </summary>
    private static Harness NewHarness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fetchhealth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var once = new FetchPlanGroup
        {
            Name = "触发组", Enabled = true, Repeat = RepeatKind.Once, Pacing = RunPacing.Immediate,
        };
        var trigger = new FetchPlanItem { Action = FetchActionId.StepStockDayBars, Enabled = true, Owner = once };
        once.Items.Add(trigger);

        var monthly = new FetchPlanGroup
        {
            Name = "月度组", Enabled = true, Repeat = RepeatKind.Monthly, DayOfMonth = 1,
            Pacing = RunPacing.Immediate,
        };
        var sick = new FetchPlanItem { Action = FetchActionId.StepNetInflow, Enabled = true, Owner = monthly };
        monthly.Items.Add(sick);

        var plan = new FetchPlan { Groups = { once, monthly } };
        plan.LinkOwners();
        return new Harness(plan, new FetchPlanStore(Path.Combine(dir, "fetch-plan.json")),
            new FetchPaths(dir), trigger, sick, dir);
    }

    /// <summary>
    /// 驱动一轮到收尾。<paramref name="failSick"/> 让"病人"这一轮跑失败——
    /// 这正是真实形状：本轮跑了、失败了，而上次成功在好几期之前。
    /// </summary>
    private static async Task<List<string>> RunOneRoundAsync(Harness h, bool failSick)
    {
        var logs = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(3));    // 兜底：跑完两项打完汇总足够了
        var runner = new PlanRunner(h.Plan, h.Store, h.Paths,
            execute: (item, deadline, progress, ct) =>
            {
                if (failSick && item.Action == h.Sick.Action)
                    throw new InvalidOperationException("东财 datacenter 连不上（连试三次）");
                return Task.FromResult(new FetchResult());
            },
            log: logs.Add, onState: _ => { });
        try { await runner.RunAsync(cts.Token); }
        catch (OperationCanceledException) { /* 正常的收尾路径 */ }
        return logs;
    }

    [Fact]
    public async Task 一轮收尾时报出连着几期不正常的项()
    {
        var h = NewHarness();
        h.Sick.LastOkAt = DateTime.Now.AddMonths(-5);   // 上次成功是 5 期之前（月度阈值 2）

        var logs = await RunOneRoundAsync(h, failSick: true);

        Assert.Contains(logs, l => l.Contains("今天的计划跑完了"));
        var head = Assert.Single(logs.Where(l => l.Contains("抓取健康")));
        Assert.Contains("1 项", head);
        Assert.Contains(logs, l => l.Contains("资金净流入") && l.Contains("期失败"));
        try { Directory.Delete(h.Dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task 错误内容要留进计划项_不能只留个数()
    {
        // 2026-09-18 查【指数权重】那 2 条错误时卡住的地方：它上次跑在日志归档保留期之前，
        // 计划里只有"有 2 条错误"这个数，内容永远查不到了。留前几条，下次就能查。
        var h = NewHarness();

        await RunOneRoundAsync(h, failSick: true);

        Assert.Equal(RunOutcome.Failed, h.Sick.LastOutcome);
        var err = Assert.Single(h.Sick.LastErrors);
        Assert.Contains("datacenter 连不上", err);
        try { Directory.Delete(h.Dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task 这一轮没出错就把上一轮的错误清掉()
    {
        // 留的是"上一轮出了什么错"，不是历史流水——上一轮的错误留到这一轮会让人查错方向。
        var h = NewHarness();
        h.Sick.LastErrors = ["上一轮的陈年老错"];

        await RunOneRoundAsync(h, failSick: false);

        Assert.Empty(h.Sick.LastErrors);
        try { Directory.Delete(h.Dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task 全都健康时一个字都不报()
    {
        // 这一段要是变成每天都有的噪声，第三天就没人看了。
        var h = NewHarness();
        h.Sick.LastOkAt = DateTime.Now.AddDays(-1);

        var logs = await RunOneRoundAsync(h, failSick: false);

        Assert.Contains(logs, l => l.Contains("今天的计划跑完了"));
        Assert.DoesNotContain(logs, l => l.Contains("抓取健康"));
        try { Directory.Delete(h.Dir, recursive: true); } catch { }
    }
}
