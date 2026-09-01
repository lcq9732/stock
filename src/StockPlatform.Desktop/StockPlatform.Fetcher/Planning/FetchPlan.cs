using System.Globalization;

namespace StockPlatform.Fetcher.Planning;

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
    public bool IsDueOn(DateTime day) => Repeat switch
    {
        RepeatKind.Manual => false,
        // 空闲项每天都"可以"跑，跑不跑由 PlanRunner 看当下空不空、离下一个定时项还有多久
        RepeatKind.WhenIdle => true,
        RepeatKind.Once => true,
        RepeatKind.EveryWorkday => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RepeatKind.Weekly => day.DayOfWeek == Weekday,
        // 31 号设在只有 30 天的月份里也要能跑到，所以超出当月天数时落到月末那天
        RepeatKind.Monthly => day.Day == Math.Min(DayOfMonth, DateTime.DaysInMonth(day.Year, day.Month)),
        _ => false,
    };

    /// <summary>
    /// 今天已经**成功**跑完了吗——用来支持"关了程序再打开接着跑"，不会把已经做完的重来一遍。
    ///
    /// ⚠ 只认 <see cref="RunOutcome.Ok"/>。被跳过（前置失败）的**不算完成**：前置后来要是补上了
    /// （比如你手动跑了一次财务报表），这一项当天还应该有机会跑。早先把 Skipped 也算成"已完成"，
    /// 结果是金融监管指标一旦因财务失败被跳过，那天就再也不会跑了。
    ///
    /// ⚠ **空闲项除外**：它一天本来就要跑很多轮（每轮补一批），跑过一轮不代表今天不用再跑。
    /// </summary>
    public bool AlreadyRanOn(DateTime day) =>
        Repeat != RepeatKind.WhenIdle
        && LastEnd.HasValue && LastEnd.Value.Date == day.Date && LastOutcome == RunOutcome.Ok;

    /// <summary>今天已经因为前置失败被跳过、并且记过一次了——别再重复记。</summary>
    public bool AlreadySkippedOn(DateTime day) =>
        LastEnd.HasValue && LastEnd.Value.Date == day.Date && LastOutcome == RunOutcome.Skipped;

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
