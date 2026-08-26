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

    /// <summary>通知一个"算出来的"属性变了（它本身没有字段，跟着别的属性走）。</summary>
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

    /// <summary>还有哪些东西等着重试（见 FetchOrchestrator.GetFailedRetrySummary）——
    /// 只在 <see cref="HasFailed"/> 为真时"重新拉取失败股票"按钮才可点。
    ///
    /// 2026-08-19 由原来的"一个总数"改成分类汇总：流通市值是整轮扫描，失败时会把整批代码记进
    /// 名单，加总后按钮上会显示"（5547）"，被读成丢了5547只票的数据，实际只是一次市值快照没取到
    /// 外加3只资金流。现在按钮直接显示"K线 0 只 · 市值 1 轮 · 净流入 3 只"这样的分类文字。</summary>
    private FailedRetrySummary _failedRetry = new();
    public FailedRetrySummary FailedRetry
    {
        get => _failedRetry;
        private set
        {
            Set(ref _failedRetry, value);
            Raise(nameof(FailedRetryText));
            Raise(nameof(HasFailed));
        }
    }

    /// <summary>按钮上那行字。</summary>
    public string FailedRetryText => _failedRetry.Describe();

    public bool HasFailed => _failedRetry.Any;

    /// <summary>自动重试的状态文字（"将于 21:00 自动重试（…）"），空=当前没有排定。</summary>
    private string _autoRetryText = "";
    public string AutoRetryText
    {
        get => _autoRetryText;
        private set { Set(ref _autoRetryText, value); Raise(nameof(HasAutoRetry)); }
    }

    /// <summary>有没有排定中的自动重试（决定那行提示和"取消自动重试"按钮是否显示）。</summary>
    public bool HasAutoRetry => !string.IsNullOrEmpty(_autoRetryText);

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
    public RelayCommand BackfillDailyCommand { get; }
    public RelayCommand FetchPeriodicCommand { get; }
    public RelayCommand FetchFinancialsCommand { get; }
    public RelayCommand FetchDividendCommand { get; }
    public RelayCommand FetchShareholderCommand { get; }
    public RelayCommand FetchIndustryCommand { get; }
    public RelayCommand ScheduledFetchAllCommand { get; }
    public RelayCommand ScheduledFetchDayCommand { get; }
    public RelayCommand CancelAutoRetryCommand { get; }

    public MainViewModel(FetchPaths paths, FetchOrchestrator orchestrator, List<NamedBarSource> availableSources)
    {
        _orchestrator = orchestrator;
        _paths = paths;
        AvailableSources = availableSources;
        // Default to Tencent, not the first entry — EastMoney gets network-limited/blocked much
        // faster on some machines (see doc/data-platform-design.md), Tencent+新浪 has proven
        // stable in practice. Falls back to the first source if "Tencent" isn't in the list.
        _selectedSource = availableSources.FirstOrDefault(s => s.Name == "Tencent") ?? availableSources[0];

        // 每次程序启动开一份新的 fetch.log，但**上一轮那份先归档、不直接冲掉**（见
        // ArchivePreviousLog）。AutoFlush 让每行一写完就落盘，崩溃/被强制结束也不会丢最后那几行。
        ArchivePreviousLog(paths);
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
        // "停止"表达的是"别再跑了"，所以连排定中的自动重试一起取消，不然点了停止过一小时它又自己跑起来。
        StopCommand = new RelayCommand(_ => { _cts?.Cancel(); CancelAutoRetry("用户点了停止"); }, _ => IsBusy);
        RetryFailedCommand = new RelayCommand(async _ => await RunRetryFailedAsync(), _ => !IsBusy && HasFailed);
        FetchBoardsCommand = new RelayCommand(async _ => await RunFetchBoardsAsync(), _ => !IsBusy);
        BackfillDailyCommand = new RelayCommand(async _ => await RunBackfillDailyHistoryAsync(), _ => !IsBusy);
        FetchPeriodicCommand = new RelayCommand(async _ => await RunFetchPeriodicAsync(), _ => !IsBusy);
        FetchFinancialsCommand = new RelayCommand(async _ => await RunFetchFinancialsAsync(), _ => !IsBusy);
        FetchDividendCommand = new RelayCommand(async _ => await RunFetchDividendAsync(), _ => !IsBusy);
        FetchShareholderCommand = new RelayCommand(async _ => await RunFetchShareholderAsync(), _ => !IsBusy);
        FetchIndustryCommand = new RelayCommand(async _ => await RunFetchIndustryAsync(), _ => !IsBusy);
        ScheduledFetchAllCommand = new RelayCommand(async _ => await RunScheduledFetchAllAsync(), _ => !IsBusy);
        ScheduledFetchDayCommand = new RelayCommand(async _ => await RunScheduledFetchDayAsync(), _ => !IsBusy);
        CancelAutoRetryCommand = new RelayCommand(_ => CancelAutoRetry("用户手动取消"), _ => HasAutoRetry);

        RefreshDataStatus();
        RefreshFailedCodeCount();
    }

    /// <summary>归档日志保留份数——每天抓一轮的话约两个月。</summary>
    private const int LogArchiveKeep = 60;

    /// <summary>把上一次运行留下的 fetch.log 挪到 <see cref="FetchPaths.LogArchiveDir"/> 下，
    /// 文件名用它自己的最后写入时间（也就是上次那轮跑完的时刻），然后才让新的一轮从空文件开始。
    ///
    /// 为什么改成保留（2026-08-21）：原先是每次启动直接清空重写，理由是"这只是崩溃时看现场用的
    /// 临时日志"。但真正要查的恰恰是**上一轮**——2026-08-20 那轮"拉取全部"有 3766 只个股没拿到
    /// 当天的前复权日线（数据源盘后更新有先后，请求时那些股票还没出当天数据，代码把这种情况算
    /// "抓到但为空"、不记失败），第二天早上一开程序，唯一能看出发生了什么的日志就被冲掉了。
    /// 空文件不归档；只保留最近 <see cref="LogArchiveKeep"/> 份，免得无限堆积。</summary>
    private static void ArchivePreviousLog(FetchPaths paths)
    {
        try
        {
            var log = paths.LogFilePath;
            if (!File.Exists(log) || new FileInfo(log).Length == 0) return;

            Directory.CreateDirectory(paths.LogArchiveDir);
            var stamp = File.GetLastWriteTime(log).ToString("yyyyMMdd-HHmmss");
            var dest = Path.Combine(paths.LogArchiveDir, $"fetch-{stamp}.log");
            // 同名（同一秒）已经有了就不动，让旧的那份留着、这份被下面的新日志覆盖掉即可。
            if (!File.Exists(dest)) File.Move(log, dest);

            // 文件名本身就是时间戳，按名倒序=按时间倒序，留最近的 N 份。
            foreach (var old in new DirectoryInfo(paths.LogArchiveDir)
                         .GetFiles("fetch-*.log")
                         .OrderByDescending(f => f.Name)
                         .Skip(LogArchiveKeep))
                old.Delete();
        }
        catch
        {
            // 归档失败（占用/权限）不该阻止程序启动，照常开新日志就行。
        }
    }

    private List<string> ParseAnnouncementKeywords() =>
        AnnouncementKeywordsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// 刷新"本地数据覆盖：X 至 Y"那行状态文字——**放到后台线程跑**（2026-08-04改）。
    ///
    /// 原因：它要在 Bar 表上求全库最早/最晚交易日，而 Bar 的主键是 (code, granularity, period_start)、
    /// 前导列不是 granularity，MIN/MAX 用不上有序性，只能把整个主键覆盖索引扫完。库涨到 7GB 后实测：
    /// 暖状态约 3 秒，**冷启动（开机后首次读这个文件）实测整个窗口要等 39 秒才出现**——用户以为没启动
    /// 就重复双击，开出好几个 Fetcher，两个实例同时抓取会往同一个 SQLite 写、互相锁表。
    ///
    /// 改法：窗口先出来（构造函数里不再等它），这行字先显示"正在统计…"，算完自己刷上去。抓取按钮
    /// 不依赖这行字，所以**不需要锁住界面**。同时把原来分两次的 MIN/MAX 合成一次扫描（省 41%，见
    /// SqliteBarRepository.GetOverallPeriodStartRange）。另外也加了单实例守卫（SingleInstanceGuard）。
    ///
    /// 没有改成"给 Bar 建 (granularity, period_start) 索引"：给 7GB 的表新建索引本身要跑几分钟、
    /// 每次写入也会变慢，代价大于收益（用户 2026-08-04 确认按"先出界面"的思路解决）。
    /// </summary>
    private void RefreshDataStatus()
    {
        DataStatusText = "正在统计本地数据范围…（库较大，首次可能要数十秒；不影响下面的抓取按钮）";
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                var status = _orchestrator.GetDataStatus();
                if (status.EarliestDay == null || status.LatestDay == null)
                {
                    text = "本地还没有任何K线数据";
                }
                else
                {
                    text = $"本地数据覆盖：{status.EarliestDay:yyyy-MM-dd} 至 {status.LatestDay:yyyy-MM-dd}";
                    if (status.LastFetchAt != null)
                        text += $"；上次抓取：{status.LastFetchAt:yyyy-MM-dd HH:mm}（{status.LastFetchKind}）";
                }
            }
            catch (Exception ex)
            {
                text = $"读取数据范围失败：{ex.Message}";
            }
            // DataStatusText 的 setter 触发 PropertyChanged → 必须回到 UI 线程更新绑定
            System.Windows.Application.Current?.Dispatcher.Invoke(() => DataStatusText = text);
        });
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogLines.Insert(0, line);
        _logFileWriter?.WriteLine(line);
    }

    private void RefreshFailedCodeCount() => FailedRetry = _orchestrator.GetFailedRetrySummary();

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

    /// <summary>自动重试最多连着做几轮——够把"数据源晚点才更新"这种情况磨平，又不会没完没了。</summary>
    private const int AutoRetryMaxRounds = 3;

    /// <summary>一轮跑完之后至少再等这么久才自动重试：数据源盘后是逐步更新的，立刻重抓大概率还是拿不到。</summary>
    private static readonly TimeSpan AutoRetryDelayAfterRun = TimeSpan.FromHours(1);

    /// <summary>自动重试不早于当天这个时刻——实测 19:00 开抓时个股当天前复权只到位约 1/3，21:00 之后才齐。</summary>
    private static readonly TimeOnly AutoRetryNotBefore = new(21, 0);

    /// <summary>到点时如果正忙（用户在手动跑别的），推迟这么久再看，不抢占。</summary>
    private static readonly TimeSpan AutoRetryBusyRecheck = TimeSpan.FromMinutes(10);

    private CancellationTokenSource? _autoRetryCts;
    private int _autoRetryRound;
    private int _autoRetryLastPending;

    /// <summary>待重试的总量（逐只那几类的股票数 + 市值那一轮算 1）——用来判断自动重试有没有进展。</summary>
    private int PendingRetryCount() => _failedRetry.PerStockTotal + (_failedRetry.MarketCapPending ? 1 : 0);

    /// <summary>
    /// 每个操作跑完后决定要不要排一次自动重试（2026-08-21新增）。
    ///
    /// 为什么要有它：待重试的东西以前只能靠人看见按钮上的数字、然后手动点。而最需要重试的那一类
    /// （数据源盘后还没更新到的当天日线，见 FetchOrchestrator.CheckLatestDayCoverage）恰恰是
    /// "现在重试也没用、过一会儿才有"——2026-08-20 那轮 19:00 开抓，个股当天前复权只到位 1773/5539，
    /// 而 21:00 之后才抓的后复权和 ETF 一个不缺。所以自动重试的时机取
    /// <c>max(跑完 + 1小时, 当天 21:00)</c>：前者给数据源留出更新时间，后者保证不会在傍晚白跑一轮。
    ///
    /// 等待期间**不占用界面**（不置 IsBusy），用户照常能手动操作；到点如果正忙就顺延，不抢占。
    /// 停止条件三个：名单清零、连做满 AutoRetryMaxRounds 轮、或某一轮之后待重试数量没有减少
    /// （剩下的多半是当天停牌、数据源确实没有那天数据的票，再试也是白试）。
    /// </summary>
    private void ScheduleAutoRetry()
    {
        CancelAutoRetryTimer();

        if (!HasFailed)
        {
            if (_autoRetryRound > 0) Log("自动重试：待重试名单已清零，不再继续。");
            _autoRetryRound = 0;
            AutoRetryText = "";
            return;
        }

        int pending = PendingRetryCount();
        if (_autoRetryRound > 0 && pending >= _autoRetryLastPending)
        {
            Log($"自动重试：这一轮之后待重试数量没有减少（{_autoRetryLastPending} → {pending}），停止自动重试"
              + "——剩下的多半是当天停牌、或数据源确实没有那天数据的票，需要人工判断。");
            _autoRetryRound = 0;
            AutoRetryText = "";
            return;
        }

        if (_autoRetryRound >= AutoRetryMaxRounds)
        {
            Log($"自动重试：已经连着自动重试 {_autoRetryRound} 轮，仍有 {FailedRetryText} 没补齐，不再自动继续"
              + "——需要的话请手动点\"重新拉取失败股票\"。");
            _autoRetryRound = 0;
            AutoRetryText = "";
            return;
        }

        _autoRetryLastPending = pending;
        var at = ComputeAutoRetryTime();
        _autoRetryCts = new CancellationTokenSource();
        AutoRetryText = $"将于 {at:HH:mm} 自动重试（{FailedRetryText}）";
        Log($"已排定自动重试：{at:MM-dd HH:mm}（{FailedRetryText}）——数据源盘后是逐步更新的，"
          + "隔一会儿再抓才补得到；不想等可以点\"取消自动重试\"。");
        _ = RunAutoRetryWhenDueAsync(at, _autoRetryCts.Token);
    }

    /// <summary>自动重试的触发时刻：<c>max(现在 + 1小时, 当天 21:00)</c>（见 ScheduleAutoRetry）。</summary>
    private static DateTime ComputeAutoRetryTime()
    {
        var earliest = DateTime.Now + AutoRetryDelayAfterRun;
        var notBefore = DateTime.Today.Add(AutoRetryNotBefore.ToTimeSpan());
        return earliest > notBefore ? earliest : notBefore;
    }

    private async Task RunAutoRetryWhenDueAsync(DateTime at, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var wait = at - DateTime.Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                ct.ThrowIfCancellationRequested();
                if (!IsBusy) break;

                // 用户正在手动跑别的操作，不抢——往后挪一点再看。
                at = DateTime.Now + AutoRetryBusyRecheck;
                AutoRetryText = $"正忙，改到 {at:HH:mm} 再自动重试（{FailedRetryText}）";
            }

            _autoRetryRound++;
            AutoRetryText = "";
            Log($"===== 自动重试（第 {_autoRetryRound}/{AutoRetryMaxRounds} 轮）到点，开始 =====");
            // 跑完之后 RunOperationAsync 的收尾会再调一次 ScheduleAutoRetry，由它决定要不要续下一轮。
            await RunRetryFailedAsync();
        }
        catch (OperationCanceledException)
        {
            // 被取消（用户点了停止/取消自动重试，或程序退出）——什么都不用做。
        }
    }

    /// <summary>取消排定中的自动重试并说明原因；<paramref name="reason"/> 为 null 时不写日志。</summary>
    private void CancelAutoRetry(string? reason)
    {
        if (!HasAutoRetry && _autoRetryCts == null) return;
        CancelAutoRetryTimer();
        AutoRetryText = "";
        _autoRetryRound = 0;
        if (reason != null) Log($"已取消排定中的自动重试（{reason}）。");
    }

    private void CancelAutoRetryTimer()
    {
        _autoRetryCts?.Cancel();
        _autoRetryCts?.Dispose();
        _autoRetryCts = null;
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
            Log($"===== 【{name}】结束 =====");
            // 还有没补齐的东西就排一次自动重试（时机与停止条件见 ScheduleAutoRetry）。
            ScheduleAutoRetry();
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

    // 【已移除】RunBackfillAmountTurnoverAsync 的 ViewModel 包装和 BackfillAmountTurnoverCommand
    // （2026-08-15）：界面上早就没有对应按钮，这个 Command 属性没有任何 XAML 绑定，属于死代码。
    // 回填任务本身也确实做完了——全库日线 1291 万行里 amount 为0的只剩 19 行(0.0%)；turnover 的
    // 7.3% 空值全部集中在板块指数(57万行)和ETF/指数(37万行)上，它们本来就没有换手率概念，个股只有 280 行。
    // 编排层的 FetchOrchestrator.RunBackfillAmountTurnoverAsync 按原注释保留、以备将来复用，
    // 要重新启用时在这里加回四行包装即可。

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

    /// <summary>拉取股东数据（见 FetchOrchestrator.RunFetchShareholderAsync）——2026-08-15 补的独立按钮。
    /// 它本来只存在于"一键拉取定期数据"的链条里、而且排在第3位（行业分类 → 指数成分/权重 → 股东数据
    /// → 财务报表 → 分红），前两步就要跑很久，导致想单独刷新股东数据时**实际上没有办法**——
    /// 修完解析bug后重抓那次就卡在这里：用户点了定期数据但没等到第3步，数据一行都没更新。
    /// 财务报表/分红/行业分类早就各有独立按钮，股东数据漏了，这里补上。</summary>
    private Task RunFetchShareholderAsync() =>
        RunOperationAsync("拉取股东数据", (progress, ct) => _orchestrator.RunFetchShareholderAsync(progress, ct));

    /// <summary>拉取行业分类（见 FetchOrchestrator.RunFetchIndustryAsync）——已并入"一键拉取定期数据"，
    /// 独立按钮给单独刷新用。只要一两分钟。</summary>
    private Task RunFetchIndustryAsync() =>
        RunOperationAsync("拉取行业分类", (progress, ct) => _orchestrator.RunFetchIndustryAsync(progress, ct));

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
            Log($"===== 【{label}】结束 =====");
            ScheduleAutoRetry();
        }
    }
}
