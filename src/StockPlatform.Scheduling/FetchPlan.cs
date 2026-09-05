using System.Globalization;
using System.Text.Json.Serialization;

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
}

/// <summary>
/// 到期之后**怎么跑**（2026-09-02 新增）——跟"多久到期一次"（<see cref="RepeatKind"/>）是两件事。
///
/// 原来「空闲时」挤在 RepeatKind 里当一种"频率"，于是表达不了"每月 1 号到期、然后空闲时慢慢补"
/// 这种最常见的组合（财务报表正是如此：季报出来后要补，可接口每轮只能 300 只、全市场跨几天才啃得完）。
/// 拆成两维之后，"什么时候该做"和"急不急"各说各的，也就不用再拿执行方式去给任务分组了。
/// </summary>
public enum RunPacing
{
    /// <summary>
    /// 到点就跑，排进队列。**每日数据必须是这个**：当天的行情、资金、龙虎榜有时效，
    /// 等空闲就可能等不到（程序一直有活干的话就一直不跑）。
    /// </summary>
    Immediate,

    /// <summary>
    /// 到期之后，**程序空着的时候**慢慢补，补完这一期为止。
    ///
    /// 适用判据只有一条：**晚几天也没关系**。周期基本面（财务/股东/分红/行业/成分）都是这种。
    /// 什么时候算空着：没有别的到点任务，或者正在等下一个定时任务、中间那段空窗；
    /// 手动点了任何按钮时一律让路；能分批的（财务报表每轮 300 只）就剩多少做多少，
    /// 到点前干净收尾，不跟定时任务撞上。
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
    /// 真正参与执行的开关＝**自己勾了 且 所在的组也勾了**（2026-09-02）。
    /// 组的勾选是总闸：整组不跑的时候，组里勾着的项也不该跑。
    /// </summary>
    [JsonIgnore]
    public bool EffectiveEnabled => Enabled && Owner is { Enabled: true };

    /// <summary>
    /// **老字段**：分组之前每一项自己的「不早于」（2026-09-02 起时刻只在组上）。
    ///
    /// 为什么取消：组内是**串行**的——龙虎榜排在 13 项后面，跑到它时早过 21 点了，
    /// 给它单独设一个"不早于 20:00"根本不起作用；而真正需要"更晚才开始"的，
    /// 本质就是另一个触发时机，那正是**另一个组**该干的事。两处都能设时刻＝时间有两个真相，
    /// 界面上也得每行再摆一个框，正是分组要消掉的那种噪音。
    ///
    /// 只在读老 json 时用得着：迁移时比组还晚的那种会被单独拆成一个组（见
    /// <see cref="FetchPlan.MigrateToGroups"/>），其余一律清空。新格式不写它。
    /// </summary>
    public TimeOnly? NotBefore { get; set; }

    /// <summary>
    /// 这一项属于哪个组（2026-09-02）。**不进 json**——组里已经装着它，再存一遍就是两个真相；
    /// 加载后由 <see cref="FetchPlan.LinkOwners"/> 统一回填。
    ///
    /// 重复规则、星期几、每月几号这些"什么时候跑"的设置全在组上，子项通过它读取
    /// （<see cref="Repeat"/> 等）——这正是分组的意义：13 行日常任务不必各自摆一份一模一样的规则。
    /// </summary>
    [JsonIgnore]
    public FetchPlanGroup? Owner { get; set; }

    /// <summary>重复规则来自所属的组。没有组（理论上不该发生）时按「手动」处理，绝不自作主张地跑。</summary>
    [JsonIgnore]
    public RepeatKind Repeat => Owner?.Repeat ?? RepeatKind.Manual;

    /// <summary>到期之后怎么跑——也来自组（见 <see cref="RunPacing"/>）。</summary>
    [JsonIgnore]
    public RunPacing Pacing => Owner?.Pacing ?? RunPacing.Immediate;

    /// <summary><see cref="RepeatKind.Weekly"/> 时用，来自组。</summary>
    [JsonIgnore]
    public DayOfWeek Weekday => Owner?.Weekday ?? DayOfWeek.Monday;

    /// <summary><see cref="RepeatKind.Monthly"/> 时用（1~31），来自组。</summary>
    [JsonIgnore]
    public int DayOfMonth => Owner?.DayOfMonth ?? 1;

    // ── 老格式（2026-09-02 之前：平铺、每项自带重复规则）的字段 ──
    //
    // ⚠ 类型是 **string** 不是 RepeatKind：老 json 里有 "Repeat": "WhenIdle"，而那个枚举成员
    //   已经删了（拆成了 Repeat + Pacing 两维）。按枚举反序列化会直接抛异常，整份文件读不进来——
    //   连带**老的运行记录也拿不到**，而那些记录是要承接的（上次跑没跑、耗时样本）。
    //   用 string 接住就只是个认不出来的值，不影响其余字段。
    //   新格式不写它们（null 时被 DefaultIgnoreCondition.WhenWritingNull 略过）。

    [JsonPropertyName("Repeat")] public string? LegacyRepeat { get; set; }
    [JsonPropertyName("Weekday")] public string? LegacyWeekday { get; set; }
    [JsonPropertyName("DayOfMonth")] public int? LegacyDayOfMonth { get; set; }

