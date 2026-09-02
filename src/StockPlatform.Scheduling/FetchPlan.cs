using System.Globalization;

namespace StockPlatform.Scheduling;

/// <summary>
/// 重复规则。**名字进 json**，别改。
/// </summary>
public enum RepeatKind
{
    /// <summary>不自动跑——执行计划时跳过。想跑就在计划页上单独触发这一项。</summary>
    Manual,
    /// <summary>周一到周五。**不是交易日**——本地没有交易日历，节假日会空跑一次；
    /// 所有动作本身都是增量/跳过已有，空跑几分钟没有副作用。</summary>
    EveryWorkday,
    /// <summary>每周固定一天，看 <see cref="FetchPlanItem.Weekday"/>。</summary>
    Weekly,
    /// <summary>每月固定一天，看 <see cref="FetchPlanItem.DayOfMonth"/>；该日超出当月天数时按月末算。</summary>
    Monthly,
    /// <summary>只跑一次，成功后自动取消勾选。</summary>
    Once,
    /// <summary>
    /// **程序空着的时候跑**，一天可以跑很多轮（2026-08-31 新增，把原来【手动】页那个
    /// "空闲时自动补财务"的复选框并进了计划——它本来就是一种触发方式，不该单独长在别处）。
    ///
    /// 什么时候算空着：计划里没有别的项该跑，或者正在等下一个定时项到点、中间那段空窗。
    /// 手动点了任何按钮时它一律让路。跑完一轮歇一会儿再来下一轮（见 PlanRunner 的冷却）。
    ///
    /// ⚠ 这类项**不看"不早于"**——它填的是别人不用的时间，本来就没有固定时刻。
    /// </summary>
    WhenIdle,
}

/// <summary>上一次跑的结果。</summary>
public enum RunOutcome
{
    None,
    Ok,
    Failed,
    /// <summary>前置动作失败、或本身没什么可做，主动跳过的。</summary>
    Skipped,
    Cancelled,
}

/// <summary>计划里的一项。</summary>
public sealed class FetchPlanItem
{
    public FetchActionId Action { get; set; }

    /// <summary>没勾就完全不参与执行。「8 月底才开财务/监管指标」靠的就是它。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// **不早于这个时刻才开始**（HH:mm，null = 上一项跑完就接着跑）。
    ///
    /// ⚠ 语义是"不早于"，不是"准时"。任务串行执行——它们共用限流器、数据库写锁和界面状态，
    /// 并发只会一起去撞同一家的配额。所以上一项超时的话，这一项顺延，绝不抢跑。
    /// </summary>
    public TimeOnly? NotBefore { get; set; }

    public RepeatKind Repeat { get; set; } = RepeatKind.EveryWorkday;

    /// <summary><see cref="RepeatKind.Weekly"/> 时用。</summary>
    public DayOfWeek Weekday { get; set; } = DayOfWeek.Monday;

    /// <summary><see cref="RepeatKind.Monthly"/> 时用，1~31。</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>动作要参数时才有值；留空表示用【手动】页上的对应输入框。</summary>
    public string? DateText { get; set; }
    /// <summary>首次回看几年（只有「拉取全部」用）。留空=用【手动】页那个框。</summary>
    public string? LookbackYearsText { get; set; }
    public string? YearStartText { get; set; }
    public string? YearEndText { get; set; }
    public bool OverwriteQfq { get; set; }

    // ── 运行记录（引擎回写，界面只读）──
    public DateTime? LastStart { get; set; }
    public DateTime? LastEnd { get; set; }
    public RunOutcome LastOutcome { get; set; } = RunOutcome.None;
    public int LastErrorCount { get; set; }
    public string? LastMessage { get; set; }

    public FetchActionInfo Info => FetchTaskCatalog.Info(Action);

