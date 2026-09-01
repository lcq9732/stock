using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockPlatform.Fetcher.Planning;

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
        new(RepeatKind.WhenIdle, "空闲时"),
        new(RepeatKind.Manual, "手动"),
    ];
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

    public string Name => Info.Name;
    public string DataSource => Info.DataSource;
    public string Note => Info.Note;
    public string FrequencyHint => Info.Frequency;
    public string EstimateText => Describe(Info.Estimate);

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

    /// <summary>"不早于"的时刻，空=接上一项。填错格式时保持原值不动（界面上会看到它弹回去）。</summary>
    public string NotBeforeText
    {
        get => Model.NotBefore.HasValue ? Model.NotBefore.Value.ToString("HH\\:mm") : "";
        set
        {
            if (!FetchPlan.TryParseTime(value, out var parsed)) { Raise(); return; }
            if (Model.NotBefore == parsed) return;
            Model.NotBefore = parsed;
            Raise();
            onChanged();
        }
    }

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

    /// <summary>
    /// 下拉绑的是 <c>SelectedItem</c>（这个属性），不是 <c>SelectedValue</c>+<c>SelectedValuePath</c>。
    /// 后者多绕一层"按路径取值再回填"，正是上面那个坑最容易发作的地方；直接给对象最稳。
    /// </summary>
    public RepeatOption SelectedRepeat
    {
        get => RepeatOptions.FirstOrDefault(o => o.Kind == Model.Repeat) ?? RepeatOptions[0];
        set { if (value != null) Repeat = value.Kind; }
    }

    public WeekdayOption SelectedWeekday
    {
        get => WeekdayOptions.FirstOrDefault(o => o.Value == Model.Weekday) ?? WeekdayOptions[0];
        set { if (value != null) Weekday = value.Value; }
    }

    public RepeatKind Repeat
    {
        get => Model.Repeat;
        set
        {
            if (Model.Repeat == value) return;
            Model.Repeat = value;
            // 换成"手动"就顺手取消启用——两者互斥（见 CanEnable）。换回定期规则时不自动帮你勾上：
            // 那是"要不要跑"的决定，得你自己点。
            if (value == RepeatKind.Manual && Model.Enabled) Model.Enabled = false;
            Raise();
            Raise(nameof(SelectedRepeat));
            Raise(nameof(Enabled));
            Raise(nameof(CanEnable));
            Raise(nameof(CannotEnable));
            Raise(nameof(ShowWeekday));
            Raise(nameof(ShowDayOfMonth));
            Raise(nameof(ShowNotBefore));
            onChanged();
        }
    }

    public DayOfWeek Weekday
    {
        get => Model.Weekday;
        set
        {
            if (Model.Weekday == value) return;
            Model.Weekday = value;
            Raise();
            Raise(nameof(SelectedWeekday));
            onChanged();
        }
    }

    public string DayOfMonthText
    {
        get => Model.DayOfMonth.ToString();
        set
        {
            if (!int.TryParse(value?.Trim(), out var d) || d is < 1 or > 31) { Raise(); return; }
            if (Model.DayOfMonth == d) return;
            Model.DayOfMonth = d;
            Raise();
            onChanged();
        }
    }

    public bool ShowWeekday => Model.Repeat == RepeatKind.Weekly;
    public bool ShowDayOfMonth => Model.Repeat == RepeatKind.Monthly;

    /// <summary>
    /// 「不早于」这个参数对这一行有没有意义。只有**定时**项才有：
    /// 空闲项由 PlanRunner.FindIdleTask 挑、根本不看 NotBefore（FindNext 里直接 continue 掉了），
    /// 手动项也不参与自动调度——给它们摆一个时间框纯属误导。
    /// </summary>
    public bool ShowNotBefore => Model.Repeat is not (RepeatKind.Manual or RepeatKind.WhenIdle);

    // ── 动作专属参数 ──
    public bool NeedsDate => Info.Params.HasFlag(FetchActionParams.Date);
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
    public bool NeedsAnyParam => NeedsDate || NeedsYearRange || NeedsLookback;

    /// <summary>首次回看几年——只有「拉取全部」这一行会显示。留空=用【手动】页那个框的值。</summary>
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