    /// <summary>
    /// 抓哪一段（2026-09-02）：增量 / 只抓某一天 / 首次整段回补。**名字进 json**。
    ///
    /// 默认增量，也就是日常那个用法。界面上只列这一项支持的模式
    /// （见 <see cref="FetchActionInfo.SupportedModes"/>）；读到不支持的值时按增量处理，
    /// 不让一个手改坏的 json 把整份计划卡住。
    /// </summary>
    public FetchMode Mode { get; set; } = FetchMode.Incremental;

    /// <summary>这一项当前该用的模式——json 里存了它不支持的值时退回增量。</summary>
    public FetchMode EffectiveMode =>
        Info.SupportedModes.HasFlag(Mode) ? Mode : FetchMode.Incremental;

    /// <summary>
    /// 当前模式下能不能"只跑一部分"（给「空闲时」那类触发用）。
    ///
    /// ⚠ 要按**模式**判，不能只看目录里那个标记：【个股日K·不复权】整段回补时是 24700 个请求、
    /// 分批跑天经地义，但日常增量只有一根K线的量，没有"跑一半"这回事——两种模式共用一个动作，
    /// 判断就得跟着模式走。只支持增量的动作（财务报表、重取前复权）不受影响。
    /// </summary>
    public bool SupportsPartialRunNow =>
        Info.SupportsPartialRun
        && (Info.SupportedModes == FetchMode.Incremental || EffectiveMode != FetchMode.Incremental);

    /// <summary>动作要参数时才有值；留空表示用【手动】页上的对应输入框。</summary>
    public string? DateText { get; set; }
    /// <summary>首次回看几年（只有「拉取全部」用）。留空=用【手动】页那个框。</summary>
    public string? LookbackYearsText { get; set; }
    /// <summary>中标/订单公告关键词，逗号分隔（2026-09-02 从全局参数挪进行里）。留空=用【手动】页那个框。</summary>
    public string? KeywordsText { get; set; }
    public string? YearStartText { get; set; }
    public string? YearEndText { get; set; }
    public bool OverwriteQfq { get; set; }

    /// <summary>
    /// 【全库数据体检】的「彻底体检」开关（2026-09-02）：勾上就清空"确认数据源没有"的白名单、
    /// 全部重查一遍。默认不勾——那份白名单正是让体检能收敛的东西，天天清等于每次都把停牌
    /// 全市场重报一遍。
    /// </summary>
    public bool ThoroughAudit { get; set; }

    // ── 运行记录（引擎回写，界面只读）──
    public DateTime? LastStart { get; set; }
    public DateTime? LastEnd { get; set; }
    public RunOutcome LastOutcome { get; set; } = RunOutcome.None;
    public int LastErrorCount { get; set; }
    public string? LastMessage { get; set; }

    /// <summary>
    /// 最近几轮**实测**耗时（秒），新的追加在后面，只留 <see cref="MaxDurationSamples"/> 条
    /// （2026-09-02 新增）。
    ///
    /// 为什么要它：目录里那个 <see cref="FetchActionInfo.Estimate"/> 是手填的，【拉取全部】拆成
    /// 13 项之后不可能填准（项目里根本没有分项计时数据）。而时间轴、"空闲窗口装不装得下"两处
    /// 判断都吃这个值。改成"跑过就用自己的实测中位数"，第二轮起就准了，也自动跟着机器和网络变。
    ///
    /// 只记**真干了活**的轮次：NothingToDo（没什么可做，秒回）不记，否则会把中位数拉到接近 0，
    /// 让空闲调度误以为什么任务都塞得进去。
    /// </summary>
    public List<int> RecentDurationsSec { get; set; } = [];

    /// <summary>留几条样本。取中位数用，5 条够抹掉偶发的"网络特别慢那一次"，又不至于记住半年前的机器。</summary>
    public const int MaxDurationSamples = 5;

    /// <summary>
    /// 排时间轴/判断空闲窗口时该用的耗时：有实测就用实测**中位数**，没有才回退到目录里的手填值。
    ///
    /// 用中位数不用平均：偶尔一轮撞上限流被拖到 3 倍时长，平均数会被它带偏，中位数不会。
    /// </summary>
    public TimeSpan EffectiveEstimate
    {
        get
        {
            if (RecentDurationsSec.Count == 0) return Info.Estimate;
            var sorted = RecentDurationsSec.OrderBy(x => x).ToList();
            int mid = sorted.Count / 2;
            double median = sorted.Count % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2.0;
            return TimeSpan.FromSeconds(Math.Max(1, median));
        }
    }

    /// <summary>记一轮实测耗时（超出上限就把最老的挤掉）。</summary>
    public void RecordDuration(TimeSpan elapsed)
    {
        RecentDurationsSec.Add((int)Math.Max(1, Math.Round(elapsed.TotalSeconds)));
        while (RecentDurationsSec.Count > MaxDurationSamples) RecentDurationsSec.RemoveAt(0);
    }

    public FetchActionInfo Info => FetchTaskCatalog.Info(Action);