    /// <summary>
    /// 今天该不该跑（只看重复规则和日历，不看有没有已经跑过）。
    /// </summary>
    /// <summary>
    /// <paramref name="day"/> 这天该不该跑。
    ///
    /// ⚠ 「每周」「每月」用的是**当期应跑日或之后**，不是"就那一天"（2026-09-01 修）。
    /// 原来写死 <c>day.Day == DayOfMonth</c>，结果是月度任务**一次都没跑过**：
    /// 【拉取全部】排在队首、18:00 开工要跑 9 小时，等它收工已经是 2 号凌晨了，
    /// 队列这才轮到月度项——而那时 IsDueOn(2号) 已经是 false，于是被静默跳过，下个月照样重演。
    /// 程序当天没开、1 号撞上长时间停机，也是同样的下场。
    ///
    /// 改成"过了应跑日就一直待办"之后，什么时候轮到就什么时候补上；
    /// 「这一期到底跑没跑过」交给 <see cref="AlreadyRanOn"/> 按**周期**判断，不会重复跑。
    /// </summary>
    public bool IsDueOn(DateTime day) => Repeat switch
    {
        RepeatKind.Manual => false,
        // 空闲项每天都"可以"跑，跑不跑由 PlanRunner 看当下空不空、离下一个定时项还有多久
        RepeatKind.WhenIdle => true,
        RepeatKind.Once => true,
        RepeatKind.EveryWorkday => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RepeatKind.Weekly or RepeatKind.Monthly => day.Date >= PeriodStartOn(day),
        _ => false,
    };

    /// <summary>
    /// <paramref name="day"/> 所属周期的**应跑日**：每周=本周的那个星期几，每月=本月的那一号
    /// （31 号设在只有 30 天的月份里落到月末），其余=当天。
    /// </summary>
    public DateTime PeriodStartOn(DateTime day) => Repeat switch
    {
        RepeatKind.Weekly => day.Date.AddDays(-(((int)day.DayOfWeek - (int)Weekday + 7) % 7)),
        RepeatKind.Monthly => new DateTime(day.Year, day.Month,
            Math.Min(Math.Max(DayOfMonth, 1), DateTime.DaysInMonth(day.Year, day.Month))),
        _ => day.Date,
    };

    /// <summary>
    /// 今天已经**成功**跑完了吗——用来支持"关了程序再打开接着跑"，不会把已经做完的重来一遍。
    ///
    /// ⚠ 只认 <see cref="RunOutcome.Ok"/>。被跳过（前置失败）的**不算完成**：前置后来要是补上了
    /// （比如你手动跑了一次财务报表），这一项当天还应该有机会跑。早先把 Skipped 也算成"已完成"，
    /// 结果是金融监管指标一旦因财务失败被跳过，那天就再也不会跑了。
    ///
    /// ⚠ **空闲项除外**：它一天本来就要跑很多轮（每轮补一批），跑过一轮不代表今天不用再跑。
    ///
    /// ⚠ 判的是**当前周期**跑没跑过，不是"今天"：月度项在本月应跑日之后跑过一次就够了，
    /// 本月剩下的日子不再重复；到了下个月，<see cref="DueTimeOn"/> 指向新一期，自然又待办。
    /// </summary>
    public bool AlreadyRanOn(DateTime day) =>
        Repeat != RepeatKind.WhenIdle && LastOutcome == RunOutcome.Ok && StartedAfterDueOn(day);

    /// <summary>今天已经因为前置失败被跳过、并且记过一次了——别再重复记。</summary>
    public bool AlreadySkippedOn(DateTime day) =>
        LastOutcome == RunOutcome.Skipped && StartedOnCalendarDay(day);

    /// <summary>
    /// 今天失败过了。当天不自动重来（同一个错误连撞几十次没意义，还会卡住后面的项），明天再试。
    ///
    /// ⚠ 这里按**自然日**判，不跟着 <see cref="AlreadyRanOn"/> 走周期：
    /// 月度任务要是按周期算，失败一次就得等下个月，太狠了。失败次日重试才对。
    /// </summary>
    public bool AlreadyFailedOn(DateTime day) =>
        LastOutcome == RunOutcome.Failed && StartedOnCalendarDay(day);

    /// <summary>上一轮是不是**在 <paramref name="day"/> 当天开始**的（跨午夜跑完的那轮算前一天）。</summary>
    private bool StartedOnCalendarDay(DateTime day)
        => (LastStart ?? LastEnd) is { } t && t.Date == day.Date;

