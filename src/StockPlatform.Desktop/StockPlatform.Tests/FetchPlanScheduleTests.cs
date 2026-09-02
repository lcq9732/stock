using StockPlatform.Scheduling;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 计划项的**排期判据**（该不该跑 / 跑没跑过 / 什么时候跑）。
///
/// 为什么这块值得单独测：它是纯函数、没有 IO，测起来便宜；而它错了是**静默的**——
/// 界面显示正常、日志没有异常，只有对着"数据日期"和"当前时间"才看得出来。
/// 2026-09-01 一天之内在这里踩了两个坑，都是用户发现的：
///   ① 跨午夜的长任务把第二天整个吃掉（【拉取全部】18:00 开工、次日凌晨收工，
///      LastEnd 落在新的一天 → 新的一天被判"已经跑过"，数据永远停在前一天）；
///   ② 月度任务一次都没跑成（判据写死 day.Day == DayOfMonth，而队列轮到它时
///      已经是 2 号凌晨了 → 静默跳过，下个月重演）。
/// </summary>
public class FetchPlanScheduleTests
{
    private static DateTime D(string s) => DateTime.Parse(s);

    private static FetchPlanItem Monthly(int dayOfMonth = 1, TimeOnly? notBefore = null) => new()
    {
        Action = FetchActionId.FetchIndustry,
        Enabled = true,
        Repeat = RepeatKind.Monthly,
        DayOfMonth = dayOfMonth,
        NotBefore = notBefore,
    };

    // ══ 月度：错过当天要能补跑 ════════════════════════════════════════════

    [Theory]
    [InlineData("2026-09-01", true)]   // 应跑日当天
    [InlineData("2026-09-02", true)]   // 错过了，补跑
    [InlineData("2026-09-15", true)]   // 月中仍待办
    [InlineData("2026-09-30", true)]   // 月末仍待办
    public void 月度_应跑日或之后都算待办(string day, bool expected)
        => Assert.Equal(expected, Monthly().IsDueOn(D(day)));

    [Fact]
    public void 月度_上个月跑过不算本月跑过()
    {
        var it = Monthly(notBefore: new TimeOnly(18, 0));
        it.LastStart = D("2026-08-01 18:05");
        it.LastEnd = D("2026-08-01 18:20");
        it.LastOutcome = RunOutcome.Ok;

        Assert.False(it.AlreadyRanOn(D("2026-09-02")));
    }

    [Fact]
    public void 月度_本期跑过之后本月不再跑_下月又待办()
    {
        var it = Monthly(notBefore: new TimeOnly(18, 0));
        it.LastStart = D("2026-09-01 18:05");
        it.LastEnd = D("2026-09-01 18:20");
        it.LastOutcome = RunOutcome.Ok;

        Assert.True(it.AlreadyRanOn(D("2026-09-02")));
        Assert.True(it.AlreadyRanOn(D("2026-09-20")));
        Assert.False(it.AlreadyRanOn(D("2026-10-01")));
    }

    [Fact]
    public void 月度_补跑时看到的应跑时刻已经过去()
    {
        // 引擎据此判断"早该跑了，立刻开跑"，而不是再等一个周期
        var it = Monthly(notBefore: new TimeOnly(18, 0));
        Assert.True(it.DueTimeOn(D("2026-09-02")) < D("2026-09-02"));
    }

    [Theory]
    [InlineData("2026-09-29", false)]  // 9月只有30天，31号落到30号
    [InlineData("2026-09-30", true)]
    [InlineData("2026-10-30", false)]
    [InlineData("2026-10-31", true)]
    public void 月度_31号在小月落到月末(string day, bool expected)
        => Assert.Equal(expected, Monthly(31).IsDueOn(D(day)));

    // ══ 跨午夜：长任务不能吃掉第二天 ══════════════════════════════════════

    [Fact]
    public void 跨午夜_昨天开工今天凌晨收工_今天仍要跑()
    {
        var it = new FetchPlanItem
        {
            Action = FetchActionId.FetchAll,
            Repeat = RepeatKind.EveryWorkday,
            NotBefore = new TimeOnly(18, 0),
            LastStart = D("2026-08-31 18:00"),   // 周一 18:00 开工
            LastEnd = D("2026-09-01 03:16"),     // 周二 03:16 收工
            LastOutcome = RunOutcome.Ok,
        };

        Assert.False(it.AlreadyRanOn(D("2026-09-01")));
    }

    [Fact]
    public void 跨午夜_没设不早于也一样()
    {
        var it = new FetchPlanItem
        {
            Action = FetchActionId.FetchBoards,
            Repeat = RepeatKind.EveryWorkday,
            LastStart = D("2026-08-31 20:00"),
            LastEnd = D("2026-09-01 02:00"),
            LastOutcome = RunOutcome.Ok,
        };

        Assert.False(it.AlreadyRanOn(D("2026-09-01")));
    }

