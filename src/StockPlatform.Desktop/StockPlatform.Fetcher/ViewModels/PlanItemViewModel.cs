using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockPlatform.Scheduling;

namespace StockPlatform.Fetcher.ViewModels;

/// <summary>重复规则的下拉选项（ComboBox 要一个带显示文本的对象，枚举本身没法直接显示中文）。</summary>
public sealed record RepeatOption(RepeatKind Kind, string Text)
{
    public static readonly IReadOnlyList<RepeatOption> All =
    [
        new(RepeatKind.EveryWorkday, "每工作日"),
        new(RepeatKind.Weekly, "每周"),
        new(RepeatKind.Monthly, "每月"),
        new(RepeatKind.Once, "仅一次"),
        new(RepeatKind.Manual, "手动"),
    ];
}

/// <summary>
/// "到期之后怎么跑"下拉里的一项（2026-09-02，见 <see cref="RunPacing"/>）。
///
/// 「空闲时」原来挤在重复规则里当一种"频率"，于是表达不了"每月 1 号到期、然后空闲时慢慢补"
/// 这种组合——而那正是财务报表需要的。拆成两个下拉之后，"多久到期"和"急不急"各说各的。
/// </summary>
public sealed record PacingOption(RunPacing Value, string Text)
{
    public static readonly IReadOnlyList<PacingOption> All =
    [
        new(RunPacing.Immediate, "到点就跑"),
        new(RunPacing.WhenIdle, "空闲时补"),
    ];
}

/// <summary>
/// "抓哪一段"下拉里的一项（2026-09-02，见 FetchMode）。
/// 每一行只列**自己支持的**模式——不支持是有具体原因的（快照接口没有历史、
/// 某些数据在老的【补指定历史日】里走的本来就是水位线增量），列出来只会让人选了没效果。
/// </summary>
public sealed record ModeOption(FetchMode Value, string Text)
{
    public static readonly IReadOnlyList<ModeOption> All =
    [
        new(FetchMode.Incremental, "增量"),
        new(FetchMode.SpecificDay, "只抓某一天"),
        new(FetchMode.FirstBackfill, "首次整段回补"),
        new(FetchMode.Thorough, "彻底重查"),
    ];

    /// <summary>某个动作支持的那几项。</summary>
    public static List<ModeOption> For(FetchMode supported) =>
        All.Where(o => supported.HasFlag(o.Value)).ToList();
}

public sealed record WeekdayOption(DayOfWeek Value, string Text)
{
    public static readonly IReadOnlyList<WeekdayOption> All =
    [
        new(DayOfWeek.Monday, "周一"), new(DayOfWeek.Tuesday, "周二"), new(DayOfWeek.Wednesday, "周三"),
        new(DayOfWeek.Thursday, "周四"), new(DayOfWeek.Friday, "周五"), new(DayOfWeek.Saturday, "周六"),
        new(DayOfWeek.Sunday, "周日"),
    ];
}