    /// <summary>
    /// 把这一项**没填的参数**补上默认值（2026-09-02）。建计划项、以及 Normalize 补新动作时调。
    ///
    /// 为什么不留空：空格子看不出会用什么值，人得知道"按几年回看""按什么关键词搜"才好判断要不要改。
    /// 已经填了的一律不动——那是用户自己设的。日期故意不填，见 <see cref="FetchTaskCatalog.DefaultParamText"/>。
    /// </summary>
    public void FillDefaultParams()
    {
        if (Info.Params.HasFlag(FetchActionParams.LookbackYears) && string.IsNullOrWhiteSpace(LookbackYearsText))
            LookbackYearsText = FetchTaskCatalog.DefaultParamText(Action, FetchActionParams.LookbackYears);

        if (Info.Params.HasFlag(FetchActionParams.Keywords) && string.IsNullOrWhiteSpace(KeywordsText))
            KeywordsText = FetchTaskCatalog.DefaultParamText(Action, FetchActionParams.Keywords);

        if (Info.Params.HasFlag(FetchActionParams.YearRange))
        {
            if (string.IsNullOrWhiteSpace(YearStartText))
                YearStartText = FetchTaskCatalog.DefaultParamText(Action, FetchActionParams.YearRange);
            // 结束年留空＝一直补到今年，这是最常用的；填上今年反而每年都要改一次
            if (string.IsNullOrWhiteSpace(YearEndText))
                YearEndText = DateTime.Today.Year.ToString();
        }
    }

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
    public bool IsDueOn(DateTime day) => Owner?.IsDueOn(day) ?? false;

    /// <summary>
    /// <paramref name="day"/> 所属周期的**应跑日**（来自组）：每周=本周的那个星期几，
    /// 每月=本月的那一号（31 号设在只有 30 天的月份里落到月末），其余=当天。
    /// </summary>
    public DateTime PeriodStartOn(DateTime day) => Owner?.PeriodStartOn(day) ?? day.Date;

    /// <summary>
    /// 今天已经**成功**跑完了吗——用来支持"关了程序再打开接着跑"，不会把已经做完的重来一遍。
    ///
    /// ⚠ 只认 <see cref="RunOutcome.Ok"/>。被跳过（前置失败）的**不算完成**：前置后来要是补上了
    /// （比如你手动跑了一次财务报表），这一项当天还应该有机会跑。早先把 Skipped 也算成"已完成"，
    /// 结果是金融监管指标一旦因财务失败被跳过，那天就再也不会跑了。
    ///
    /// ⚠ **一轮只补了一部分的不算完成**（2026-09-02）：财务报表每轮上限 300 只，全市场要跨几天，
    /// 跑成功一轮不等于这一期做完了。判据是上一轮有没有报"没什么可做"（<see cref="LastNothingToDo"/>）——
    /// 有活干就说明还欠着，下一个空闲窗口接着补；真的补齐了才算这一期完成。
    ///
    /// ⚠⚠ 这条"还欠着"**只对「空闲时补」成立**，而且要看**当前模式**下真的能分批
    /// （<see cref="SupportsPartialRunNow"/>，不是目录里那个静态标记）——2026-09-02 踩过的坑：
    /// 判据写成 <c>!Info.SupportsPartialRun</c> 之后，【个股日K·不复权】这类"目录里标着能分批、
    /// 但当前是增量模式"的项每轮跑完都判成"没做完"，而它在日更组是**到点就跑**、没有空闲冷却挡着，
    /// 于是主循环立刻又把它挑出来——**4 秒一轮无限重跑**（实测日志里刷了几百轮）。
    /// 「空闲时补」不会这样：它每轮跑完都进冷却（PlanRunner._idleNextAllowed，20 分钟）。
    ///
    /// ⚠ 判的是**当前周期**跑没跑过，不是"今天"：月度项在本月应跑日之后跑过一次就够了，
    /// 本月剩下的日子不再重复；到了下个月，<see cref="DueTimeOn"/> 指向新一期，自然又待办。
    /// </summary>
    public bool AlreadyRanOn(DateTime now) =>
        LastOutcome == RunOutcome.Ok
        && (LastNothingToDo || Pacing != RunPacing.WhenIdle || !SupportsPartialRunNow)
        && StartedAfterAnchor(now);

    /// <summary>
    /// 上一轮跑完时报的"已经齐了、没什么可做"（2026-09-02）。
    /// 分批补的任务靠它判断这一期到底做完没有，见 <see cref="AlreadyRanOn"/>。
    /// </summary>
    public bool LastNothingToDo { get; set; }

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
    /// 这一项在 <paramref name="day"/> 所属的周期里，最早什么时候可以开跑——**完全由组决定**。
    /// 每周/每月算的是**当期应跑日**那天的时刻，所以补跑时（比如 2 号才轮到月度组）
    /// 它返回的是已经过去的时刻，调用方一看就知道"早该跑了，现在立刻跑"。
    /// </summary>
    public DateTime DueTimeOn(DateTime day) => Owner?.DueTimeOn(day) ?? day.Date;

    /// <summary>当前这一轮是什么时候开工的（<see cref="FetchPlanGroup.DueAnchorAt"/>）。</summary>
    public DateTime? DueAnchorAt(DateTime now) => Owner?.DueAnchorAt(now);

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
    private bool StartedAfterAnchor(DateTime now)
    {
        if (LastEnd is not { } end) return false;
        // 这一期还没轮到（手动项，或周末问一个从没到过点的组）——那就谈不上"这一轮跑过了"
        if (DueAnchorAt(now) is not { } anchor) return false;
        return (LastStart ?? end) >= anchor;
    }

    /// <summary>重复规则的中文说法——来自组（子项不再各自有重复规则）。</summary>
    public string RepeatText => Owner?.RepeatText ?? "手动";

