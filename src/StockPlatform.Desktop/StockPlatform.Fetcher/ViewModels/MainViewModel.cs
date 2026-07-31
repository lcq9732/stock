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
    private readonly FetchPaths _paths;

    /// <summary>失败股票的重试名单里还有多少只（见 FetchOrchestrator.GetFailedCodeCount）——
    /// 只在这个数字大于0时"重新拉取失败股票"按钮才可点。</summary>
    private int _failedCodeCount;
    public int FailedCodeCount { get => _failedCodeCount; private set => Set(ref _failedCodeCount, value); }

    /// <summary>本地数据覆盖范围 + 上次实际抓取时间（见 FetchOrchestrator.GetDataStatus）——帮用户
    /// 判断该不该再点一次抓取，不用凭感觉重复点或者担心漏了哪天。</summary>
    private string _dataStatusText = "";
    public string DataStatusText { get => _dataStatusText; private set => Set(ref _dataStatusText, value); }

    /// <summary>Date typed in for "补指定历史日"（旧名"拉取当天"） (see doc/data-platform-design.md) — free text so the
    /// user can pick any day, defaults to today. Parsed on click, not as-you-type, so a
    /// momentarily invalid string while editing doesn't disable the button underneath them.</summary>
    private string _fetchDayText = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");
    public string FetchDayText { get => _fetchDayText; set => Set(ref _fetchDayText, value); }

    /// <summary>"拉取全部"里，遇到本地完全没有历史的股票（真正的首次运行，或者新上市还没抓过的
    /// 股票）时回看多少年——只影响这种股票，已经抓过的股票永远从自己上次抓到的日期+1继续，不受
    /// 这个设置影响。用户可调，默认3年。</summary>
    private string _lookbackYearsText = "3";
    public string LookbackYearsText { get => _lookbackYearsText; set => Set(ref _lookbackYearsText, value); }

    /// <summary>"拉取指定年份区间"的起始年（2026-07-29新增，同日从单年改为区间）——往回补历史用，见
    /// FetchOrchestrator.RunFetchYearAsync。默认填去年（最常见的用法是把去年补齐）；跟"首次回看"
    /// 是两件事：回看年数只影响"从没抓过的标的"，调大它也不会让已有标的的历史往前延长，要补更早的
    /// 年份就得用这个按钮。点击时解析，编辑中途的非法值不会禁用按钮。</summary>
    private string _fetchYearText = (DateTime.Today.Year - 1).ToString();
    public string FetchYearText { get => _fetchYearText; set => Set(ref _fetchYearText, value); }

    /// <summary>"拉取指定年份区间"的结束年——留空表示"从起始年一直补到现在"；与起始年填一样就是只补那一年。</summary>
    private string _fetchYearEndText = "";
    public string FetchYearEndText { get => _fetchYearEndText; set => Set(ref _fetchYearEndText, value); }

    /// <summary>"覆盖重抓前复权"（2026-07-30新增）——勾上后区间抓取不再跳过本地已有的部分，而是把整段
    /// 前复权按数据源当前基准重写一遍，用来一次性抹平历史上分批入库造成的复权基准接缝（见
    /// FetchOrchestrator.RepairDriftedHistoryAsync）。默认不勾：勾了这一轮会失去"已有就跳过"的优化、
    /// 耗时与首次回补相当。日常的漂移由抓取时的自动检测修正，不需要靠这个。</summary>
    private bool _overwriteQfq;
    public bool OverwriteQfq { get => _overwriteQfq; set => Set(ref _overwriteQfq, value); }

    /// <summary>Comma-separated keywords for the 中标/订单公告 keyword sweep — see
    /// AnnouncementFetchOrchestrator. Defaults to the two most common order-win announcement
    /// phrasings. Used automatically by both "拉取全部" and "补指定历史日" now (see
    /// FetchOrchestrator.FetchAnnouncementsAsync) — not a separately-triggered action anymore.</summary>
    private string _announcementKeywordsText = "中标,签订合同";
    public string AnnouncementKeywordsText { get => _announcementKeywordsText; set => Set(ref _announcementKeywordsText, value); }

    /// <summary>"定时拉取"的触发时间（HH:mm，默认 18:00）——点定时按钮后等到这个时间再开始，用于收盘确认后
    /// (建议18点以后：K线已收盘确认、融资/龙虎已公布)无人值守自动取当天最终数据；点击时已过该时间则立即执行。</summary>
    private string _scheduleTimeText = "18:00";
    public string ScheduleTimeText { get => _scheduleTimeText; set => Set(ref _scheduleTimeText, value); }

    public RelayCommand FetchCommand { get; }
    public RelayCommand FetchDayCommand { get; }
    public RelayCommand FetchYearCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RetryFailedCommand { get; }
    public RelayCommand FetchBoardsCommand { get; }
    public RelayCommand BackfillAmountTurnoverCommand { get; }
    public RelayCommand UploadBaselineCommand { get; }
    public RelayCommand UploadDailyCommand { get; }
    public RelayCommand BackfillDailyCommand { get; }
    public RelayCommand FetchPeriodicCommand { get; }
    public RelayCommand FetchFinancialsCommand { get; }
    public RelayCommand FetchDividendCommand { get; }
    public RelayCommand ScheduledFetchAllCommand { get; }
    public RelayCommand ScheduledFetchDayCommand { get; }

    public MainViewModel(FetchPaths paths, FetchOrchestrator orchestrator, List<NamedBarSource> availableSources)
    {
        _orchestrator = orchestrator;
        _paths = paths;
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
        FetchYearCommand = new RelayCommand(async _ => await RunFetchYearAsync(), _ => !IsBusy);
        StopCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        RetryFailedCommand = new RelayCommand(async _ => await RunRetryFailedAsync(), _ => !IsBusy && FailedCodeCount > 0);
        FetchBoardsCommand = new RelayCommand(async _ => await RunFetchBoardsAsync(), _ => !IsBusy);
        BackfillAmountTurnoverCommand = new RelayCommand(async _ => await RunBackfillAmountTurnoverAsync(), _ => !IsBusy);
        UploadBaselineCommand = new RelayCommand(async _ => await RunUploadAsync(baseline: true), _ => !IsBusy);
        UploadDailyCommand = new RelayCommand(async _ => await RunUploadAsync(baseline: false), _ => !IsBusy);
        BackfillDailyCommand = new RelayCommand(async _ => await RunBackfillDailyHistoryAsync(), _ => !IsBusy);
        FetchPeriodicCommand = new RelayCommand(async _ => await RunFetchPeriodicAsync(), _ => !IsBusy);
        FetchFinancialsCommand = new RelayCommand(async _ => await RunFetchFinancialsAsync(), _ => !IsBusy);
        FetchDividendCommand = new RelayCommand(async _ => await RunFetchDividendAsync(), _ => !IsBusy);
        ScheduledFetchAllCommand = new RelayCommand(async _ => await RunScheduledFetchAllAsync(), _ => !IsBusy);
        ScheduledFetchDayCommand = new RelayCommand(async _ => await RunScheduledFetchDayAsync(), _ => !IsBusy);

        RefreshDataStatus();
        RefreshFailedCodeCount();
    }

    /// <summary>把数据上传到 GitHub Releases（见 GitHubUploadService）——baseline=true 传全量基线
    /// （偶尔一次），false 传当天增量（每天）。上传日期用本地库最新的那一天。token 缺失时给出明确
    /// 提示，不弹异常。</summary>
    private async Task RunUploadAsync(bool baseline)
    {
        var name = baseline ? "上传全量基线" : "上传当天增量";
        var svc = new GitHubUploadService(_paths);
        var token = svc.ReadToken();
        if (token == null)
        {
            Log($"没有配置 GitHub token，无法上传。请在 {_paths.GitHubTokenPath} 里放一行 PAT（需要对本仓库有 Contents 写权限），再点上传。");
            return;
        }
        var latest = _orchestrator.GetDataStatus().LatestDay;
        if (latest == null)
        {
            Log("本地还没有数据，无法上传。");
            return;
        }

        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        Log($"===== 【{name}】开始 =====");
        try
        {
            var progress = new Progress<string>(Log);
            if (baseline)
                await svc.UploadBaselineAsync(token, latest.Value, progress, _cts.Token);
            else
                await svc.UploadDailyAsync(token, latest.Value, progress, _cts.Token);
        }
        catch (OperationCanceledException) { Log($"【{name}】已停止（用户手动取消）"); }
        catch (Exception ex) { Log($"【{name}】失败：{ex.Message}"); }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            Log($"===== 【{name}】结束，不会自动继续，需要再次操作请重新点击按钮 =====");
        }
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

    /// <summary>所有"点按钮跑一个操作"的统一外壳——置忙/心跳、**开始与结束都打印带功能名的醒目标记**、
    /// 取消与异常处理、收尾刷新。<paramref name="name"/> 是功能名（如"拉取全部"）；action 返回的
    /// FetchResult 里的错误逐条记日志。这样每个功能开始/结束在日志里都能一眼看出是哪个。</summary>
    private async Task RunOperationAsync(string name, Func<IProgress<string>, CancellationToken, Task<FetchResult>> action)
    {
        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        Log($"===== 【{name}】开始 =====");
        try
        {
            var progress = new Progress<string>(Log);
            var result = await action(progress, _cts.Token);
            foreach (var err in result.Errors) Log($"错误：{err}");
        }
        catch (OperationCanceledException)
        {
            Log($"【{name}】已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"【{name}】失败：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshDataStatus();
            RefreshFailedCodeCount();
            IsBusy = false;
            Log($"===== 【{name}】结束，不会自动继续，需要再次操作请重新点击按钮 =====");
        }
    }

    private Task RunFetchAsync()
    {
        if (!int.TryParse(LookbackYearsText.Trim(), out var lookbackYears) || lookbackYears <= 0)
        {
            Log($"回看年数不对：\"{LookbackYearsText}\"，请填一个正整数（例如 3）");
            return Task.CompletedTask;
        }
        return RunOperationAsync("拉取全部",
            (progress, ct) => _orchestrator.RunFetchAsync(SelectedSource, lookbackYears, ParseAnnouncementKeywords(), progress, ct));
    }

    private Task RunFetchDayAsync()
    {
        if (!DateOnly.TryParseExact(FetchDayText.Trim(), "yyyy-MM-dd", out var date))
        {
            Log($"日期格式不对：\"{FetchDayText}\"，请用 yyyy-MM-dd 格式（例如 2026-07-06）");
            return Task.CompletedTask;
        }
        return RunOperationAsync("补指定历史日",
            (progress, ct) => _orchestrator.RunFetchDayAsync(SelectedSource, date, ParseAnnouncementKeywords(), progress, ct));
    }

    /// <summary>解析界面上的年份区间两个输入框。结束年留空=从起始年补到现在；起止相同=只补那一年。
    /// 年份的合法范围（A股最早1990年、不能晚于今年、起≤止）由编排层校验，这里只做格式校验。</summary>
    private bool TryParseYearRange(out int startYear, out int endYear, out string label)
    {
        endYear = DateTime.Today.Year; // 结束年留空 → 一直补到现在
        label = "";
        if (!int.TryParse(FetchYearText.Trim(), out startYear))
        {
            Log($"起始年份格式不对：\"{FetchYearText}\"，请填4位年份（例如 {DateTime.Today.Year - 1}）");
            return false;
        }
        var endText = FetchYearEndText.Trim();
        if (endText.Length > 0 && !int.TryParse(endText, out endYear))
        {
            Log($"结束年份格式不对：\"{FetchYearEndText}\"，请填4位年份或留空（留空=补到现在）");
            return false;
        }
        label = startYear == endYear ? $"{startYear}年" : $"{startYear}~{endYear}年";
        return true;
    }

    /// <summary>"拉取指定年份区间"（见 FetchOrchestrator.RunFetchYearAsync）——把 [起始年,结束年] 里能取到
    /// 历史的各类数据一次补齐（K线/退市股/资金净流入/融资余额/龙虎榜/公告），只补本地还缺的部分，
    /// 可反复点、可随时停。</summary>
    private Task RunFetchYearAsync()
    {
        if (!TryParseYearRange(out var startYear, out var endYear, out var label)) return Task.CompletedTask;
        return RunOperationAsync($"拉取{label}数据" + (OverwriteQfq ? "(覆盖重抓前复权)" : ""),
            (progress, ct) => _orchestrator.RunFetchYearAsync(SelectedSource, startYear, endYear, ParseAnnouncementKeywords(), progress, ct, OverwriteQfq));
    }

    private Task RunFetchBoardsAsync() =>
        RunOperationAsync("拉取板块", (progress, ct) => _orchestrator.RunFetchBoardsAsync(progress, ct));

    /// <summary>一次性修复历史数据的回填（见 FetchOrchestrator.RunBackfillAmountTurnoverAsync）——界面已
    /// 移除按钮(全库成交额已补齐)，方法保留以备将来复用。幂等，可随时停止后再点、只会继续补还缺的。</summary>
    private Task RunBackfillAmountTurnoverAsync() =>
        RunOperationAsync("回填成交额/换手率", (progress, ct) => _orchestrator.RunBackfillAmountTurnoverAsync(SelectedSource, progress, ct));

    private Task RunRetryFailedAsync() =>
        RunOperationAsync("重新拉取失败股票", (progress, ct) => _orchestrator.RunRetryFailedAsync(SelectedSource, progress, ct));

    /// <summary>一键补齐每日历史（见 FetchOrchestrator.RunBackfillDailyHistoryAsync）——把融资余额、
    /// 龙虎榜的历史从 K线最早日补到今天、跳过本地已有的交易日。一次性用途，之后靠"拉取全部/当天"增量。</summary>
    private Task RunBackfillDailyHistoryAsync() =>
        RunOperationAsync("一键补齐每日历史", (progress, ct) => _orchestrator.RunBackfillDailyHistoryAsync(progress, ct));

    /// <summary>一键拉取定期数据（见 FetchOrchestrator.RunFetchPeriodicAsync）——依次跑指数成分/权重、
    /// 股东数据（较慢）。</summary>
    private Task RunFetchPeriodicAsync() =>
        RunOperationAsync("一键拉取定期数据", (progress, ct) => _orchestrator.RunFetchPeriodicAsync(progress, ct));

    /// <summary>拉取财务报表（见 FetchOrchestrator.RunFetchFinancialsAsync）——已并入"一键拉取定期数据"，
    /// 这个独立按钮给首次回补用：不用连带跑几小时的股东数据全量刷新。</summary>
    private Task RunFetchFinancialsAsync() =>
        RunOperationAsync("拉取财务报表", (progress, ct) => _orchestrator.RunFetchFinancialsAsync(progress, ct));

    /// <summary>拉取分红送配（见 FetchOrchestrator.RunFetchDividendAsync）——已并入"一键拉取定期数据"，
    /// 这个独立按钮给单独刷新分红用，不用连带跑几小时的其它定期数据。</summary>
    private Task RunFetchDividendAsync() =>
        RunOperationAsync("拉取分红送配", (progress, ct) => _orchestrator.RunFetchDividendAsync(progress, ct));

    /// <summary>定时拉取全部：点后等到"触发时间"再跑"拉取全部"（已过则立即）。参数在点击时先校验。</summary>
    private async Task RunScheduledFetchAllAsync()
    {
        if (!int.TryParse(LookbackYearsText.Trim(), out var lookbackYears) || lookbackYears <= 0)
        {
            Log($"回看年数不对：\"{LookbackYearsText}\"，请填一个正整数（例如 3）");
            return;
        }
        await RunScheduledAsync("拉取全部",
            (progress, ct) => _orchestrator.RunFetchAsync(SelectedSource, lookbackYears, ParseAnnouncementKeywords(), progress, ct));
    }

    /// <summary>定时补指定日（旧名"定时拉取当天"）：点后等到"触发时间"再跑"补指定历史日"（已过则立即）。
    /// 日期用"日期"框（默认今天）。日常无人值守请用"定时拉取全部"——只有它会自动补断档，耗时还一样，
    /// 见 FetchOrchestrator 类注释里 2026-07-31 的复核结论。</summary>
    private async Task RunScheduledFetchDayAsync()
    {
        if (!DateOnly.TryParseExact(FetchDayText.Trim(), "yyyy-MM-dd", out var date))
        {
            Log($"日期格式不对：\"{FetchDayText}\"，请用 yyyy-MM-dd 格式（例如 2026-07-06）");
            return;
        }
        await RunScheduledAsync("补指定历史日",
            (progress, ct) => _orchestrator.RunFetchDayAsync(SelectedSource, date, ParseAnnouncementKeywords(), progress, ct));
    }

    /// <summary>定时执行：点后等到 <see cref="ScheduleTimeText"/>(HH:mm) 再跑 action；点击时已过该时间则立即
    /// 跑。等待期间可点"停止"取消（等待和抓取共用同一个 CancellationToken）。</summary>
    private async Task RunScheduledAsync(string label, Func<IProgress<string>, CancellationToken, Task<FetchResult>> action)
    {
        if (!TimeOnly.TryParseExact(ScheduleTimeText.Trim(), "HH:mm", out var t))
        {
            Log($"触发时间格式不对：\"{ScheduleTimeText}\"，请用 HH:mm 格式（例如 18:00）");
            return;
        }

        IsBusy = true;
        StartHeartbeat();
        _cts = new CancellationTokenSource();
        try
        {
            var target = DateTime.Today.Add(t.ToTimeSpan());
            if (target > DateTime.Now)
            {
                var wait = target - DateTime.Now;
                Log($"已排定：等到 {target:HH:mm} 再开始【{label}】（还有约 {wait.TotalMinutes:F0} 分钟；等待期间可随时点\"停止\"取消）");
                await Task.Delay(wait, _cts.Token);
            }
            else
            {
                Log($"当前已过 {ScheduleTimeText}，立即开始【{label}】");
            }
            Log($"到点，开始【{label}】...");
            var progress = new Progress<string>(Log);
            var result = await action(progress, _cts.Token);
            foreach (var err in result.Errors) Log($"错误：{err}");
        }
        catch (OperationCanceledException)
        {
            Log($"【{label}】已停止（用户手动取消）");
        }
        catch (Exception ex)
        {
            Log($"定时【{label}】失败：{ex.Message}");
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            RefreshDataStatus();
            RefreshFailedCodeCount();
            IsBusy = false;
            Log($"===== 【{label}】结束，不会自动继续，需要再次操作请重新点击按钮 =====");
        }
    }
}