    /// <summary>
    /// 这一项在 <paramref name="day"/> 所属的周期里，最早什么时候可以开跑。
    /// 每周/每月算的是**当期应跑日**那天的「不早于」时刻——所以补跑时（比如 2 号才轮到月度项）
    /// 它返回的是已经过去的时刻，调用方一看就知道"早该跑了，现在立刻跑"。
    /// </summary>
    public DateTime DueTimeOn(DateTime day)
    {
        var d = PeriodStartOn(day);
        return NotBefore.HasValue ? d.Add(NotBefore.Value.ToTimeSpan()) : d;
    }

    /// <summary>
    /// 上一轮是不是**从 <paramref name="day"/> 当天的计划时点之后**开始的——也就是"今天这一轮已经跑过了"。
    ///
    /// ⚠ 为什么不能简单比较 <see cref="LastEnd"/> 落在哪一天（2026-09-01 用户报的 bug）：
    /// 【拉取全部】这类要跑好几个小时，设了「不早于 18:00」的话，昨天 18:00 开工、**今天凌晨 3 点收工**，
    /// LastEnd 就落在了今天。按旧判据今天一整天都算"已经跑过"，于是今天 18:00 那轮被静默跳过，
    /// 数据永远停在前一天，而界面上还显示着"✔ 03:16 完成"，看着一切正常。
    ///
    /// 用**开始时间**跟当天的计划时点比就不会错：昨天 18:00 开的工 &lt; 今天 18:00，所以今天照跑。
    /// LastStart 为空（老版本写的记录）时退回用 LastEnd，行为跟以前一致。
    /// </summary>
    private bool StartedAfterDueOn(DateTime day)
    {
        if (LastEnd is not { } end) return false;
        return (LastStart ?? end) >= DueTimeOn(day);
    }

    public string RepeatText => Repeat switch
    {
        RepeatKind.Manual => "手动",
        RepeatKind.WhenIdle => "空闲时",
        RepeatKind.Once => "仅一次",
        RepeatKind.EveryWorkday => "每工作日",
        RepeatKind.Weekly => "每周" + Weekday switch
        {
            DayOfWeek.Monday => "一", DayOfWeek.Tuesday => "二", DayOfWeek.Wednesday => "三",
            DayOfWeek.Thursday => "四", DayOfWeek.Friday => "五", DayOfWeek.Saturday => "六",
            _ => "日",
        },
        RepeatKind.Monthly => $"每月{DayOfMonth}日",
        _ => "",
    };

    public string TriggerText =>
        NotBefore.HasValue ? $"不早于 {NotBefore.Value:HH\\:mm}" : "接上一项";

    public FetchPlanItem Clone() => (FetchPlanItem)MemberwiseClone();
}

/// <summary>
/// 一份计划 = 一串有序的任务项（2026-08-31 新增）。
///
/// **只有一份计划**，不做"日常/财报季"多套切换：靠每项的 <see cref="FetchPlanItem.Enabled"/>
/// 和重复规则就能表达同样的意思（八月底把财务和监管指标勾上，跑完再取消勾选），
/// 多一套计划就多一层要记住"现在用的是哪套"的负担。
/// </summary>
public sealed class FetchPlan
{
    /// <summary>顺序即执行顺序。</summary>
    public List<FetchPlanItem> Items { get; set; } = [];

    /// <summary>程序启动后自动开始执行计划（无人值守用）。</summary>
    public bool AutoStartOnLaunch { get; set; }