    public string TriggerText =>
        NotBefore.HasValue ? $"不早于 {NotBefore.Value:HH\\:mm}" : "接上一项";

    public FetchPlanItem Clone() => (FetchPlanItem)MemberwiseClone();
}

/// <summary>
/// 一组共用触发时机的任务（2026-09-02 新增）。
///
/// ════ 组是「排期单位」，不是「执行单位」════
/// 这一条是底线，改这个文件的人必须先读懂：组只表达"这几项共用一个开始时刻和重复规则"，
/// **它绝不是一次不可干预的调用**。所以：
///   · 「这一期跑过没有 / 今天失败过没有」永远按**子项**记录（运行记录也全在子项上）——
///     一项失败不拖累同组其它项，明天也只重试它；
///   · 空闲调度挑的是**子项**——两小时的组塞不进空窗，但组里那个 3 分钟的项塞得进；
///   · 耗时自学、时间轴也都按子项，组头只做求和。
/// 早先被退役的【拉取全部】那种复合动作就是"执行单位"，拆掉它正是为了拿到上面这些好处；
/// 谁要是把判定改成组粒度，等于把拆分白拆了。
///
/// ════ 为什么要有组 ════
/// 拆成 26 个原子项之后，日常那 13 行的重复规则全是"每工作日"、只有第一行填了时刻，
/// 26 行里有 20 多个格子在重复同一件事，还看不出"这些是收盘后那一串"。
/// 把"什么时候跑"上提到组，子项就只剩"做什么 + 参数 + 顺序"。
/// </summary>
public sealed class FetchPlanGroup
{
    /// <summary>组名，用户可改。只用于显示和日志，不参与任何判定。</summary>
    public string Name { get; set; } = "";

    /// <summary>整组开关：没勾则整组不跑，哪怕子项各自勾着。</summary>
    public bool Enabled { get; set; } = true;

    public RepeatKind Repeat { get; set; } = RepeatKind.EveryWorkday;

    /// <summary>
    /// 到期之后怎么跑：到点就跑，还是空闲时慢慢补（2026-09-02 新增，见 <see cref="RunPacing"/>）。
    /// 跟 <see cref="Repeat"/> 正交——"多久到期一次"和"急不急"是两件事。
    /// </summary>
    public RunPacing Pacing { get; set; } = RunPacing.Immediate;

    /// <summary><see cref="RepeatKind.Weekly"/> 时用。</summary>
    public DayOfWeek Weekday { get; set; } = DayOfWeek.Monday;

    /// <summary><see cref="RepeatKind.Monthly"/> 时用，1~31（该日超出当月天数时按月末算）。</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>这一组不早于几点开始；null＝接着上一组跑。语义同子项：是"不早于"，不是"准时"。</summary>
    public TimeOnly? NotBefore { get; set; }

    /// <summary>界面上折没折叠。存进 json 只是为了下次打开保持原样，跟执行无关。</summary>
    public bool Collapsed { get; set; }

    /// <summary>组内任务，顺序即组内执行顺序。</summary>
    public List<FetchPlanItem> Items { get; set; } = [];

    /// <summary>这一天该不该跑（只看重复规则和日历，不看有没有跑过）。语义同原来的子项版本。</summary>
    public bool IsDueOn(DateTime day) => Repeat switch
    {
        RepeatKind.Manual => false,
        RepeatKind.Once => true,
        RepeatKind.EveryWorkday => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RepeatKind.Weekly or RepeatKind.Monthly => day.Date >= PeriodStartOn(day),
        _ => false,
    };

    /// <summary>
    /// A股收盘时刻（2026-09-04 新增）——「每工作日」的任务在这之后必须再跑一轮，
    /// 因为收盘前跑到的当日数据是不完整的（K线未定盘、龙虎榜/资金流盘后才发布）。
    /// 见 <see cref="DueAnchorAt"/> 里 EveryWorkday 那一段。
    ///
    /// 15:00 是真实收盘，这里用 17:00：盘后数据（龙虎榜、融资余额、资金流）要到傍晚才齐，
    /// 15:05 就跑等于白跑一趟。
    /// </summary>
    public static readonly TimeOnly MarketClose = new(17, 0);

    private static TimeSpan MarketCloseOffset => MarketClose.ToTimeSpan();

