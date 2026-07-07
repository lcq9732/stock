using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Fetcher.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly FetchOrchestrator _orchestrator;

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Available data sources — the user picks exactly one per run (see doc/data-platform-design.md 3.4).
    /// No automatic switching: if the selected source stops working, the user manually switches here and re-runs.</summary>
    public List<NamedBarSource> AvailableSources { get; }

    private NamedBarSource _selectedSource;
    public NamedBarSource SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    /// <summary>Live "still alive" ticker shown next to 运行中, independent of log lines —
    /// a long silent step (e.g. fetching the full stock list) shouldn't look indistinguishable
    /// from a hung process.</summary>
    private string _elapsedText = "";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    private CancellationTokenSource? _cts;
    private DispatcherTimer? _heartbeat;
    private DateTime _runStartedAt;

    /// <summary>How many daily increment files are waiting to be merged into a new master
    /// (see doc/data-platform-design.md 6.2) — shown next to the "合并" button as a nudge.</summary>
    private int _pendingDailyCount;
    public int PendingDailyCount { get => _pendingDailyCount; private set => Set(ref _pendingDailyCount, value); }

    private string _pendingDailyText = "";
    public string PendingDailyText { get => _pendingDailyText; private set => Set(ref _pendingDailyText, value); }

    /// <summary>Date typed in for "拉取当天" (see doc/data-platform-design.md) — free text so the
    /// user can pick any day, defaults to today. Parsed on click, not as-you-type, so a
    /// momentarily invalid string while editing doesn't disable the button underneath them.</summary>
    private string _fetchDayText = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    public string FetchDayText { get => _fetchDayText; set => Set(ref _fetchDayText, value); }

    public RelayCommand FetchCommand { get; }
    public RelayCommand FetchDayCommand { get; }
    public RelayCommand MergeCommand { get; }
    public RelayCommand StopCommand { get; }

    public MainViewModel(FetchOrchestrator orchestrator, List<NamedBarSource> availableSources)
    {
        _orchestrator = orchestrator;
        AvailableSources = availableSources;
        // Default to Tencent, not the first entry — EastMoney gets network-limited/blocked much
        // faster on some machines (see doc/data-platform-design.md), Tencent+新浪 has proven
        // stable in practice. Falls back to the first source if "Tencent" isn't in the list.
        _selectedSource = availableSources.FirstOrDefault(s => s.Name == "Tencent") ?? availableSources[0];

        FetchCommand = new RelayCommand(async _ => await RunFetchAsync(), _ => !IsBusy);
        FetchDayCommand = new RelayCommand(async _ => await RunFetchDayAsync(), _ => !IsBusy);
        MergeCommand = new RelayCommand(async _ => await RunMergeAsync(), _ => !IsBusy);
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);

        RefreshPendingDaily();
    }

    private void Log(string message) => LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private void StartHeartbeat()
    {
        _runStartedAt = DateTime.Now;
        ElapsedText = "已运行 0 秒";
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeat.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _runStartedAt;
            ElapsedText = elapsed.TotalMinutes >= 1
                ? $"已运行 {(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
                : $"已运行 {elapsed.Seconds} 秒";
        };
        _heartbeat.Start();
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Stop();
        _heartbeat = null;
        ElapsedText = "";
    }

    private void RefreshPendingDaily()
    {
        var pending = _orchestrator.GetPendingDailyFiles();
        PendingDailyCount = pending.Count;
        PendingDailyText = pending.Count == 0
            ? "暂无待合并的日增量文件"
            : $"待合并的日增量文件：{pending.Count} 个（最早 {pending.Min(d => d.Date):yyyy-MM-dd}，最新 {pending.Max(d => d.Date):yyyy-MM-dd}），建议点击\"合并\"";
    }

    private async Task RunFetchAsync()
    {
        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(Log);
            var result = await _orchestrator.RunFetchAsync(SelectedSource, progress, _cts.Token);

            foreach (var err in result.Errors) Log($"错误：{err}");

            if (result.ProducedFile == null)
            {
                Log("本次抓取没有产生新文件（所有股票均已是最新）");
            }
            else
            {
                Log($"已生成 {result.ProducedFile}，请手动上传到网盘");
                if (result.OutboxPath != null) Log($"文件已放入本地暂存目录：{result.OutboxPath}");
            }
        }
        catch (OperationCanceledException)
        {
            Log("已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"抓取失败：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshPendingDaily();
            IsBusy = false;
            // Unconditional, unmistakable end-of-run marker — regardless of success/error/cancel,
            // once we're here the run is definitively over and nothing will continue on its own.
            // Without this, a wall of per-stock error lines right before the run ends can read as
            // "still going wrong" rather than "already stopped" (see doc/data-platform-design.md).
            Log("===== 本轮已结束，不会自动继续，需要再次抓取/合并请重新点击按钮 =====");
        }
    }

    private async Task RunFetchDayAsync()
    {
        if (!DateOnly.TryParseExact(FetchDayText.Trim(), "yyyy-MM-dd", out var date))
        {
            Log($"日期格式不对：\"{FetchDayText}\"，请用 yyyy-MM-dd 格式（例如 2026-07-06）");
            return;
        }

        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(Log);
            var result = await _orchestrator.RunFetchDayAsync(SelectedSource, date, progress, _cts.Token);

            foreach (var err in result.Errors) Log($"错误：{err}");

            if (result.ProducedFile == null)
            {
                Log("本次按天抓取没有产生新文件（该日期所有股票均已是最新）");
            }
            else
            {
                Log($"已生成 {result.ProducedFile}，请手动上传到网盘");
                if (result.OutboxPath != null) Log($"文件已放入本地暂存目录：{result.OutboxPath}");
            }
        }
        catch (OperationCanceledException)
        {
            Log("已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"按天抓取失败：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshPendingDaily();
            IsBusy = false;
            // Unconditional, unmistakable end-of-run marker — regardless of success/error/cancel,
            // once we're here the run is definitively over and nothing will continue on its own.
            // Without this, a wall of per-stock error lines right before the run ends can read as
            // "still going wrong" rather than "already stopped" (see doc/data-platform-design.md).
            Log("===== 本轮已结束，不会自动继续，需要再次抓取/合并请重新点击按钮 =====");
        }
    }

    private async Task RunMergeAsync()
    {
        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _orchestrator.RunMergeAsync(_cts.Token);
            Log($"合并完成，已生成新的总数据文件 {result.NewMasterFile}，请手动上传到网盘");
            Log($"文件已放入本地暂存目录：{result.OutboxPath}");
        }
        catch (OperationCanceledException)
        {
            Log("已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"合并失败：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshPendingDaily();
            IsBusy = false;
            // Unconditional, unmistakable end-of-run marker — regardless of success/error/cancel,
            // once we're here the run is definitively over and nothing will continue on its own.
            // Without this, a wall of per-stock error lines right before the run ends can read as
            // "still going wrong" rather than "already stopped" (see doc/data-platform-design.md).
            Log("===== 本轮已结束，不会自动继续，需要再次抓取/合并请重新点击按钮 =====");
        }
    }
}
