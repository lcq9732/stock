using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockPlatform.Scheduling;

namespace StockPlatform.Fetcher.ViewModels;

/// <summary>
/// 「正在执行」列表里的一行（2026-09-05）。
///
/// 原来这块是一行拼好的字符串（SourceOccupancy 的 OccupancyText），只能看不能动；
/// 用户要的是**每一项后面一个【停止】**——并发跑着两三项时【停止全部】太狠：
/// 另一项可能已经跑了一个多小时，不该被连坐。
///
/// 每行自己记开始时刻、自己算已用时。以前顶上只有一个全局的"已运行 x 分"，
/// 并发之后那个数字属于谁根本说不清（顶上还写着"空闲"，因为并发任务不占 IsBusy）。
///
/// 两种来源：
///   · 占用表里的任务（计划项、按行【执行】）——<see cref="FromOccupancy"/> 为 true，
///     由 MainViewModel 跟着占用表增删；
///   · 【手动】页那种横跨所有数据源的大任务——它不进占用表（它跟谁都冲突，登记进去
///     只会把自己挡在门外），所以由 MainViewModel 手工加一行、跑完手工去掉。
/// </summary>
public sealed class RunningTaskViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly CancellationTokenSource _cts;
    private readonly DateTime _startedAt;

    private RunningTaskViewModel(
        Guid id, string name, string detail, DateTime startedAt,
        CancellationTokenSource cts, bool fromOccupancy, Action<RunningTaskViewModel> stop)
    {
        Id = id;
        Name = name;
        Detail = detail;
        _startedAt = startedAt;
        _cts = cts;
        FromOccupancy = fromOccupancy;
        // 已经点过【停止】的行不让再点：信号已经发出去了，再点一次什么也不会发生，
        // 只会让人以为"点了没用"。按钮此时改写成"停止中"，见 StopButtonText。
        StopCommand = new RelayCommand(_ => stop(this), _ => !Stopping);
        UpdateElapsed();
    }

    /// <summary>占用表里的一项。</summary>
    public static RunningTaskViewModel FromTask(RunningTask t, Action<RunningTaskViewModel> stop)
        => new(t.Id, t.Name, t.SourcesText + (t.Manual ? "，手动" : ""), t.StartedAt, t.Cts, true, stop);

    /// <summary>【手动】页那种不进占用表的大任务。</summary>
    public static RunningTaskViewModel FromManualRun(
        string name, CancellationTokenSource cts, Action<RunningTaskViewModel> stop)
        => new(Guid.NewGuid(), name, "手动，横跨所有数据源", DateTime.Now, cts, false, stop);

    public Guid Id { get; }
    public string Name { get; }

    /// <summary>括号里那截：占了哪些源、是不是手动点的。</summary>
    public string Detail { get; }

    public bool FromOccupancy { get; }

    public string Title => $"{Name}（{Detail}）";

    private string _elapsedText = "";
    /// <summary>这一项自己的已用时，由 MainViewModel 的秒级定时器驱动。</summary>
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    private bool _stopping;
    /// <summary>已经发出停止信号、还在收尾（占用表里的任务是在自己的 finally 里才消失的）。</summary>
    public bool Stopping
    {
        get => _stopping;
        set
        {
            if (!Set(ref _stopping, value)) return;
            Raise(nameof(StopButtonText));
            // RelayCommand 的 CanExecuteChanged 挂在 CommandManager 上，它只在鼠标键盘活动之后
            // 才重新问一遍——不主动踢一下的话，按钮会一直保持可点的样子（点了也没用）。
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StopButtonText => Stopping ? "停止中" : "停止";

    public RelayCommand StopCommand { get; }

    /// <summary>叫停它自己。返回 false = 它已经收工了（CTS 都释放了），不用再提示什么。</summary>
    public bool Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    public void UpdateElapsed()
    {
        var e = DateTime.Now - _startedAt;
        ElapsedText = e.TotalHours >= 1
            ? $"已跑 {(int)e.TotalHours} 小时 {e.Minutes} 分"
            : e.TotalMinutes >= 1
                ? $"已跑 {(int)e.TotalMinutes} 分 {e.Seconds} 秒"
                : $"已跑 {e.Seconds} 秒";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