    /// <summary>
    /// 「当期锚点」：**最近一个已经过去的到点时刻**，也就是"当前这一轮是什么时候开工的"。
    /// 返回 null 表示这一期还没轮到（手动项永远是 null）。
    ///
    /// 为什么要有它（2026-09-03 用户发现的漏跑）：原来判"该不该跑"用的是
    /// <see cref="DueTimeOn"/>——**今天**的到点时刻。9/2 18:00 那一轮跑到次日 01:24，
    /// 剩下七项（融资余额、龙虎榜、板块、公告…）再去问"该跑了吗"，得到的答案是
    /// "今天的到点是 9/3 18:00，还没到"——于是那七项被推迟了整整一天，9/2 的数据谁也没抓。
    /// 日志里一点异常都没有，界面上还显示着"18:00 → 19:35 待执行"。
    ///
    /// 锚点把"轮次"和"自然日"解耦：9/3 01:24 问锚点，答案是 9/2 18:00——那一轮还在进行中，
    /// 没跑的接着跑；到 9/3 18:00 锚点前移，所有项自然进入新一轮。周末同理：
    /// 周五 18:00 开的工跑到周六，锚点仍是周五 18:00，不会因为"周六不是工作日"就停摆。
    /// </summary>
    public DateTime? DueAnchorAt(DateTime now)
    {
        var offset = NotBefore?.ToTimeSpan() ?? TimeSpan.Zero;
        switch (Repeat)
        {
            // 手动项和「仅一次」都没有周期可言，锚点给一个"永远在过去"的时刻：
            // 它们不靠锚点排期（手动项在 PlanRunner.IsPending 一进门就被挡掉，仅一次的跑成功
            // 会自动取消勾选），锚点对它们只有一个用处——让"上次跑的结果"在界面上显示得出来。
            case RepeatKind.Manual:
            case RepeatKind.Once:
                return DateTime.MinValue;

            case RepeatKind.EveryWorkday:
            {
                // 每个工作日有**两个**到点（2026-09-04 按用户要求）：设定的时刻，加上收盘。
                //
                // 为什么必须补收盘这一个：收盘前跑到的当日数据是不完整的（当天K线还没定盘、
                // 龙虎榜和资金流盘后才出），可原来只要跑过一轮就算"今天做完了"——早上开机
                // 自动跑一遍，收盘后就再也不跑了，当天数据永远停在盘中那个残缺状态。
                // 用户 17:04 看到的「共 32 项、已完成 30 项」就是这么来的：那 30 项绝大多数
                // 是早上跑的，按收盘口径它们今天都还得再跑一遍。
                //
                // 补成"两个到点"而不是"把设定时刻推到 17:00"，是因为白天那一轮照样有用
                // （盘中要看行情、要补历史），要的只是**收盘后再来一遍**。
                // 两个到点也天然不会来回重跑：白天跑完，锚点还是早上那个，AlreadyRanOn 为真；
                // 过了 17:00 锚点前移到收盘，上一轮的开始时间落在它之前，于是自然进入新一轮。
                //
                // 设定时刻本来就在收盘后（比如「不早于 18:00」）时不补——那一轮已经满足要求了，
                // 再插一个 17:00 只会让它每天多跑一趟。
                var offsets = new List<TimeSpan> { offset };
                if (offset < MarketCloseOffset) offsets.Add(MarketCloseOffset);

                for (int back = 0; back <= 7; back++)
                {
                    var d = now.Date.AddDays(-back);
                    if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

                    // 同一天里取**最靠后的那个已过去的到点**：17:05 时该对齐到收盘那一轮，
                    // 而不是早上那一轮，否则收盘后这次重跑判不出来。
                    var passed = offsets.Select(o => d + o).Where(t => t <= now)
                                        .OrderByDescending(t => t).ToList();
                    if (passed.Count == 0) continue;

                    // 今天已经到过点 = 当前就在这一轮里；更早的日子只有"上一轮还没跑完"才算数
                    return back == 0 ? passed[0] : (StartedSince(passed[0]) ? passed[0] : null);
                }
                return null;
            }

            case RepeatKind.Weekly:
            case RepeatKind.Monthly:
            {
                var thisPeriod = PeriodStartOn(now) + offset;
                if (thisPeriod <= now) return thisPeriod;

                var back1 = Repeat == RepeatKind.Weekly ? now.Date.AddDays(-7) : now.Date.AddMonths(-1);
                var prev = PeriodStartOn(back1) + offset;
                return prev <= now && StartedSince(prev) ? prev : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// 这一组里**有没有哪一项在 <paramref name="since"/> 之后开过工**——用来判断"上一轮还在进行中"。
    ///
    /// 这是回溯锚点的闸门，少了它会把「不早于」架空：一份全新的计划，日更组设了 18:00，
    /// 上午十点问"该跑了吗"，回溯到昨天 18:00 一看"这项没跑过"，于是十点就开抓——
    /// 而用户设 18:00 正是因为收盘前拿不到当天数据。有了这个闸门，昨天那轮压根没开工的话
    /// 就不算数，老老实实等今天 18:00；只有**确实开了工、跑到一半没跑完**（比如 18:00 开工
    /// 跑过午夜，剩下七项还欠着）才认，那些掉队的项才接着跑。
    /// </summary>
    private bool StartedSince(DateTime since)
        => Items.Any(i => (i.LastStart ?? i.LastEnd) is { } t && t >= since);

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

    /// <summary>这一组在 <paramref name="day"/> 所属周期里最早什么时候可以开跑。</summary>
    public DateTime DueTimeOn(DateTime day)
    {
        var d = PeriodStartOn(day);
        return NotBefore.HasValue ? d.Add(NotBefore.Value.ToTimeSpan()) : d;
    }

    /// <summary>"每工作日"这类频率说法；到期后怎么跑另看 <see cref="PacingText"/>。</summary>
    public string RepeatText => Repeat switch
    {
        RepeatKind.Manual => "手动",
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

    public string TriggerText => NotBefore.HasValue ? $"不早于 {NotBefore.Value:HH\\:mm}" : "接上一组";

    public string PacingText => Pacing == RunPacing.WhenIdle ? "空闲时补" : "到点就跑";
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
    /// <summary>
    /// 计划＝一串**组**（2026-09-02），组内又是一串任务。组顺序即组间执行顺序。
    ///
    /// 组只管"什么时候跑"（重复规则 + 不早于），任务管"做什么"（模式、参数、组内顺序）。
    /// 判定谁跑过、谁失败了永远按**任务**来，见 <see cref="FetchPlanGroup"/> 的类注释。
    /// </summary>
    public List<FetchPlanGroup> Groups { get; set; } = [];

    /// <summary>
    /// 老格式（2026-09-02 之前：一串平铺的任务、每项自带重复规则）。**只用于读一次做迁移**，
    /// 见 <see cref="MigrateToGroups"/>；迁移完就是 null，不再写回文件。
    /// </summary>
    [JsonPropertyName("Items")]
    public List<FetchPlanItem>? LegacyItems { get; set; }

    /// <summary>所有任务，按组顺序 + 组内顺序摊平——遍历、统计、查重都用它。</summary>
    [JsonIgnore]
    public IEnumerable<FetchPlanItem> AllItems => Groups.SelectMany(g => g.Items);

    /// <summary>程序启动后自动开始执行计划（无人值守用）。</summary>
    public bool AutoStartOnLaunch { get; set; }

    /// <summary>
    /// 这次加载时做过的迁移说明（<see cref="MigrateRetired"/> 的产物），给调用方打日志用。
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>：它是本次加载的临时信息，不该写回文件。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> MigrationNotes { get; set; } = [];

    /// <summary>
    /// 第一次用（没有 fetch-plan.json）时给的默认计划。
    ///
    /// **表里一次列全所有动作**，用"启用"勾选决定跑不跑——不再分"可选清单"和"我的计划"两张表
    /// （2026-08-31 按用户意见改）：勾选本身就表达了"要不要"，两张表是多余的一层搬来搬去。
    /// 没写进这里的动作由 <see cref="Normalize"/> 补到表尾（未启用 + 手动）。
    ///
    /// 2026-09-02 起，日常那一串默认是**拆开的 13 个原子项**（见 <see cref="FetchTaskCatalog.FetchAllSteps"/>）
    /// 而不是复合的【拉取全部】；定期数据按月排好但不勾，财报季要跑时勾上、跑完取消；
    /// 按需和一次性的排在最后、重复规则给"手动"。
    /// </summary>
    public static FetchPlan CreateDefault()
    {
        FetchPlanItem It(FetchActionId a, bool on, TimeOnly? at = null) =>
            new() { Action = a, Enabled = on, NotBefore = at };

        // 日更那一串的顺序集中定义在 FetchTaskCatalog.DailyOrder（按数据性质分四族：
        // K线 → 资金交易 → 收尾与合成 → 补漏与重算），那边也写着哪四条是硬约束。
        var daily = FetchTaskCatalog.DailyOrder.Select(a => It(a, true)).ToList();

        var plan = new FetchPlan
        {
            Groups =
            [
                // ── 组 1：每天产生的新数据，当天就得更新 ──
                // 顺序上有三处讲究，别随手挪：
                //   · 【板块指数合成】要用当天的成分股 + 当天个股K线 → 排在它俩后面；
                //   · 【当日覆盖率体检】要在个股K线抓完之后才查得出今天漏了谁；
                //   · 【重新拉取失败】【重取前复权】排最后——它们消费的名单正是前面那些步骤产生的。
                new FetchPlanGroup
                {
                    Name = "每日收盘后", Enabled = true, Repeat = RepeatKind.EveryWorkday,
                    Pacing = RunPacing.Immediate, NotBefore = new TimeOnly(18, 0),
                    Items = daily,
                },

                // ── 组 2：按周期更新、晚几天没关系的 ──
                // 整组「空闲时补」：财务报表接口配额严、每轮只能 300 只，全市场要跨几天才啃得完，
                // 设成"每月 1 号跑一轮"根本补不完；而这类数据本来也没有时效压力。
                new FetchPlanGroup
                {
                    Name = "季度定期", Enabled = true, Repeat = RepeatKind.Monthly, DayOfMonth = 1,
                    Pacing = RunPacing.WhenIdle, Collapsed = true,
                    // 顺序集中在 FetchTaskCatalog.PeriodicOrder（那边写着两条依赖：
                    // 监管指标要在财务报表之后、导入手工数据要在监管指标之后）
                    Items = [.. FetchTaskCatalog.PeriodicOrder.Select(a => It(a, true))],
                },

                // ── 组 3：按需启动 ── 想起来才做的一次性活：往回补更早的历史、全库体检、建索引。
                // 三项都不勾、点各自的【执行】：它们都是重活（全库扫描、几小时的回补、建索引锁写入），
                // 自己跑起来会把某天的日更整个挤掉。
                new FetchPlanGroup
                {
                    Name = "按需启动", Enabled = false, Repeat = RepeatKind.Manual, Collapsed = true,
                    Items = [.. FetchTaskCatalog.OnDemandOrder.Select(a => It(a, false))],
                },
            ],
        };
        plan.Normalize();   // 剩下的动作（区间回补、优化数据库）由它补进第 3 组
        return plan;
    }

    /// <summary>
    /// 把另一份计划里的**运行记录**按动作搬过来（2026-09-02）。
    ///
    /// 用在"老计划不迁移、直接重建"那条路上：计划的**结构**（谁在哪个组、几点跑）按新的来，
    /// 但"上次什么时候跑的、成没成、跑了多久"是**事实**，不该跟着结构一起丢：
    ///   · 丢了"今天跑过没有" → 当天已经跑完的会被再跑一遍（幂等，但白花两小时）；
    ///   · 丢了耗时样本 → 时间轴退回手填估值，得重新学几轮。
    ///
    /// ⚠ 各类**失败名单**不在这儿——它们在 manifest.json 里（K线/市值/资金流/成分/权重/股东/分红
    /// 各自一份，外加待重取前复权、全库体检的空洞名单），重建计划根本不碰那个文件，天然就是承接的。
    /// </summary>
    public void CarryOverRunRecords(IEnumerable<FetchPlanItem> from)
    {
        var byAction = new Dictionary<FetchActionId, FetchPlanItem>();
        foreach (var old in from) byAction[old.Action] = old;   // 同一动作重复出现时以最后一条为准

        foreach (var item in AllItems)
        {
            if (!byAction.TryGetValue(item.Action, out var old)) continue;
            item.LastStart = old.LastStart;
            item.LastEnd = old.LastEnd;
            item.LastOutcome = old.LastOutcome;
            item.LastErrorCount = old.LastErrorCount;
            item.LastMessage = old.LastMessage;
            item.LastNothingToDo = old.LastNothingToDo;
            item.RecentDurationsSec = [.. old.RecentDurationsSec];
        }
    }

    /// <summary>把每个任务的 <see cref="FetchPlanItem.Owner"/> 指回它所在的组。加载/改动结构之后都要调。</summary>
    public void LinkOwners()
    {
        foreach (var g in Groups)
            foreach (var i in g.Items)
                i.Owner = g;
    }

    // 2026-09-02：老格式（一串平铺的任务、每项自带重复规则）**不迁移**了——新的三组划分更合理，
    // 硬把老计划映射过来只会带进一堆别扭的组合（比如被设成「空闲时」的日更项）。
    // FetchPlanStore 读到老格式时直接备份原文件、重建默认计划，见那边的说明。

    /// <summary>
    /// 加载之后的规整，两件事：
    ///
    /// ① **五个组一定都在**（少一个就等于那类任务在界面上凭空消失了）；
    ///
    /// ② **补齐动作全集**——目录里有、计划里没有的动作补进它的默认组（不启用）。
    ///    读到旧版本存的计划、或者以后代码里新增了动作时都靠它。已有的项保持原位和原设置。
    ///
    /// ④ **参数格补默认值**（见 <see cref="FetchPlanItem.FillDefaultParams"/>）。
    ///
    /// ③ **「手动」组里的项一律取消启用**——这两个状态是互斥的（2026-08-31 按用户意见）：
    ///    "手动"的意思就是"别自作主张替我跑"，队列本来就会跳过它（见 <see cref="FetchPlanGroup.IsDueOn"/>），
    ///    这时还勾着"启用"只会让人以为它会跑。
    ///
    /// ④ 回填每个任务的 <see cref="FetchPlanItem.Owner"/>。
    /// </summary>
    public void Normalize()
    {
        // ① 三个组按固定顺序补齐（用户改过名字/规则的保持原样，只认位置）
        var defaults = CreateDefaultGroupsShell();
        for (int i = Groups.Count; i < defaults.Count; i++) Groups.Add(defaults[i]);

        // ② 缺的动作补进默认组
        var have = AllItems.Select(i => i.Action).ToHashSet();
        foreach (var info in FetchTaskCatalog.Active)
        {
            if (have.Contains(info.Id)) continue;
            var kind = FetchTaskCatalog.DefaultGroupOf(info.Id);
            // 新加的项一律不启用：它没经过用户同意，不该自己跑起来
            GroupOf(kind).Items.Add(new FetchPlanItem { Action = info.Id, Enabled = false });
        }

        // ③ 「手动」组里的项不留启用状态
        foreach (var g in Groups)
            if (g.Repeat == RepeatKind.Manual)
                foreach (var i in g.Items) i.Enabled = false;

        // ④ 参数格该有默认值的补上——空格子看不出会用什么值（已填的不动）
        foreach (var i in AllItems) i.FillDefaultParams();

        LinkOwners();   // ⑤
    }

    /// <summary>按位置取组。位置＝<see cref="PlanGroupKind"/> 的序号——用户能改组名，改不了它是第几组。</summary>
    public FetchPlanGroup GroupOf(PlanGroupKind kind) =>
        Groups[Math.Min((int)kind, Groups.Count - 1)];

    /// <summary>只要三个空组（名字/规则用默认值）——给 Normalize 补位用，避免跟 CreateDefault 递归。</summary>
    private static List<FetchPlanGroup> CreateDefaultGroupsShell() =>
    [
        new() { Name = "每日收盘后", Enabled = true, Repeat = RepeatKind.EveryWorkday,
                Pacing = RunPacing.Immediate, NotBefore = new TimeOnly(18, 0) },
        new() { Name = "季度定期", Enabled = true, Repeat = RepeatKind.Monthly, DayOfMonth = 1,
                Pacing = RunPacing.WhenIdle, Collapsed = true },
        new() { Name = "按需启动", Enabled = false, Repeat = RepeatKind.Manual, Collapsed = true },
    ];

    /// <summary>
    /// 把已退役的复合动作原地换成等价的原子项（2026-09-02），返回给人看的说明（调用方打日志）。
    ///
    /// 为什么要**自动换**而不是只提示：退役之后计划页上不再有这些动作，那一行留在 json 里就是个
    /// 认得出、却排不动也删不掉的幽灵。而替换是安全的——展开出来的项跑完，库里的东西跟原来一模一样
    /// （见 <see cref="FetchTaskCatalog.RetiredInto"/> 的"等价"标准）。
    ///
    /// 替换规则：
    ///   · 原来那行**没启用** → 直接丢掉（Normalize 会保证等价项在表里，用户想排自己勾）；
    ///   · 原来那行启用着 → 展开成一串启用的项，**排在它原来的位置**（顺序即执行顺序，不能挪到末尾），
    ///     重复规则照搬，「不早于」只给第一项（后面几项接着跑），日期按需要搬过去；
    ///   · 展开目标要是已经在表里了（用户自己排过），就不动它，只把原来那行删掉——
    ///     绝不覆盖用户已有的设置。
    /// </summary>
    public List<string> MigrateRetired()
    {
        var notes = new List<string>();

        foreach (var group in Groups)
        {
            for (int idx = 0; idx < group.Items.Count; idx++)
            {
                var old = group.Items[idx];
                if (!FetchTaskCatalog.RetiredInto.TryGetValue(old.Action, out var expansions)) continue;

                var oldName = FetchTaskCatalog.Info(old.Action).Name;
                group.Items.RemoveAt(idx);

                if (!old.Enabled)
                {
                    idx--;                       // 这一格已经换人了，回退一位重新看
                    continue;                    // 没启用的直接丢，不留痕
                }

                var inserted = new List<FetchPlanItem>();
                var skipped = new List<string>();
                foreach (var e in expansions)
                {
                    // 等价项可能已经在**别的组**里了（用户自己排过），那就不动它
                    var existing = AllItems.FirstOrDefault(i => i.Action == e.Into);
                    if (existing != null && existing.Enabled)
                    {
                        skipped.Add(FetchTaskCatalog.Info(e.Into).Name);
                        continue;
                    }
                    if (existing != null) existing.Owner?.Items.Remove(existing);

                    inserted.Add(new FetchPlanItem
                    {
                        Action = e.Into,
                        Enabled = true,
                        Owner = group,
                        // 「不早于」只给第一项：后面几项接着上一项跑，否则整串会各自等到同一个时刻
                        NotBefore = inserted.Count == 0 ? old.NotBefore : null,
                        Mode = e.Mode,
                        DateText = e.CarryDate ? old.DateText : null,
                        LookbackYearsText = old.LookbackYearsText,
                    });
                }

                group.Items.InsertRange(Math.Min(idx, group.Items.Count), inserted);
                idx += inserted.Count - 1;

                notes.Add($"【{oldName}】已拆成 {inserted.Count} 项：{string.Join(" → ", inserted.Select(i => i.Info.Name))}"
                    + (skipped.Count > 0 ? $"（{string.Join("、", skipped)} 你已经自己排过了，保持原样）" : ""));
            }
        }

        LinkOwners();
        return notes;
    }

    /// <summary>
    /// 排得对不对的轻校验（2026-09-02），返回给人看的提醒。**只提示、不拦**——
    /// 用户完全可能故意那么排（比如白天先跑一遍补断档，晚上再跑一次）。
    ///
    /// 现在只查一件事：**要等收盘的项会不会在收盘前就开跑**。
    /// 任务是严格串行的，所以一项的实际开始时刻＝"它前面最近一个『不早于』"——
    /// 中间那些留空的项都是接着上一项跑。于是只要沿着顺序往下走、记住最近一次看到的时刻，
    /// 遇到 <see cref="DataReadiness.AfterClose"/> 的项时看那个时刻早不早就行。
    ///
    /// 早跑的后果不是报错，而是**静默拿到不完整的数据**（见 DataReadiness.AfterClose 的注释），
    /// 所以这个提醒有必要——不然人只会在第二天看盘时发现少了一半股票。
    /// </summary>
    public List<string> CheckSchedule()
    {
        var warnings = new List<string>();
        var flagged = new List<string>();

        foreach (var group in Groups)
        {
            if (!group.Enabled) continue;
            // 「空闲时补」的组填的是别人不用的空档，本来就没有固定时刻；「手动」压根不自动跑
            if (group.Repeat == RepeatKind.Manual || group.Pacing == RunPacing.WhenIdle) continue;

            // 组的时刻管着整组；组内某一项自己填了更晚的锚点，就以那个为准
            var groupTime = group.NotBefore;
            foreach (var item in group.Items)
            {
                if (!item.Enabled) continue;
                var start = item.NotBefore is { } own && (groupTime is not { } gt || own > gt) ? own : groupTime;
                if (item.Info.Readiness != DataReadiness.AfterClose) continue;
                if (start is { } s && s >= FetchTaskCatalog.EarliestAfterClose) continue;
                flagged.Add(item.Info.Name);
            }
        }

        if (flagged.Count > 0)
        {
            warnings.Add($"这几项要等当天收盘后才有完整数据，现在却排在 "
                + $"{FetchTaskCatalog.EarliestAfterClose:HH\\:mm} 之前（或者整串都没设开始时刻）："
                + $"{string.Join("、", flagged)}。"
                + "早跑不会报错，但会静默拿到不完整的数据（盘中价、或者数据源盘后还没更新到的那批）。"
                + "建议给这一串的**第一项**设一个「不早于 18:00」，后面的接着跑就行。");
        }
        return warnings;
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