/// <summary>
/// 计划里一项的界面包装（2026-08-31 新增）。
///
/// 它**直接改底层的 <see cref="FetchPlanItem"/>**、不做副本：计划正在跑的时候用户照样可以改
/// （把某项停掉、改个时间），引擎每分钟重新评估一次，改动就生效了。改完立刻回存盘，
/// 免得程序意外退出丢掉排了半天的计划。
/// </summary>
public sealed class PlanItemViewModel(FetchPlanItem model, Action onChanged) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public FetchPlanItem Model { get; } = model;
    public FetchActionInfo Info => Model.Info;

    /// <summary>
    /// 这一行在计划里的**执行序号**（1 开始，2026-09-02）。拆细之后表里二十几行，
    /// 没有序号很难说清"第几项跑"。它不进 json——顺序本来就是 Items 的顺序，
    /// 存一份编号只会多一个可能对不上的真相（拖动排序后由 MainViewModel 统一重排）。
    /// </summary>
    private int _order;
    public int Order
    {
        get => _order;
        set { if (_order == value) return; _order = value; Raise(nameof(Order)); Raise(nameof(Number)); }
    }

    private int _groupIndex = 1;
    /// <summary>所在组是第几组（1 开始）。跟 <see cref="Order"/> 一起拼成 <see cref="Number"/>。</summary>
    public int GroupIndex
    {
        get => _groupIndex;
        set { if (_groupIndex == value) return; _groupIndex = value; Raise(nameof(Number)); }
    }

    /// <summary>
    /// 全表唯一的编号 <c>组号.序号</c>（2026-09-02）：日更组 1.01~1.18、季度组 2.xx、按需组 3.xx。
    /// 有了它，"依赖"那一列才写得出"1.02"这种指向——光有组内序号会跨组重名。
    ///
    /// 序号补足**两位**：1.1 和 1.12 混在一列里左对齐时很容易看串行，1.01/1.12 就齐了。
    /// </summary>
    public string Number => $"{GroupIndex}.{Order:D2}";

    private string _dependsText = "";
    /// <summary>
    /// 这一项依赖谁（显示成编号，如 <c>1.2</c>；硬前置带 <c>*</c>）。
    /// 由 MainViewModel 在重编号时统一算好写进来——它才知道全表的编号分布。
    /// </summary>
    public string DependsText
    {
        get => _dependsText;
        set { if (_dependsText == value) return; _dependsText = value; Raise(); }
    }

    public string Name => Info.Name;
    /// <summary>数据源那一列。板块两项在 terminal 通道下会显示成本地文件——
    /// 见 <see cref="FetchActionInfo.DataSourceText"/>。</summary>
    public string DataSource => Info.DataSourceText;
    public string Note => Info.Note;
    public string FrequencyHint => Info.Frequency;
    /// <summary>
    /// 预计耗时：跑过就用这一项自己最近几轮的**实测中位数**，没跑过才用目录里手填的估值
    /// （见 FetchPlanItem.EffectiveEstimate）。手填值在拆细之后只是个数量级。
    /// </summary>
    public string EstimateText => Describe(Model.EffectiveEstimate)
        + (Model.RecentDurationsSec.Count > 0 ? "" : "?");

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            // "手动"的行不许勾选，见 CanEnable。界面上那一格根本没有勾选框，这里再挡一道，
            // 免得以后有别的路径（比如绑定或代码）把它设成 true 又造出"勾着却不跑"的状态。
            if (value && !CanEnable) { Raise(); return; }
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            Raise();
            onChanged();
        }
    }

    /// <summary>
    /// 这一行能不能勾"启用"——重复规则是"手动"时不行。
    ///
    /// 两个状态本来就是互斥的：队列会跳过"手动"的项（见 FetchPlanItem.IsDueOn），
    /// 这时还勾着启用只会让人以为它会自动跑。想让它自动跑，先把重复规则换成
    /// 每工作日/每周/每月。要临时跑一次就选中这一行点【立即执行】——那条路不看这些。
    /// </summary>
    public bool CanEnable => Model.Repeat != RepeatKind.Manual;

    /// <summary>
    /// <see cref="CanEnable"/> 的反面，界面上用。
    ///
    /// 为什么要它：这类行原先是把勾选框**灰掉**，但深色主题下禁用态的勾选框几乎看不见，
    /// 看着像"这一格什么都没有"。现在改成不放勾选框、直接显示一个"—"——没有开关这件事
    /// 本身就该一眼看出来，而不是让人凑近了辨认一个灰框。
    /// </summary>
    public bool CannotEnable => !CanEnable;

    // 2026-09-02：子项不再有自己的「不早于」。时刻只在**组**上（PlanGroupViewModel.NotBeforeText）——
    // 组里是串行跑的，给单项设时刻不起作用；要让某一项更晚开始，就把它单独拆成一组。

    /// <summary>
    /// **每一行自己一份**下拉选项，不共用 <see cref="RepeatOption.All"/> 那个静态列表。
    ///
    /// ════ 为什么必须各自一份 ════
    /// 原先 14 个 ComboBox 全都 <c>ItemsSource="{x:Static vm:RepeatOption.All}"</c>，指向同一个
    /// 集合实例。WPF 会给**同一个集合对象**只建一个默认 CollectionView，而 ComboBox 默认
    /// <c>IsSynchronizedWithCurrentItem</c> 会跟着那个视图的"当前项"走——十四个下拉于是共用
    /// 一个当前项，互相打架。实测的表现是：**选了新值根本不写回源属性**——用自动化选中「手动」
    /// 之后 fetch-plan.json 里还是 EveryWorkday，setter 一次都没进来，所以"启用列不跟着变"
    /// 只是症状，病根在这儿。
    /// 每行一份 List（5 个元素）就各有各的视图了，开销可以忽略。
    /// </summary>
    public IReadOnlyList<RepeatOption> RepeatOptions { get; } = RepeatOption.All.ToList();

    public IReadOnlyList<WeekdayOption> WeekdayOptions { get; } = WeekdayOption.All.ToList();

    // ── 模式：抓哪一段（2026-09-02）──
    // 跟 RepeatOptions 一样，**每行一份自己的列表**，绝不共用静态列表：共用会让十几个下拉
    // 抢同一个 CollectionView 的当前项，选了新值写不回源（见上面 RepeatOptions 的注释）。

    public IReadOnlyList<ModeOption> ModeOptions { get; } = ModeOption.For(model.Info.SupportedModes);

    /// <summary>只有一种模式可选的行（大多数）不显示这个下拉——摆一个只有一项的下拉纯属噪音。</summary>
    public bool ShowMode => ModeOptions.Count > 1;

    public ModeOption SelectedMode
    {
        get => ModeOptions.FirstOrDefault(o => o.Value == Model.EffectiveMode) ?? ModeOptions[0];
        set
        {
            if (value == null || Model.Mode == value.Value) return;
            Model.Mode = value.Value;
            Raise(nameof(SelectedMode));
            Raise(nameof(NeedsDate));
            Raise(nameof(EstimateText));
            onChanged();
        }
    }

    // ── 重复规则、星期几、每月几号：2026-09-02 起全在**组**上（见 PlanGroupViewModel）──
    // 子行只读它们（显示用），不再各自可编辑：13 行日常任务摆 13 份一模一样的"每工作日"
    // 正是拆细之后界面变乱的主因。

    public RepeatKind Repeat => Model.Repeat;

    /// <summary>这一行的组是不是「空闲时补」那一档——状态列据此不显示"预计几点跑"（它没有固定时刻）。</summary>
    public bool IsIdlePaced => Model.Pacing == RunPacing.WhenIdle;

    // ── 动作专属参数 ──

    /// <summary>
    /// 日期框显不显示：**只有「只抓某一天」这个模式才用得上它**（2026-09-02 修）。
    ///
    /// 之前的判据是"这个动作认日期 且 不是整段回补"，结果日常那些走增量的行（资金净流入、
    /// 公告、前复权、融资、龙虎）全都摆着一个日期框——增量是按各自的水位线续抓，
    /// 日期填了根本不起作用，看着却像"这里要我填点什么"。
    ///
    /// 顺带一提：日常缺了哪天不该靠人去填日期补，那是【全库数据体检】的活——
    /// 它查出空洞、交给【重新拉取失败】去补，人不用判断缺了哪天。
    /// </summary>
    public bool NeedsDate => Info.Params.HasFlag(FetchActionParams.Date)
                             && Model.EffectiveMode == FetchMode.SpecificDay;
    public bool NeedsYearRange => Info.Params.HasFlag(FetchActionParams.YearRange);

    public bool NeedsLookback => Info.Params.HasFlag(FetchActionParams.LookbackYears);

    /// <summary>
    /// 是不是【重新拉取失败】那一行——只有它要在参数格里显示"待重试多少"。
    /// 那个数字是**这一个任务专属的待办量**（K线 12 只 / 市值 1 轮…），原先摆在全局参数行里，
    /// 看着像是所有任务共用的东西。
    /// </summary>
    public bool IsRetryFailed => Model.Action == FetchActionId.RetryFailed;

    /// <summary>是不是【重取前复权】那一行——跟上面那行一样，在参数格里显示自己的待办量。</summary>
    public bool IsRepairQfq => Model.Action == FetchActionId.RepairQfq;

    /// <summary>是不是【补不复权历史】那一行——参数格显示还差多少只。</summary>
    public bool IsFetchRawBars => Model.Action == FetchActionId.FetchRawBars;

    /// <summary>是不是【重算回测序列】那一行——参数格显示还有多少只要重算。</summary>
    public bool IsRebuildAdj => Model.Action == FetchActionId.RebuildAdjSeries;

    /// <summary>是不是【拉取财务报表】那一行——参数格显示还差多少只没补。</summary>
    public bool IsFetchFinancials => Model.Action == FetchActionId.FetchFinancials;

    /// <summary>【导入手工数据】那一行——参数格里显示"还有几项要手工填"。</summary>
    public bool IsImportManual => Model.Action == FetchActionId.ImportManual;

    /// <summary>是不是【拉取财报预约日】那一行——参数格显示还有多少只没到披露日。</summary>
    public bool IsFetchEarnings => Model.Action == FetchActionId.FetchEarningsSchedule;
    public bool IsFetchMoneyFlow => Model.Action == FetchActionId.FetchMoneyFlowDetail;

    /// <summary>是不是【板块成分股】那一行——参数格显示还剩多少个板块要抓。</summary>
    public bool IsFetchBoardMembers => Model.Action == FetchActionId.StepBoardMembers;

    /// <summary>
    /// 是不是【交易日历】那一行——参数格显示**日历覆盖到哪天**（2026-09-09）。
    /// 它没有"还差多少只"这种待办量，人判断"还要不要再取"看的就是这个日期。
    /// </summary>
    public bool IsTradingCalendar => Model.Action == FetchActionId.StepTradingCalendar;
    public bool NeedsKeywords => Info.Params.HasFlag(FetchActionParams.Keywords);
    public bool NeedsAnyParam => NeedsDate || NeedsYearRange || NeedsLookback || NeedsKeywords;

    /// <summary>
    /// 这一行的数据要不要等收盘（2026-09-02）。计划自检拿它判断"这一组的时刻会不会太早"，
    /// 界面上也用它给任务名配一句提示（见 DataReadiness）。
    /// </summary>
    public bool NeedsAfterClose => Info.Readiness == DataReadiness.AfterClose;

    /// <summary>中标/订单公告关键词，逗号分隔。留空＝这一项不抓公告。</summary>
    public string KeywordsText
    {
        get => Model.KeywordsText ?? "";
        set { Model.KeywordsText = Blank(value); Raise(); onChanged(); }
    }

    /// <summary>首次回看几年——只有抓K线那几行会显示。留空＝3 年（目录里的默认值）。</summary>
    public string LookbackYearsText
    {
        get => Model.LookbackYearsText ?? "";
        set { Model.LookbackYearsText = Blank(value); Raise(); onChanged(); }
    }

    public string DateText
    {
        get => Model.DateText ?? "";
        set { Model.DateText = Blank(value); Raise(); onChanged(); }
    }

    public string YearStartText
    {
        get => Model.YearStartText ?? "";
        set { Model.YearStartText = Blank(value); Raise(); onChanged(); }
    }

    public string YearEndText
    {
        get => Model.YearEndText ?? "";
        set { Model.YearEndText = Blank(value); Raise(); onChanged(); }
    }

    public bool OverwriteQfq
    {
        get => Model.OverwriteQfq;
        set { if (Model.OverwriteQfq == value) return; Model.OverwriteQfq = value; Raise(); onChanged(); }
    }

    // ── 状态 ────────────────────────────────────────────────────────────────
    // 原先分了「预计」「状态」「上次」三列，用户反馈"预计和上次没什么用"——确实：
    // 三列里同一时刻只有一列有内容，其余两列一片空白，反而看不出重点。现在合成**一列**，
    // 按这一行当下最该被知道的事情显示：
    //   没启用 → 为什么不跑；要跑还没跑 → 预计什么时候跑；正在跑 → 在跑/在等；跑完 → 结果。
    // 更早的历史看 data\local\logs\plan-yyyy-MM-dd.txt，表格里堆历史没意义。

    private string _statusText = "";
    public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }

    /// <summary>状态的颜色分档，给界面上的转换器用：0 普通 / 1 好 / 2 警告 / 3 进行中。</summary>
    private int _statusLevel;
    public int StatusLevel { get => _statusLevel; set { _statusLevel = value; Raise(); } }

    public string LastMessage => Model.LastMessage ?? "";

    /// <summary>上次那一轮的结果摘要，鼠标停在状态上时看（表格里不单独占一列）。</summary>
    public string LastRunTip
    {
        get
        {
            if (Model.LastEnd == null && Model.LastStart == null) return "还没跑过";
            var mark = Model.LastOutcome switch
            {
                RunOutcome.Ok => "✔ 成功",
                RunOutcome.Failed => "✘ 失败",
                RunOutcome.Skipped => "⏭ 跳过",
                RunOutcome.Cancelled => "⏹ 被停止",
                _ => "·",
            };
            var span = Model.LastStart.HasValue && Model.LastEnd.HasValue
                ? "，用时 " + Describe(Model.LastEnd.Value - Model.LastStart.Value) : "";
            return $"上次：{Model.LastEnd:MM-dd HH:mm} {mark}{span}"
                 + (Model.LastErrorCount > 0 ? $"（{Model.LastErrorCount} 条错误）" : "")
                 + (Model.LastMessage is { Length: > 0 } m ? $"\n{m}" : "");
        }
    }

    public void RefreshStatus()
    {
        // 数据源那一列会随板块通道变（terminal＝本地文件），所以换过配置也要重播一次
        Raise(nameof(DataSource));
        Raise(nameof(LastRunTip));
        Raise(nameof(LastMessage));
        Raise(nameof(Enabled));
        Raise(nameof(CanEnable));
        Raise(nameof(CannotEnable));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#}小时"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分钟"
        : $"{(int)t.TotalSeconds}秒";
}
