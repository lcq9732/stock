using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockPlatform.Scheduling;

namespace StockPlatform.Fetcher.ViewModels;

/// <summary>
/// 计划里一个**组**的界面包装（2026-09-02 新增）。
///
/// 组头这一行管的是"什么时候跑"：整组启用、重复规则、不早于、这一组的时间轴和合计耗时。
/// 组里每一行管的是"做什么"：模式、参数、顺序。
///
/// ⚠ 组是**排期单位**，不是执行单位——判定谁跑过、谁失败了永远按子项来
/// （见 <see cref="FetchPlanGroup"/> 的类注释）。所以这里没有"整组的运行结果"这种东西，
/// 组头显示的状态是**按子项汇总出来的**（3/14 完成）。
/// </summary>
public sealed class PlanGroupViewModel(FetchPlanGroup model, Action onChanged) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public FetchPlanGroup Model { get; } = model;

    public ObservableCollection<PlanItemViewModel> Items { get; } = [];

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name == value) return; Model.Name = value ?? ""; Raise(); onChanged(); }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            Raise();
            // 子行的"会不会跑"取决于组，勾选组要让每一行的状态跟着刷新
            foreach (var i in Items) i.RefreshStatus();
            onChanged();
        }
    }

    /// <summary>「手动」组不存在"启用"这回事——它永远不自动跑，勾着只会误导。</summary>
    public bool CanEnable => Model.Repeat != RepeatKind.Manual;

    // ── 下拉选项：**每个组自己一份**，绝不共用静态列表 ──
    // 共用会让多个 ComboBox 抢同一个 CollectionView 的当前项，选了新值写不回源
    // （踩过的坑见 PlanItemViewModel.RepeatOptions 的注释）。
    public IReadOnlyList<RepeatOption> RepeatOptions { get; } = RepeatOption.All.ToList();
    public IReadOnlyList<WeekdayOption> WeekdayOptions { get; } = WeekdayOption.All.ToList();

    public RepeatOption SelectedRepeat
    {
        get => RepeatOptions.FirstOrDefault(o => o.Kind == Model.Repeat) ?? RepeatOptions[0];
        set
        {
            if (value == null || Model.Repeat == value.Kind) return;
            Model.Repeat = value.Kind;
            // 换成「手动」就顺手取消启用——两者互斥。换回定期规则时不自动帮你勾上：
            // 那是"要不要跑"的决定，得自己点。
            if (value.Kind == RepeatKind.Manual && Model.Enabled) Model.Enabled = false;
            Raise();
            Raise(nameof(Enabled));
            Raise(nameof(CanEnable));
            Raise(nameof(ShowWeekday));
            Raise(nameof(ShowDayOfMonth));
            Raise(nameof(ShowNotBefore));
            Raise(nameof(ShowPacing));
            foreach (var i in Items) i.RefreshStatus();
            onChanged();
        }
    }

    // ── 到期之后怎么跑（2026-09-02）──
    // 跟重复规则是两个正交的维度：前者说"多久到期一次"，这个说"急不急"。
    // 拆开之后才表达得了"每月 1 号到期、然后空闲时慢慢补"（财务报表就得这么跑）。

    public IReadOnlyList<PacingOption> PacingOptions { get; } = PacingOption.All.ToList();

    public PacingOption SelectedPacing
    {
        get => PacingOptions.FirstOrDefault(o => o.Value == Model.Pacing) ?? PacingOptions[0];
        set
        {
            if (value == null || Model.Pacing == value.Value) return;
            Model.Pacing = value.Value;
            Raise();
            Raise(nameof(ShowNotBefore));
            foreach (var i in Items) i.RefreshStatus();
            onChanged();
        }
    }

    /// <summary>「手动」组不自动跑，谈不上"到期后怎么跑"。</summary>
    public bool ShowPacing => Model.Repeat != RepeatKind.Manual;

    public WeekdayOption SelectedWeekday
    {
        get => WeekdayOptions.FirstOrDefault(o => o.Value == Model.Weekday) ?? WeekdayOptions[0];
        set
        {
            if (value == null || Model.Weekday == value.Value) return;
            Model.Weekday = value.Value; Raise(); onChanged();
        }
    }

    public string DayOfMonthText
    {
        get => Model.DayOfMonth.ToString();
        set
        {
            if (!int.TryParse(value?.Trim(), out var d) || d is < 1 or > 31) { Raise(); return; }
            if (Model.DayOfMonth == d) return;
            Model.DayOfMonth = d; Raise(); onChanged();
        }
    }

    public string NotBeforeText
    {
        get => Model.NotBefore?.ToString("HH\\:mm") ?? "";
        set
        {
            if (!FetchPlan.TryParseTime(value, out var parsed)) { Raise(); return; }
            if (Model.NotBefore == parsed) return;
            Model.NotBefore = parsed; Raise(); onChanged();
        }
    }

    public bool ShowWeekday => Model.Repeat == RepeatKind.Weekly;
    public bool ShowDayOfMonth => Model.Repeat == RepeatKind.Monthly;

    /// <summary>
    /// 「不早于」只对**到点就跑**的定时组有意义：「空闲时补」的组填的是别人不用的空档、
    /// 本来就没有固定时刻，「手动」组不自动跑。
    /// </summary>
    public bool ShowNotBefore =>
        Model.Repeat != RepeatKind.Manual && Model.Pacing == RunPacing.Immediate;

    public bool IsExpanded
    {
        get => !Model.Collapsed;
        set { if (Model.Collapsed != value) return; Model.Collapsed = !value; Raise(); onChanged(); }
    }

    /// <summary>组头右边那句汇总："14 项 · 3 项没勾 · 合计 3小时12分"。</summary>
    public string SummaryText
    {
        get
        {
            int on = Items.Count(i => i.Enabled);
            var total = TimeSpan.FromSeconds(Items.Where(i => i.Enabled)
                .Sum(i => i.Model.EffectiveEstimate.TotalSeconds));
            return $"{Items.Count} 项"
                 + (on < Items.Count ? $"（{on} 项启用）" : "")
                 + (on > 0 ? $" · 合计 {Describe(total)}" : "");
        }
    }

    /// <summary>组头中间那段时间轴，由 MainViewModel 按顺序累加算出来后写进来。</summary>
    private string _timelineText = "";
    public string TimelineText
    {
        get => _timelineText;
        set { if (_timelineText == value) return; _timelineText = value; Raise(); }
    }

    /// <summary>
    /// 组头那行进度，按**子项**汇总（组本身没有运行结果，见类注释）。
    ///
    /// ════ 说的是「今天要做多少」，不是「过去做了多少」（2026-09-04 用户指出）════
    /// 这一行原来数的是"完成时间落在今天的项"，日更组 18:00 开工跑到次日凌晨，白天看到的
    /// 就成了昨晚那一轮的残影——刚启动计划，组头写着"14/20 完成"，而今晚这 20 项一个都还没跑。
    /// 按收盘重跑规则，今天到点后它们全都要重跑，所以那个数字对"今天"毫无意义。
    ///
    /// 现在分两种情形，跟运行日志的清单同一套判断：
    ///   ① 今天的到点还没到 → 说"今天要跑 N 项"，不报任何完成数；
    ///   ② 今天这一轮已经开始 → 报这一轮的进度"今天 X/N 完成"。
    /// </summary>
    public string ProgressText
    {
        get
        {
            var now = DateTime.Now;
            var enabled = Items.Where(i => i.Enabled).ToList();
            int on = enabled.Count;
            if (on == 0) return "";

            // 今天这一轮开始了没有——锚点落在今天就是开始了（同 PlanRunner.LogTodayPlan）
            var anchor = enabled.Select(i => i.Model.DueAnchorAt(now))
                                .Where(x => x is { } v && v != DateTime.MinValue)
                                .Select(x => x!.Value).DefaultIfEmpty().Max();
            bool 今轮已开始 = anchor != default && anchor.Date == now.Date;

            if (!今轮已开始)
            {
                var 起跑 = enabled.Select(i => i.Model.DueTimeOn(now))
                                  .Where(t => t.Date == now.Date).DefaultIfEmpty().Min();
                return 起跑 != default ? $"今天 {起跑:HH\\:mm} 起 {on} 项" : $"今天 {on} 项";
            }

            int done = 0, failed = 0;
            foreach (var i in enabled)
            {
                if (i.Model.AlreadyRanOn(now)) { done++; continue; }
                // 这一轮里失败的（AlreadyFailedOn 本身按自然日算，这里要"本轮失败过"）
                if (i.Model.LastOutcome == RunOutcome.Failed
                    && i.Model.DueAnchorAt(now) is { } a
                    && (i.Model.LastStart ?? i.Model.LastEnd) is { } t && t >= a) failed++;
            }
            if (done == 0 && failed == 0) return $"今天 {on} 项在跑";
            return $"今天 {done}/{on} 完成" + (failed > 0 ? $"、{failed} 失败" : "");
        }
    }

    public void RefreshHeader()
    {
        Raise(nameof(SummaryText));
        Raise(nameof(ProgressText));
    }

    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}分钟"
        : $"{(int)t.TotalSeconds}秒";
}