    /// <summary>
    /// 第一次用（没有 fetch-plan.json）时给的默认计划。
    ///
    /// **表里一次列全 14 个动作**，用"启用"勾选决定跑不跑——不再分"可选清单"和"我的计划"两张表
    /// （2026-08-31 按用户意见改）：勾选本身就表达了"要不要"，两张表是多余的一层搬来搬去。
    /// 默认只勾日常那三项（收盘后拉全部 → 补失败 → 拉板块）；定期数据按季度/月排好但不勾，
    /// 财报季要跑时勾上、跑完取消；按需和一次性的排在最后、重复规则给"手动"。
    /// </summary>
    public static FetchPlan CreateDefault()
    {
        FetchPlanItem Daily(FetchActionId a, bool on, TimeOnly? at = null) => new()
        {
            Action = a, Enabled = on, NotBefore = at, Repeat = RepeatKind.EveryWorkday,
        };
        FetchPlanItem Monthly(FetchActionId a) => new()
        {
            Action = a, Enabled = false, Repeat = RepeatKind.Monthly, DayOfMonth = 1,
        };
        FetchPlanItem OnDemand(FetchActionId a) => new()
        {
            Action = a, Enabled = false, Repeat = RepeatKind.Manual,
        };

        return new FetchPlan
        {
            Items =
            [
                // ── 日常：收盘后这一串 ──
                Daily(FetchActionId.FetchAll, true, new TimeOnly(18, 0)),
                Daily(FetchActionId.RetryFailed, true, new TimeOnly(21, 0)),
                Daily(FetchActionId.FetchBoards, true),
                // 除权后要重取前复权的票，空闲时慢慢补（跟财务报表一个路子）
                new FetchPlanItem { Action = FetchActionId.RepairQfq, Enabled = true, Repeat = RepeatKind.WhenIdle },
                // 回测专用价格序列的两步：先补不复权原始价，再本地算乘法式复权（收益率才准）。
                // 不复权的**日常增量已经并进拉取全部/当天**了，这一项只管首次回补十年历史——
                // 所以默认「手动」：补完一次就不用再跑，之后看参数格的待办量是不是 0 就行。
                new FetchPlanItem { Action = FetchActionId.FetchRawBars, Enabled = false, Repeat = RepeatKind.Manual },
                new FetchPlanItem { Action = FetchActionId.RebuildAdjSeries, Enabled = true, Repeat = RepeatKind.WhenIdle },
                // 预约披露日：每天都得看一眼，因为它会改（12% 改过，提前的还比延后的多）
                new FetchPlanItem { Action = FetchActionId.FetchEarningsSchedule, Enabled = true, Repeat = RepeatKind.EveryWorkday },

                // ── 定期：财报季勾上，跑完取消 ──
                Monthly(FetchActionId.FetchIndustry),
                Monthly(FetchActionId.FetchIndexCons),
                Monthly(FetchActionId.FetchShareholder),
                // 财务报表天生适合空闲时补：接口配额严、每轮只能 300 只、全市场要跨几天，
                // 排成"每月某天跑一轮"根本补不完（见 FetchTaskCatalog 里它的说明）
                new FetchPlanItem { Action = FetchActionId.FetchFinancials, Enabled = true, Repeat = RepeatKind.WhenIdle },
                Monthly(FetchActionId.FetchDividend),
                Monthly(FetchActionId.BankRegulatory),
                OnDemand(FetchActionId.ImportManual),

                // ── 按需 / 一次性 ──
                OnDemand(FetchActionId.FetchDay),
                OnDemand(FetchActionId.BackfillDaily),
                OnDemand(FetchActionId.FetchYear),
                OnDemand(FetchActionId.OptimizeDatabase),
            ],
        };
    }

    /// <summary>
    /// 加载之后的规整，两件事：
    ///
    /// ① **补齐动作全集**——目录里有、这份计划里没有的动作补到表尾（默认不启用、手动）。
    ///    读到旧版本存的计划（那时表里只有 8 项）、或者以后代码里新增了动作时都靠它；
    ///    既然表就是全集，缺一行就等于那个动作在界面上凭空消失了。
    ///    已有的行**保持原顺序和原设置**。
    ///
    /// ② **"手动"的项一律取消启用**——这两个状态是互斥的（2026-08-31 按用户意见）：
    ///    "手动"的意思就是"别自作主张替我跑"，队列本来就会跳过它（见 <see cref="IsDueOn"/>），
    ///    这时还勾着"启用"只会让人以为它会跑。界面上也把这类行的启用框灰掉。
    /// </summary>
    public void Normalize()
    {
        var have = Items.Select(i => i.Action).ToHashSet();
        foreach (var info in FetchTaskCatalog.All)
            if (!have.Contains(info.Id))
                Items.Add(new FetchPlanItem { Action = info.Id, Enabled = false, Repeat = RepeatKind.Manual });

        foreach (var i in Items)
            if (i.Repeat == RepeatKind.Manual) i.Enabled = false;
    }

    /// <summary>把 HH:mm 文本解析成 <see cref="FetchPlanItem.NotBefore"/>；空文本=接上一项。</summary>
    public static bool TryParseTime(string? text, out TimeOnly? value)
    {
        value = null;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return true;
        if (!TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return false;
        value = t;
        return true;
    }
}
