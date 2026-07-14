using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private readonly StreamWriter? _logFileWriter;

    /// <summary>失败股票的重试名单里还有多少只（见 FetchOrchestrator.GetFailedCodeCount）——
    /// 只在这个数字大于0时"重新拉取失败股票"按钮才可点。</summary>
    private int _failedCodeCount;
    public int FailedCodeCount { get => _failedCodeCount; private set => Set(ref _failedCodeCount, value); }

    /// <summary>本地数据覆盖范围 + 上次实际抓取时间（见 FetchOrchestrator.GetDataStatus）——帮用户
    /// 判断该不该再点一次抓取，不用凭感觉重复点或者担心漏了哪天。</summary>
    private string _dataStatusText = "";
    public string DataStatusText { get => _dataStatusText; private set => Set(ref _dataStatusText, value); }

    /// <summary>Date typed in for "拉取当天" (see doc/data-platform-design.md) — free text so the
    /// user can pick any day, defaults to today. Parsed on click, not as-you-type, so a
    /// momentarily invalid string while editing doesn't disable the button underneath them.</summary>
    private string _fetchDayText = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    public string FetchDayText { get => _fetchDayText; set => Set(ref _fetchDayText, value); }

    /// <summary>"拉取全部"里，遇到本地完全没有历史的股票（真正的首次运行，或者新上市还没抓过的
    /// 股票）时回看多少年——只影响这种股票，已经抓过的股票永远从自己上次抓到的日期+1继续，不受
    /// 这个设置影响。用户可调，默认3年。</summary>
    private string _lookbackYearsText = "3";
    public string LookbackYearsText { get => _lookbackYearsText; set => Set(ref _lookbackYearsText, value); }

    /// <summary>Comma-separated keywords for the 中标/订单公告 keyword sweep — see
    /// AnnouncementFetchOrchestrator. Defaults to the two most common order-win announcement
    /// phrasings. Used automatically by both "拉取全部" and "拉取当天" now (see
    /// FetchOrchestrator.FetchAnnouncementsAsync) — not a separately-triggered action anymore.</summary>
    private string _announcementKeywordsText = "中标,签订合同";
    public string AnnouncementKeywordsText { get => _announcementKeywordsText; set => Set(ref _announcementKeywordsText, value); }

    public RelayCommand FetchCommand { get; }
    public RelayCommand FetchDayCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RetryFailedCommand { get; }
    public RelayCommand BackfillAmountTurnoverCommand { get; }

    public MainViewModel(FetchPaths paths, FetchOrchestrator orchestrator, List<NamedBarSource> availableSources)
    {
        _orchestrator = orchestrator;
        AvailableSources = availableSources;
        // Default to Tencent, not the first entry — EastMoney gets network-limited/blocked much
        // faster on some machines (see doc/data-platform-design.md), Tencent+新浪 has proven
        // stable in practice. Falls back to the first source if "Tencent" isn't in the list.
        _selectedSource = availableSources.FirstOrDefault(s => s.Name == "Tencent") ?? availableSources[0];

        // 每次程序启动清空重写（不是追加/不是按天滚动）——这只是给"程序意外退出时还能看到发生了
        // 什么"用的诊断日志，不是长期审计记录，保持单文件+每次重开清零最简单。AutoFlush让每行
        // 一写完就落盘，崩溃/被强制结束也不会丢失最后那几行。
        try
        {
            _logFileWriter = new StreamWriter(paths.LogFilePath, append: false) { AutoFlush = true };
        }
        catch
        {
            _logFileWriter = null; // 日志文件打不开（比如被占用）不应该阻止程序正常使用
        }

        FetchCommand = new RelayCommand(async _ => await RunFetchAsync(), _ => !IsBusy);
        FetchDayCommand = new RelayCommand(async _ => await RunFetchDayAsync(), _ => !IsBusy);
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        RetryFailedCommand = new RelayCommand(async _ => await RunRetryFailedAsync(), _ => !IsBusy && FailedCodeCount > 0);
        BackfillAmountTurnoverCommand = new RelayCommand(async _ => await RunBackfillAmountTurnoverAsync(), _ => !IsBusy);

        RefreshDataStatus();
        RefreshFailedCodeCount();
    }

    private List<string> ParseAnnouncementKeywords() =>
        AnnouncementKeywordsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void RefreshDataStatus()
    {
        var status = _orchestrator.GetDataStatus();
        if (status.EarliestDay == null || status.LatestDay == null)
        {
            DataStatusText = "本地还没有任何K线数据";
        }
        else
        {
            DataStatusText = $"本地数据覆盖：{status.EarliestDay:yyyy-MM-dd} 至 {status.LatestDay:yyyy-MM-dd}";
            if (status.LastFetchAt != null)
                DataStatusText += $"；上次抓取：{status.LastFetchAt:yyyy-MM-dd HH:mm}（{status.LastFetchKind}）";
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogLines.Insert(0, line);
        _logFileWriter?.WriteLine(line);
    }

    private void RefreshFailedCodeCount() => FailedCodeCount = _orchestrator.GetFailedCodeCount();

    private void StartHeartbeat()
    {
        _runStartedAt = DateTime.Now;
        ElapsedText = "已运行 0 秒";
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeat.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _runStartedAt;
            ElapsedText = elapsed.TotalHours >= 1
                ? $"已运行 {(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分 {elapsed.Seconds} 秒"
                : elapsed.TotalMinutes >= 1
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

    private async Task RunFetchAsync()
    {
        if (!int.TryParse(LookbackYearsText.Trim(), out var lookbackYears) || lookbackYears <= 0)
        {
            Log($"回看年数不对：\"{LookbackYearsText}\"，请填一个正整数（例如 3）");
            return;
        }

        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(Log);
            var result = await _orchestrator.RunFetchAsync(SelectedSource, lookbackYears, ParseAnnouncementKeywords(), progress, _cts.Token);
            foreach (var err in result.Errors) Log($"错误：{err}");
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
            RefreshDataStatus();
            RefreshFailedCodeCount();
            IsBusy = false;
            // Unconditional, unmistakable end-of-run marker — regardless of success/error/cancel,
            // once we're here the run is definitively over and nothing will continue on its own.
            // Without this, a wall of per-stock error lines right before the run ends can read as
            // "still going wrong" rather than "already stopped" (see doc/data-platform-design.md).
            Log("===== 本轮已结束，不会自动继续，需要再次抓取请重新点击按钮 =====");
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
            var result = await _orchestrator.RunFetchDayAsync(SelectedSource, date, ParseAnnouncementKeywords(), progress, _cts.Token);
            foreach (var err in result.Errors) Log($"错误：{err}");
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
            RefreshDataStatus();
            RefreshFailedCodeCount();
            IsBusy = false;
            // Unconditional, unmistakable end-of-run marker — regardless of success/error/cancel,
            // once we're here the run is definitively over and nothing will continue on its own.
            // Without this, a wall of per-stock error lines right before the run ends can read as
            // "still going wrong" rather than "already stopped" (see doc/data-platform-design.md).
            Log("===== 本轮已结束，不会自动继续，需要再次抓取请重新点击按钮 =====");
        }
    }

    /// <summary>一次性修复历史数据的回填（见 FetchOrchestrator.RunBackfillAmountTurnoverAsync）
    /// ——2026-07-10之前入库的日线成交额/换手率全是0，正常抓取（增量水位线+INSERT OR IGNORE）
    /// 永远不会回头补这些旧行，只能靠这个按钮。幂等，可随时停止后再点、只会继续补还缺的。</summary>
    private async Task RunBackfillAmountTurnoverAsync()
    {
        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(Log);
            var result = await _orchestrator.RunBackfillAmountTurnoverAsync(SelectedSource, progress, _cts.Token);
            foreach (var err in result.Errors) Log($"错误：{err}");
        }
        catch (OperationCanceledException)
        {
            Log("已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"回填成交额/换手率时出错：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshDataStatus();
            RefreshFailedCodeCount();
            IsBusy = false;
            Log("===== 本轮已结束，不会自动继续，需要再次抓取请重新点击按钮 =====");
        }
    }

    private async Task RunRetryFailedAsync()
    {
        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(Log);
            var result = await _orchestrator.RunRetryFailedAsync(SelectedSource, progress, _cts.Token);
            foreach (var err in result.Errors) Log($"错误：{err}");
        }
        catch (OperationCanceledException)
        {
            Log("已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"重新拉取失败股票时出错：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshDataStatus();
            RefreshFailedCodeCount();
            IsBusy = false;
            Log("===== 本轮已结束，不会自动继续，需要再次抓取请重新点击按钮 =====");
        }
    }
}