    [Fact]
    public void 当天开工跑完_当天不再跑_次日照跑()
    {
        var it = new FetchPlanItem
        {
            Action = FetchActionId.FetchAll,
            Repeat = RepeatKind.EveryWorkday,
            NotBefore = new TimeOnly(18, 0),
            LastStart = D("2026-09-01 18:05"),
            LastEnd = D("2026-09-01 23:00"),
            LastOutcome = RunOutcome.Ok,
        };

        Assert.True(it.AlreadyRanOn(D("2026-09-01")));
        Assert.False(it.AlreadyRanOn(D("2026-09-02")));
    }

    // ══ 每周：本周内补跑，跨周重置 ════════════════════════════════════════

    private static FetchPlanItem Weekly() => new()
    {
        Action = FetchActionId.FetchShareholder,
        Repeat = RepeatKind.Weekly,
        Weekday = DayOfWeek.Monday,
        NotBefore = new TimeOnly(20, 0),
    };

    [Theory]
    [InlineData("2026-08-31", true)]   // 周一，应跑日
    [InlineData("2026-09-02", true)]   // 周三，补跑
    [InlineData("2026-09-06", true)]   // 周日，本周最后一天仍待办
    public void 每周_应跑日或之后都算待办(string day, bool expected)
        => Assert.Equal(expected, Weekly().IsDueOn(D(day)));

    [Fact]
    public void 每周_本周跑过之后本周不再跑_下周又待办()
    {
        var it = Weekly();
        it.LastStart = D("2026-08-31 20:05");
        it.LastEnd = D("2026-08-31 20:30");
        it.LastOutcome = RunOutcome.Ok;

        Assert.True(it.AlreadyRanOn(D("2026-09-02")));
        Assert.False(it.AlreadyRanOn(D("2026-09-07")));
    }

    // ══ 失败/跳过按自然日，不跟周期 ═══════════════════════════════════════

    [Fact]
    public void 失败_当天不重试_次日重试而不是等下个月()
    {
        // 月度项要是按周期算失败，失败一次就得等下个月，太狠
        var it = Monthly();
        it.LastStart = D("2026-09-02 10:00");
        it.LastEnd = D("2026-09-02 10:05");
        it.LastOutcome = RunOutcome.Failed;

        Assert.True(it.AlreadyFailedOn(D("2026-09-02")));
        Assert.False(it.AlreadyFailedOn(D("2026-09-03")));
        Assert.False(it.AlreadyRanOn(D("2026-09-03")));   // 失败不算跑过
    }

    [Fact]
    public void 跳过_不算已完成()
    {
        // 前置失败被跳过的，前置后来补上了就该有机会再跑（见 AlreadyRanOn 的注释）
        var it = Monthly();
        it.LastStart = D("2026-09-01 18:00");
        it.LastEnd = D("2026-09-01 18:00");
        it.LastOutcome = RunOutcome.Skipped;

        Assert.True(it.AlreadySkippedOn(D("2026-09-01")));
        Assert.False(it.AlreadyRanOn(D("2026-09-01")));
    }

    // ══ 其它重复类型 ═════════════════════════════════════════════════════

    [Theory]
    [InlineData("2026-09-01", true)]    // 周二
    [InlineData("2026-09-05", false)]   // 周六
    [InlineData("2026-09-06", false)]   // 周日
    public void 每工作日_周末不跑(string day, bool expected)
        => Assert.Equal(expected, new FetchPlanItem
        {
            Action = FetchActionId.FetchAll,
            Repeat = RepeatKind.EveryWorkday,
        }.IsDueOn(D(day)));

    [Fact]
    public void 手动项永远不自动跑()
        => Assert.False(new FetchPlanItem
        {
            Action = FetchActionId.FetchDay,
            Repeat = RepeatKind.Manual,
        }.IsDueOn(D("2026-09-01")));

    [Fact]
    public void 空闲项每天都可以跑_跑过一轮不代表今天不用再跑()
    {
        var it = new FetchPlanItem
        {
            Action = FetchActionId.FetchFinancials,
            Repeat = RepeatKind.WhenIdle,
            LastStart = D("2026-09-01 10:00"),
            LastEnd = D("2026-09-01 10:30"),
            LastOutcome = RunOutcome.Ok,
        };

        Assert.True(it.IsDueOn(D("2026-09-01")));
        Assert.False(it.AlreadyRanOn(D("2026-09-01")));   // 空闲项不受"今天跑过"约束
    }
}
