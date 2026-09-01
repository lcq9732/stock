using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using StockPlatform.Data.Orchestration;
using StockPlatform.Fetcher.Planning;
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
    /// <summary>
    /// 抓 K 线用哪家（2026-08-31 起**不再是界面上的选项**）。
    ///
    /// 为什么撤掉那个下拉：默认的 "Tencent" 本身就是"腾讯为主、单只拿不到时自动回退新浪"，
    /// 实际用下来一年没切过；EastMoney 在这台机器上基本连不上。一个从不动的下拉却占着
    /// 每个页面最显眼的位置，不如收起来。
    ///
    /// 但**切换能力必须留着**——万一腾讯整体不可用（接口改版、被封），逐只回退新浪会慢到不可用，
    /// 这时要能整体切到纯新浪。所以改成读 data/fetcher-settings.json 里的 <c>BarSource</c>：
    /// 填 "Sina" 或 "EastMoney" 就换源，不用改代码重新发布。启动时日志里会说明当前用的是哪个。
    /// </summary>
    public NamedBarSource SelectedSource { get => _selectedSource; private set => Set(ref _selectedSource, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { Set(ref _isBusy, value); Raise(nameof(BusyText)); }
    }

    /// <summary>状态行开头那句。以前直接绑 IsBusy 显示成"运行中：False"，看着像出了错。</summary>
    public string BusyText => IsBusy ? "运行中" : "空闲";

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

    private int _pendingQfqRepair;
    /// <summary>
    /// 还有多少只股票等着重取前复权——除权之后数据源的前复权基准就变了，那只票的全部历史都得
    /// 按新基准整段重写。日常抓取顺带检出来记进名单，计划里的【重取前复权】慢慢补。
    /// 显示在计划表那一行的参数格里。
    /// </summary>
    public int PendingQfqRepair
    {
        get => _pendingQfqRepair;
        private set { Set(ref _pendingQfqRepair, value); Raise(nameof(PendingQfqRepairText)); }
    }

    public string PendingQfqRepairText =>
        PendingQfqRepair > 0 ? $"待重取 {PendingQfqRepair} 只" : "无待重取";

    private int _pendingRawBars;
    /// <summary>还差多少只个股没补不复权日线。</summary>
    public int PendingRawBars
    {
        get => _pendingRawBars;
        private set { Set(ref _pendingRawBars, value); Raise(nameof(PendingRawBarsText)); }
    }
    public string PendingRawBarsText => PendingRawBars > 0 ? $"待补 {PendingRawBars} 只" : "已补齐";

    private int _pendingAdjRebuild;
    /// <summary>还有多少只个股的回测序列要重算（不复权比它新，或者还没算过）。</summary>
    public int PendingAdjRebuild
    {
        get => _pendingAdjRebuild;
        private set { Set(ref _pendingAdjRebuild, value); Raise(nameof(PendingAdjRebuildText)); }
    }
    public string PendingAdjRebuildText => PendingAdjRebuild > 0 ? $"待重算 {PendingAdjRebuild} 只" : "已是最新";

    private int _pendingFinancials;
    /// <summary>还有多少只股票的财务报表没补（报告期落后、或科目集版本落后于当前 v4）。</summary>
    public int PendingFinancials
    {
        get => _pendingFinancials;
        private set { Set(ref _pendingFinancials, value); Raise(nameof(PendingFinancialsText)); }
    }
    public string PendingFinancialsText => PendingFinancials > 0 ? $"待补 {PendingFinancials} 只" : "已补齐";

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

    // ── 空闲时自动补财务数据（2026-08-27 按用户要求）──
    //
    // 为什么需要它：新浪的 vDOWN 报表接口配额很严（实测 1.1 请求/秒跑到 100 多个就被 HTTP 456
    // 封约 40 分钟），降速到约 10 请求/分钟后，全市场 5780 只 × 3 请求要跨天才能补完。让程序在
    // 空着的时候自己一轮一轮往下补，比人守着点按钮现实得多。
    // 断点续传由 FinancialFetchState 保证（记了报告期和科目集版本），所以中间随便停、随便关程序。


    /// <summary>
    /// 定时任务正在等待的触发时刻（<see cref="RunScheduledAsync"/> 排定后设，开跑或取消时清）。
    ///
    /// 为什么需要它：等定时触发的那段时间里 <see cref="IsBusy"/> 是 true（那个方法一进来就设了，
    /// 等待和执行共用同一个标记），但那段时间**一个网络请求都没发，是真空闲**。
    /// 2026-08-27 实测就因为这个，挂着"18:00 自动拉取全部"时【空闲时自动补财务】永远不触发。
    /// </summary>
    private DateTime? _scheduledStartAt;

    /// <summary>到定时时刻之前要留的余量——【定时拉取全部】那类等待用。</summary>
    private static readonly TimeSpan ScheduleSafetyMargin = TimeSpan.FromMinutes(5);

    // ── 【空闲时自动补财务】这个复选框没了（2026-08-31）────────────────────────────
    // 它其实就是一种触发方式，却单独长在【手动】页上，跟计划里那套重复规则各说各话。
    // 现在并成了计划里的一种重复规则「空闲时」：勾上财务报表那一行、重复选「空闲时」，
    // 效果完全一样——程序空着就补一批、到点前自动给定时任务让路、跑完歇一会儿再来。
    // 原来那些常量（冷却 20 分钟、安全余量 5 分钟、每只约 18 秒）都搬进了 PlanRunner
    // 和下面的 IdleRunDeadlineToCount。老设置 fetcher-settings.json 里的开关会自动迁移。
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
    public RelayCommand OptimizeDatabaseCommand { get; }
    public RelayCommand FetchBankRegulatoryCommand { get; }
    public RelayCommand ImportManualMetricsCommand { get; }
    public RelayCommand ScheduledFetchAllCommand { get; }
    public RelayCommand ScheduledFetchDayCommand { get; }
    public RelayCommand CancelAutoRetryCommand { get; }

    // ── 计划（2026-08-31）──
    public RelayCommand StartPlanCommand { get; }
    public RelayCommand StopPlanCommand { get; }
    public RelayCommand RunPlanItemNowCommand { get; }

    public MainViewModel(FetchPaths paths, FetchOrchestrator orchestrator, List<NamedBarSource> availableSources)
    {
        _orchestrator = orchestrator;
        _paths = paths;
        AvailableSources = availableSources;
        // Default to Tencent, not the first entry — EastMoney gets network-limited/blocked much
        // faster on some machines (see doc/data-platform-design.md), Tencent+新浪 has proven
        // stable in practice. Falls back to the first source if "Tencent" isn't in the list.
        _selectedSource = ResolveBarSource(availableSources);

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
        // 【停止】同时取消定时/常规抓取和空闲自动补——后者用的是自己的 CTS（不动 IsBusy），
        // 计划也归它管：等下一项到点的那段时间 IsBusy=false，不把 IsPlanRunning 算进来的话
        // 按钮是灰的、用户没法停一个正在等待的计划。"停止"表达的是"别再跑了"，就该停到底。
        StopCommand = new RelayCommand(
            _ =>
            {
                if (IsPlanRunning) StopPlan("用户点了停止");
                _cts?.Cancel();
                CancelAutoRetry("用户点了停止");
            },
            _ => IsBusy || IsPlanRunning);
        RetryFailedCommand = new RelayCommand(async _ => await RunRetryFailedAsync(), _ => !IsBusy && HasFailed);
        FetchBoardsCommand = new RelayCommand(async _ => await RunFetchBoardsAsync(), _ => !IsBusy);
        BackfillDailyCommand = new RelayCommand(async _ => await RunBackfillDailyHistoryAsync(), _ => !IsBusy);
        FetchPeriodicCommand = new RelayCommand(async _ => await RunFetchPeriodicAsync(), _ => !IsBusy);
        FetchFinancialsCommand = new RelayCommand(async _ => await RunFetchFinancialsAsync(), _ => !IsBusy);
        FetchDividendCommand = new RelayCommand(async _ => await RunFetchDividendAsync(), _ => !IsBusy);
        FetchShareholderCommand = new RelayCommand(async _ => await RunFetchShareholderAsync(), _ => !IsBusy);
        FetchIndustryCommand = new RelayCommand(async _ => await RunFetchIndustryAsync(), _ => !IsBusy);
        OptimizeDatabaseCommand = new RelayCommand(async _ => await RunOptimizeDatabaseAsync(), _ => !IsBusy);
        FetchBankRegulatoryCommand = new RelayCommand(async _ => await RunFetchBankRegulatoryAsync(), _ => !IsBusy);
        ImportManualMetricsCommand = new RelayCommand(async _ => await RunImportManualMetricsAsync(), _ => !IsBusy);
        ScheduledFetchAllCommand = new RelayCommand(async _ => await RunScheduledFetchAllAsync(), _ => !IsBusy);
        ScheduledFetchDayCommand = new RelayCommand(async _ => await RunScheduledFetchDayAsync(), _ => !IsBusy);
        CancelAutoRetryCommand = new RelayCommand(_ => CancelAutoRetry("用户手动取消"), _ => HasAutoRetry);

        StartPlanCommand = new RelayCommand(async _ => await StartPlanAsync(), _ => !IsPlanRunning);
        StopPlanCommand = new RelayCommand(_ => StopPlan("用户点了停止计划"), _ => IsPlanRunning);
        // 每行一个【执行】按钮，参数就是那一行——比"先选中再点右边的按钮"少一步
        RunPlanItemNowCommand = new RelayCommand(
            async p => await RunPlanItemNowAsync(p as PlanItemViewModel),
            p => p is PlanItemViewModel && !IsBusy && !IsPlanRunning);

        RefreshDataStatus();
        RefreshFailedCodeCount();
        LoadPlan();
        // 界面上没有数据源选项了，那就在日志里说清楚这一轮用的是哪个、怎么换（见 SelectedSource）
        Log($"K线数据源：{SelectedSource.Name}"
          + (SelectedSource.Name == "Tencent" ? "（腾讯为主，单只拿不到时自动回退新浪）" : "")
          + "。要换源改 data/fetcher-settings.json 里的 BarSource。");
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

    /// <summary>
    /// 改某一行的状态文字。<see cref="ExecutePlanItemAsync"/> 可能跑在后台线程上，
    /// 所以统一切回 UI 线程再动绑定属性。
    /// </summary>
    private static void SetRowState(PlanItemViewModel? row, string text, int level)
    {
        if (row == null) return;
        OnUi(() => { row.StatusText = text; row.StatusLevel = level; });
    }

    /// <summary>切到 UI 线程执行——调用方可能在后台线程上。</summary>
    private static void OnUi(Action action)
        => System.Windows.Application.Current?.Dispatcher.Invoke(action);

    private int _refreshingCounts;

    /// <summary>
    /// 刷新表格里那几个"还差多少只"的计数。**必须在后台线程跑**。
    ///
    /// ⚠ 这几个查询一点都不轻：GetPendingRawBarCount / GetPendingAdjRebuildCount 各要对
    /// Bar 表（1300 万行）做四遍全表 GROUP BY，GetFinancialFetchPlan 还要扫 FinancialReport。
    /// 实测单跑一轮十几秒，抓取正在写库、抢着 SQLite 锁的时候更久。
    /// 原来这里是同步调用的，于是用户点【停止计划】之后整个界面冻住、看着像卡死
    /// （2026-09-01 反馈："点了停止计划，程序会卡下，显示没反应"）。
    ///
    /// _refreshingCounts 是个防重入闸：这几个入口（启动、每轮抓完、停止）可能挨得很近，
    /// 上一轮还没算完就再开一轮，只会让 SQLite 锁竞争更糟。
    /// </summary>
    private void RefreshFailedCodeCount()
    {
        if (Interlocked.Exchange(ref _refreshingCounts, 1) == 1) return;
        Task.Run(() =>
        {
            FailedRetrySummary? failed = null;
            int qfq = 0, raw = 0, adj = 0, fin = 0;
            try { failed = _orchestrator.GetFailedRetrySummary(); } catch { }
            // 待重取前复权的计数跟失败名单同源（都在 manifest.json 里），一起刷新
            try { qfq = _orchestrator.GetPendingQfqRepairCount(); } catch { /* 只是个计数 */ }
            try { raw = _orchestrator.GetPendingRawBarCount(); } catch { }
            try { adj = _orchestrator.GetPendingAdjRebuildCount(); } catch { }
            try { fin = _orchestrator.GetFinancialFetchPlan().AllPending.Count; } catch { }
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (failed is not null) FailedRetry = failed;
                PendingQfqRepair = qfq;
                PendingRawBars = raw;
                PendingAdjRebuild = adj;
                PendingFinancials = fin;
            });
            Interlocked.Exchange(ref _refreshingCounts, 0);
        });
    }

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

    // ── 空闲自动补的定时器 ──

    // ── 界面设置的持久化 ──

    /// <summary>
    /// 定下这一次运行用哪个 K 线源：配置文件里指定了就用它，没有/认不出来就用腾讯。
    /// 见 <see cref="SelectedSource"/> 的说明——界面上没有这个选项了，改源靠改配置。
    /// </summary>
    private NamedBarSource ResolveBarSource(List<NamedBarSource> sources)
    {
        string? want = null;
        try
        {
            if (File.Exists(_paths.SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_paths.SettingsPath));
                if (doc.RootElement.TryGetProperty("BarSource", out var v) && v.ValueKind == JsonValueKind.String)
                    want = v.GetString();
            }
        }
        catch
        {
            // 配置读不了就用默认，不该拦住程序启动
        }

        var picked = sources.FirstOrDefault(
            s => string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase));
        if (picked != null) return picked;

        if (!string.IsNullOrWhiteSpace(want))
            Log($"⚠ 配置里的 BarSource=\"{want}\" 不认识，改用腾讯。可选：{string.Join(" / ", sources.Select(s => s.Name))}");
        return sources.FirstOrDefault(s => s.Name == "Tencent") ?? sources[0];
    }

    /// <summary>
    /// 读界面设置。现在这里只剩一件事：把**老版本的**「空闲时自动补财务」开关迁移进计划
    /// （2026-08-31）——那个复选框已经并成了计划里的重复规则「空闲时」，见 MigrateLegacyIdleSetting。
    /// </summary>
    private bool ReadLegacyIdleFinancialSetting()
    {
        try
        {
            if (!File.Exists(_paths.SettingsPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_paths.SettingsPath));
            return doc.RootElement.TryGetProperty("AutoFillFinancialsWhenIdle", out var v)
                && v.ValueKind is JsonValueKind.True && v.GetBoolean();
        }
        catch
        {
            return false;   // 设置文件坏了不影响程序启动
        }
    }

    /// <summary>迁移完就把老键去掉，免得下次启动又迁一遍、把用户后来的修改盖回去。</summary>
    private void ClearLegacyIdleFinancialSetting()
    {
        try
        {
            File.WriteAllText(_paths.SettingsPath,
                JsonSerializer.Serialize(new Dictionary<string, object>(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 清不掉只会导致下次启动多迁一次，不值得打扰用户
        }
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
            _scheduledStartAt = null;
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

    /// <summary>优化数据库（见 FetchOrchestrator.RunOptimizeDatabaseAsync）——纯本地维护、不联网，
    /// 给大表补建二级索引。一次性动作，建完就不用再点。</summary>
    private Task RunOptimizeDatabaseAsync() =>
        RunOperationAsync("优化数据库", (progress, ct) => _orchestrator.RunOptimizeDatabaseAsync(progress, ct));

    /// <summary>抓银行监管指标（见 FetchOrchestrator.RunFetchBankRegulatoryAsync）——下载年报/中报
    /// PDF 并解析不良率、拨备覆盖率、核心一级资本充足率等。前置：先跑过【拉取财务报表】。</summary>
    /// <summary>导入人工回填的监管指标（见 FetchOrchestrator.RunImportManualMetricsAsync）。</summary>
    private Task RunImportManualMetricsAsync() =>
        RunOperationAsync("导入手工数据",
            (progress, ct) => _orchestrator.RunImportManualMetricsAsync(progress, ct));

    private Task RunFetchBankRegulatoryAsync() =>
        RunOperationAsync("金融监管指标",
            // ⚠ 必须 Task.Run 推到线程池，不能直接 await 那个方法。
            // 它内部 await 之后紧跟着两段**同步重活**——重读全市场财务快照、重解析上百份 PDF
            // （CPU 密集）。WPF 下 await 的后续默认回到 DispatcherSynchronizationContext，
            // 也就是 UI 线程，于是界面整个假死：实测进程 CPU 满载、Responding=False，
            // 看起来像卡死，其实在正常干活。Task.Run 里没有同步上下文，await 之后继续留在
            // 线程池，界面就不受影响了。（【优化数据库】那个按钮本来就是这么写的。）
            (progress, ct) => Task.Run(
                () => _orchestrator.RunFetchBankRegulatoryAsync(progress, refetchAll: false, ct), ct));

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
                // 触发时刻仍然记着：手动页的定时按钮跟计划是两条路，记下来便于日志和排查。
                _scheduledStartAt = target;
                Log($"已排定：等到 {target:HH:mm} 再开始【{label}】（还有约 {wait.TotalMinutes:F0} 分钟；等待期间可随时点\"停止\"取消）");
                await Task.Delay(wait, _cts.Token);
            }
            else
            {
                Log($"当前已过 {ScheduleTimeText}，立即开始【{label}】");
            }
            _scheduledStartAt = null;
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

    // ══════════════════════════════════════════════════════════════════════════
    //  计划（2026-08-31 新增）
    //
    //  按你排好的顺序和时间把任务一项项跑掉，人不用守着。执行引擎在 PlanRunner，
    //  这里负责：界面状态、把计划项翻译成具体的编排层调用、以及跟手动操作互相让路。
    // ══════════════════════════════════════════════════════════════════════════

    private FetchPlan _plan = new();
    private FetchPlanStore? _planStore;
    private CancellationTokenSource? _planCts;
    private DispatcherTimer? _planTimer;

    /// <summary>计划里的任务项，顺序即执行顺序。</summary>
    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = [];

    private PlanItemViewModel? _selectedPlanItem;
    public PlanItemViewModel? SelectedPlanItem
    {
        get => _selectedPlanItem;
        set { Set(ref _selectedPlanItem, value); Raise(nameof(HasSelectedPlanItem)); }
    }

    public bool HasSelectedPlanItem => SelectedPlanItem != null;

    private bool _isPlanRunning;
    /// <summary>
    /// 计划引擎在跑（**含等待时间**）。注意它跟 <see cref="IsBusy"/> 是两件事：
    /// 等下一项到点的那段时间里 IsPlanRunning=true 而 IsBusy=false，此时没有任何网络请求，
    /// 【空闲时自动补财务】照样能利用这段空档，界面上的手动按钮也照常可点。
    /// </summary>
    public bool IsPlanRunning
    {
        get => _isPlanRunning;
        private set { Set(ref _isPlanRunning, value); Raise(nameof(PlanRunStateText)); }
    }

    private string _planStatusText = "";
    /// <summary>计划页顶部那行细节（在等谁、等到几点、正在跑什么）。左边那个 PlanRunStateText
    /// 已经说了"运行中/未运行"，这里就别再重复一遍。</summary>
    public string PlanStatusText { get => _planStatusText; private set => Set(ref _planStatusText, value); }

    public string PlanRunStateText => IsPlanRunning ? "计划执行中" : "计划未运行";

    /// <summary>程序一打开就自动开始执行计划（无人值守用）。存在 fetch-plan.json 里。</summary>
    public bool AutoStartPlanOnLaunch
    {
        get => _plan.AutoStartOnLaunch;
        set
        {
            if (_plan.AutoStartOnLaunch == value) return;
            _plan.AutoStartOnLaunch = value;
            Raise(nameof(AutoStartPlanOnLaunch));
            SavePlan();
        }
    }

    private void LoadPlan()
    {
        _planStore = new FetchPlanStore(_paths.PlanPath);
        _plan = _planStore.Load();
        MigrateLegacyIdleSetting();
        RebuildPlanItems();
        Raise(nameof(AutoStartPlanOnLaunch));

        // 每分钟重画一次时间轴：预计开始时刻是相对"现在"算的，不刷新的话看着会越来越不对。
        _planTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _planTimer.Tick += (_, _) => RecalcTimeline();
        _planTimer.Start();
        RecalcTimeline();

        if (_plan.AutoStartOnLaunch)
        {
            // 等界面真正显示出来再开始，别在构造函数里就发起网络请求
            Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(async () =>
                {
                    Log("检测到【程序启动后自动开始】已勾选，正在启动计划…");
                    await StartPlanAsync();
                }),
                DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// 把老版本的【空闲时自动补财务】开关迁进计划（2026-08-31，一次性）。
    ///
    /// 那个复选框已经并成了计划里的重复规则「空闲时」。老用户的 fetcher-settings.json 里
    /// 要是勾着，就把"拉取财务报表"那一行设成 启用 + 空闲时——保持他原来的行为，不用重新配。
    /// 迁完清掉老键，免得下次启动又把用户后来的修改盖回去。
    /// </summary>
    private void MigrateLegacyIdleSetting()
    {
        if (!ReadLegacyIdleFinancialSetting()) return;

        var fin = _plan.Items.FirstOrDefault(i => i.Action == FetchActionId.FetchFinancials);
        if (fin != null)
        {
            fin.Enabled = true;
            fin.Repeat = RepeatKind.WhenIdle;
            _planStore?.Save(_plan);
            Log("【空闲时自动补财务】这个开关已经并进计划了——"
              + "已把计划里的「拉取财务报表」设成 启用 + 重复「空闲时」，效果跟原来一样："
              + "程序空着就补一批、到点前自动给定时任务让路。以后在【计划】页调它就行。");
        }
        ClearLegacyIdleFinancialSetting();
    }

    private void RebuildPlanItems()
    {
        PlanItems.Clear();
        foreach (var item in _plan.Items)
            PlanItems.Add(new PlanItemViewModel(item, OnPlanItemEdited));
    }

    /// <summary>任何一项被改动（勾选/时间/重复/参数）都会走到这里：立刻存盘 + 重画时间轴。</summary>
    private void OnPlanItemEdited()
    {
        SavePlan();
        RecalcTimeline();
    }

    private void SavePlan()
    {
        // PlanItems 的顺序才是权威顺序（拖动排序改的是它），回写进 _plan 再存
        _plan.Items = PlanItems.Select(v => v.Model).ToList();
        _planStore?.Save(_plan);
    }

    /// <summary>
    /// 按顺序累加估时，算出每项**预计**什么时候跑——排计划时看得见会不会挤到一起。
    ///
    /// 只是给人看的估算：引擎永远是"上一项真的跑完了才开下一项"，这里估错了也不会导致抢跑。
    /// 已经跑过/没启用/今天不跑的项不占时间轴。
    /// </summary>
    /// <summary>
    /// 重算每一行的状态列。这一列**只讲执行**——在跑什么、跑成什么样、下一次几点跑：
    ///
    ///   正在跑 / 在等空隙   → PlanRunner 的回调实时写（这里不覆盖）
    ///   今天跑过了           → 结果和时刻
    ///   今天还要跑（定时项） → **预计**什么时候跑（按顺序累加各项估时，遇到"不早于"就等到那个点）
    ///   其余                 → 上一次跑的结果（跨天，带日期）；从没跑过就直说
    ///
    /// ⚠ 刻意**不**显示"未启用/手动执行/空闲时补/今天不跑"这类话。
    /// 那些是「启用」和「重复」两列的翻版——用户自己刚设的东西，一眼就看得出来，
    /// 这一列再复述一遍，就把真正要翻日志才知道的执行结果给挤没了（2026-09-01 用户反馈）。
    ///
    /// 预计只是给人排计划时看的估算，引擎永远是"上一项真跑完才开下一项"，估错了不会导致抢跑。
    /// </summary>
    private void RecalcTimeline()
    {
        var now = DateTime.Now;
        var cursor = now;
        foreach (var vm in PlanItems)
        {
            var m = vm.Model;

            // ① 正在跑/正在等的那行，PlanRunner 的回调刚写过，别覆盖；但时间轴要照样往后推
            if (vm.StatusLevel == 3)
            {
                if (m.Enabled && m.IsDueOn(now) && m.Repeat != RepeatKind.WhenIdle)
                    cursor = (cursor > now ? cursor : now) + vm.Info.Estimate;
                continue;
            }

            // ② 今天已经有结果了——这一列最该说的就是它（空闲项也一样，跑过就报结果）
            if (m.LastEnd?.Date == now.Date && m.LastOutcome != RunOutcome.None)
            {
                (vm.StatusText, vm.StatusLevel) = m.LastOutcome switch
                {
                    RunOutcome.Ok        => ($"✔ {m.LastEnd:HH:mm} 完成", 1),
                    RunOutcome.Failed    => ($"✘ {m.LastEnd:HH:mm} 失败", 2),
                    RunOutcome.Skipped   => ("⏭ 已跳过（前置失败）", 2),
                    RunOutcome.Cancelled => ($"⏹ {m.LastEnd:HH:mm} 被停止", 2),
                    _                    => ($"· {m.LastEnd:HH:mm}", 0),
                };
                continue;
            }

            // ③ 今天还要跑的**定时**项：算预计时段，并占掉时间轴。
            //    空闲项填的是别人不用的空隙，没有"预计几点跑"这回事，也不该占时间轴。
            if (m.Enabled && m.IsDueOn(now)
                && m.Repeat != RepeatKind.WhenIdle && m.Repeat != RepeatKind.Manual)
            {
                var earliest = m.NotBefore.HasValue ? now.Date.Add(m.NotBefore.Value.ToTimeSpan()) : cursor;
                var start = earliest > cursor ? earliest : cursor;
                var end = start + vm.Info.Estimate;
                vm.StatusText = $"{start:HH:mm} → {end:HH:mm}";
                vm.StatusLevel = 0;
                cursor = end;
                continue;
            }

            // ④ 今天不跑的（没启用、手动、空闲时、今天不是它的日子）：拿上一次的结果顶上。
            //    比"未启用"有用得多——那只票到底补没补过、上次是成是败，本来得翻日志才知道。
            (vm.StatusText, vm.StatusLevel) = LastRunBrief(m);
        }
    }

    /// <summary>跨天的"上一次跑成什么样"，给状态列兜底用。从没跑过就直说，别留空白。</summary>
    private static (string Text, int Level) LastRunBrief(FetchPlanItem m)
    {
        if (m.LastEnd is not { } end || m.LastOutcome == RunOutcome.None) return ("从未执行", 0);
        return m.LastOutcome switch
        {
            RunOutcome.Ok        => ($"上次 {end:MM-dd HH:mm} ✔", 0),
            RunOutcome.Failed    => ($"上次 {end:MM-dd HH:mm} ✘ 失败", 2),
            RunOutcome.Skipped   => ($"上次 {end:MM-dd HH:mm} ⏭ 跳过", 0),
            RunOutcome.Cancelled => ($"上次 {end:MM-dd HH:mm} ⏹ 被停止", 0),
            _                    => ($"上次 {end:MM-dd HH:mm}", 0),
        };
    }

    /// <summary>
    /// 拖放排序：把第 <paramref name="from"/> 行挪到第 <paramref name="to"/> 行的位置
    /// （2026-08-31 用户要求改成鼠标拖放，原来的上移/下移按钮撤掉了）。
    /// 拖动处理在 MainWindow.xaml.cs，那边只管算行号，顺序和存盘都在这里。
    /// </summary>
    public void MovePlanItem(int from, int to)
    {
        if (from < 0 || from >= PlanItems.Count) return;
        if (to < 0 || to >= PlanItems.Count || from == to) return;
        var vm = PlanItems[from];
        PlanItems.Move(from, to);
        SelectedPlanItem = vm;
        SavePlan();
        RecalcTimeline();
        Log($"计划顺序已调整：【{vm.Name}】移到第 {to + 1} 位。");
    }

    private async Task StartPlanAsync()
    {
        if (IsPlanRunning) return;
        SavePlan();
        _planCts = new CancellationTokenSource();
        IsPlanRunning = true;

        var runner = new PlanRunner(
            _plan, _planStore!, _paths,
            ExecutePlanItemAsync,
            Log,
            state =>
            {
                // PlanRunner 跑在后台线程，回调要切回 UI 线程再动集合和绑定属性。
                // ⚠ 这里**不能**用 Dispatcher.CurrentDispatcher：它返回的是"当前线程的"Dispatcher，
                //   在后台线程上拿到的是那个线程自己的（还没有消息循环在跑），委托就直接在
                //   后台线程上执行了，等于隔着线程改绑定属性。要 Application.Current.Dispatcher。
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    PlanStatusText = state.Text;
                    // 先把"进行中"的标记清掉，再给当前那一行打上——否则上一轮正在跑的那行会
                    // 一直挂着"执行中"，RecalcTimeline 认得 StatusLevel==3 就不覆盖它了。
                    foreach (var vm in PlanItems)
                    {
                        if (vm.StatusLevel == 3) vm.StatusLevel = 0;
                        if (state.Current == vm.Model)
                        {
                            vm.StatusText = "▶ 执行中…";
                            vm.StatusLevel = 3;
                        }
                        else if (state.Waiting == vm.Model && state.WaitUntil.HasValue)
                        {
                            vm.StatusText = $"⏳ 等 {state.WaitUntil:HH:mm}";
                            vm.StatusLevel = 3;
                        }
                        vm.RefreshStatus();
                    }
                    RecalcTimeline();
                });
            });

        try
        {
            await runner.RunAsync(_planCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常停止，PlanRunner 已经打过日志了
        }
        catch (Exception ex)
        {
            Log($"计划执行意外中止：{ex.Message}");
        }
        finally
        {
            IsPlanRunning = false;
            PlanStatusText = "";
            _planCts?.Dispose();
            _planCts = null;
            foreach (var vm in PlanItems) vm.RefreshStatus();
            RecalcTimeline();
        }
    }

    private void StopPlan(string why)
    {
        if (!IsPlanRunning) return;
        Log($"正在停止计划（{why}）——当前任务会被取消，后面的项不再执行。");
        _planCts?.Cancel();
        _cts?.Cancel();          // 正在跑的那个任务用的是它派生出来的 token
    }

    /// <summary>把选中的那一项**立刻**跑一次（不走队列，也不影响它的重复规则）。</summary>
    private async Task RunPlanItemNowAsync(PlanItemViewModel? vm)
    {
        if (vm == null) return;
        // 原来这里是**静默 return**：计划正在跑的时候点【执行】，什么都不发生、也不说为什么，
        // 用户只能反复点（2026-09-01 反馈）。现在至少说清楚。
        if (IsPlanRunning)
        {
            Log($"【{vm.Name}】没有执行：计划正在运行中。任务是严格串行的，要单独跑这一项请先点【停止计划】。");
            return;
        }
        if (IsBusy)
        {
            Log($"【{vm.Name}】没有执行：另一个任务正在跑，等它结束再点。");
            return;
        }

        Log($"===== 手动执行计划项【{vm.Name}】 =====");
        // 状态列立刻打上标记——有些任务开头要先扫库算待办量，几十秒不出声，
        // 没这个标记就看不出到底点没点上。
        vm.StatusText = "▶ 执行中…";
        vm.StatusLevel = 3;
        _planCts = new CancellationTokenSource();
        try
        {
            await ExecutePlanItemAsync(vm.Model, null, new Progress<string>(Log), _planCts.Token);
            vm.Model.LastEnd = DateTime.Now;
            vm.Model.LastOutcome = RunOutcome.Ok;
        }
        catch (OperationCanceledException) { vm.Model.LastOutcome = RunOutcome.Cancelled; }
        catch (Exception ex)
        {
            vm.Model.LastOutcome = RunOutcome.Failed;
            vm.Model.LastMessage = ex.Message;
            Log($"【{vm.Name}】失败：{ex.Message}");
        }
        finally
        {
            SavePlan();
            if (vm.StatusLevel == 3) vm.StatusLevel = 0;   // 让 RecalcTimeline 能接管这一行
            vm.RefreshStatus();
            RecalcTimeline();
            _planCts?.Dispose();
            _planCts = null;
            Log($"===== 计划项【{vm.Name}】结束 =====");
        }
    }

    /// <summary>
    /// 执行计划里的一项。这里负责的是**跟手动操作互相让路**，具体干活的是
    /// <see cref="DispatchPlanActionAsync"/>。
    ///
    /// 三件事按顺序做：
    ///   ① 手动任务正在跑就等它——两个抓取并发会同时争限流器、日志也会混成一团；
    ///   ② 【空闲时自动补财务】正在跑就先叫停它（它本来就是"捡空档跑"的，计划优先）；
    ///   ③ 置 IsBusy 保护整段执行，跑完清掉并照常排一次自动重试。
    /// </summary>
    private async Task<FetchResult> ExecutePlanItemAsync(
        FetchPlanItem item, DateTime? deadline, IProgress<string> progress, CancellationToken ct)
    {
        // PlanRunner 是**挑中**这一项就回调报告"当前项 = 它"的，而下面这个等待循环可能让它在这儿
        // 排上十几分钟。不区分的话状态列会写着「▶ 执行中…」，其实一个请求都还没发
        // （2026-09-01 用户反馈："这个其实是没开始做的，是在等前一个完成"）。
        var row = PlanItems.FirstOrDefault(x => ReferenceEquals(x.Model, item));
        bool warned = false;
        while (IsBusy && !ct.IsCancellationRequested)
        {
            if (!warned)
            {
                progress.Report("　有别的任务正在跑，这一项先排队等它结束（任务严格串行，不会并发抓取）…");
                SetRowState(row, "⏸ 排队等待", 3);
                // 顶上那条总状态也是 PlanRunner "挑中就报"的，同样会写成"正在执行"——改掉，
                // 否则界面说在跑财务报表、日志却在刷不复权历史的进度，对不上（2026-09-01 用户反馈）。
                OnUi(() => PlanStatusText = $"【{row?.Name ?? "下一项"}】排队中——等当前任务结束");
                warned = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        ct.ThrowIfCancellationRequested();

        if (warned)
        {
            progress.Report($"　前一个任务结束了，开始【{row?.Name ?? item.Action.ToString()}】。");
            OnUi(() => PlanStatusText = $"正在执行【{row?.Name ?? item.Action.ToString()}】");
        }
        SetRowState(row, "▶ 执行中…", 3);       // 到这儿才是真的开跑

        // 计划任务也走 _cts：【停止】按钮取消的就是它
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsBusy = true;
        StartHeartbeat();
        try
        {
            return await DispatchPlanActionAsync(item, deadline, progress, _cts.Token);
        }
        finally
        {
            StopHeartbeat();
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            RefreshDataStatus();
            RefreshFailedCodeCount();
            // 自动重试保持原样：它不占 IsBusy、到点忙就顺延，跟计划天然不打架，
            // 而它"等到当天 21:00 之后再试"的时机是计划排不出来的（数据源盘后逐步更新）。
            ScheduleAutoRetry();
        }
    }

    /// <summary>把计划项翻译成具体的编排层调用。参数不合法就抛异常——引擎会把这一项记成失败、继续后面的。</summary>
    private Task<FetchResult> DispatchPlanActionAsync(
        FetchPlanItem item, DateTime? deadline, IProgress<string> progress, CancellationToken ct)
    {
        switch (item.Action)
        {
            case FetchActionId.FetchAll:
                return _orchestrator.RunFetchAsync(
                    SelectedSource, ParseLookbackYears(item.LookbackYearsText),
                    ParseAnnouncementKeywords(), progress, ct);

            case FetchActionId.FetchDay:
            {
                // 计划项自己填了日期就用它，没填就用【手动】页上那个框（默认今天）
                var text = (item.DateText ?? FetchDayText).Trim();
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date))
                    throw new InvalidOperationException($"日期格式不对：\"{text}\"，要 yyyy-MM-dd");
                return _orchestrator.RunFetchDayAsync(
                    SelectedSource, date, ParseAnnouncementKeywords(), progress, ct);
            }

            case FetchActionId.RetryFailed:
                return _orchestrator.RunRetryFailedAsync(SelectedSource, progress, ct);

            case FetchActionId.FetchRawBars:
                // 一只补十年约 4 秒（多页），按空窗剩余时间估本轮补几只
                return _orchestrator.RunFetchRawBarsAsync(
                    SelectedSource, progress, ct, DeadlineToCount(deadline, TimeSpan.FromSeconds(4)));

            case FetchActionId.RebuildAdjSeries:
                // 纯本地计算，一只十年约 50 毫秒；给足余量按 0.2 秒/只估
                return _orchestrator.RunRebuildAdjSeriesAsync(
                    progress, ct, DeadlineToCount(deadline, TimeSpan.FromSeconds(0.2)));

            case FetchActionId.RepairQfq:
                // 一只票重抓十年约 4 秒（多页），按剩余时间估本轮能取几只，到点前收尾
                return _orchestrator.RunRepairQfqAsync(
                    SelectedSource, progress, ct, DeadlineToCount(deadline, TimeSpan.FromSeconds(4)));

            case FetchActionId.FetchBoards:
                return _orchestrator.RunFetchBoardsAsync(progress, ct);

            case FetchActionId.FetchIndustry:
                return _orchestrator.RunFetchIndustryAsync(progress, ct);

            case FetchActionId.FetchIndexCons:
                return _orchestrator.RunFetchIndexConsAsync(progress, ct);

            case FetchActionId.FetchShareholder:
                return _orchestrator.RunFetchShareholderAsync(progress, ct);

            case FetchActionId.FetchFinancials:
                return FetchFinancialsRoundAsync(deadline, progress, ct);

            case FetchActionId.FetchDividend:
                return _orchestrator.RunFetchDividendAsync(progress, ct);

            case FetchActionId.BankRegulatory:
                // ⚠ 必须推到线程池：这个方法 await 之后紧跟着两段同步重活（重读全市场财务快照、
                // 重解析上百份 PDF），留在 UI 线程会让界面假死。理由同 RunFetchBankRegulatoryAsync。
                return Task.Run(
                    () => _orchestrator.RunFetchBankRegulatoryAsync(progress, refetchAll: false, ct), ct);

            case FetchActionId.ImportManual:
                return _orchestrator.RunImportManualMetricsAsync(progress, ct);

            case FetchActionId.BackfillDaily:
                return _orchestrator.RunBackfillDailyHistoryAsync(progress, ct);

            case FetchActionId.FetchYear:
            {
                var startText = (item.YearStartText ?? FetchYearText).Trim();
                var endText = (item.YearEndText ?? FetchYearEndText).Trim();
                if (!int.TryParse(startText, out var startYear))
                    throw new InvalidOperationException($"起始年份格式不对：\"{startText}\"");
                int endYear = DateTime.Today.Year;
                if (endText.Length > 0 && !int.TryParse(endText, out endYear))
                    throw new InvalidOperationException($"结束年份格式不对：\"{endText}\"");
                return _orchestrator.RunFetchYearAsync(
                    SelectedSource, startYear, endYear, ParseAnnouncementKeywords(),
                    progress, ct, item.OverwriteQfq);
            }

            case FetchActionId.OptimizeDatabase:
                return _orchestrator.RunOptimizeDatabaseAsync(progress, ct);

            default:
                throw new InvalidOperationException($"还没实现的动作：{item.Action}");
        }
    }

    /// <summary>一只票大约要多久（实测约 18 秒：3 个请求 × 4 秒 + 每 30 请求歇 60 秒）。
    /// 按"到截止时刻还剩多少时间"估算本轮能抓几只时用。</summary>
    private static readonly TimeSpan PerStockEstimate = TimeSpan.FromSeconds(18);

    /// <summary>
    /// 「空闲时」那类任务塞进空窗时，本轮最多做几个——就是"到截止时刻还剩多少时间 ÷ 每个多久"。
    /// <paramref name="deadline"/> 为 null（今天没有后续定时任务了）就返回 null = 不限量。
    /// 至少给 1，免得算出 0 之后每轮都空跑。
    /// </summary>
    private static int? DeadlineToCount(DateTime? deadline, TimeSpan perItem)
    {
        if (!deadline.HasValue) return null;
        var usable = deadline.Value - DateTime.Now;
        if (usable <= TimeSpan.Zero) return 0;
        return Math.Max(1, (int)(usable.TotalSeconds / perItem.TotalSeconds));
    }

    /// <summary>
    /// 跑一轮财务报表。这是**唯一支持"只跑一部分"的动作**（见 FetchActionInfo.SupportsPartialRun）——
    /// 计划里把它设成重复「空闲时」之后，就靠这里在别人不用的时间见缝插针地补。
    ///
    /// 两件原来在【空闲时自动补财务】里、必须保住的事：
    ///   ① <paramref name="deadline"/> 是"最晚要结束的时刻"（后面还有定时任务时才有值）。
    ///      按剩余时间除以每只约 18 秒，算出本轮最多抓几只，到点前干净收尾——
    ///      不是抓一半被掐断（虽然那样也不丢数据，但会白白撞一次配额）。
    ///   ② 待抓清单为空时**不发任何请求**，直接标 NothingToDo 返回，让计划把下一轮推远一点
    ///      （不然全补齐之后还每 20 分钟查一遍几 GB 的库）。
    /// </summary>
    private async Task<FetchResult> FetchFinancialsRoundAsync(
        DateTime? deadline, IProgress<string> progress, CancellationToken ct)
    {
        var cap = DeadlineToCount(deadline, PerStockEstimate);
        if (deadline.HasValue && cap is null or <= 0) return new FetchResult { NothingToDo = true };

        int remaining;
        try
        {
            remaining = _orchestrator.GetFinancialFetchPlan().AllPending.Count;
        }
        catch (Exception ex)
        {
            progress.Report($"　查待抓清单失败（{ex.Message}），这一轮跳过。");
            return new FetchResult { NothingToDo = true };
        }

        if (remaining == 0)
        {
            progress.Report("　财务数据已全部补齐（报告期和科目集版本都是最新），这一轮无事可做。");
            return new FetchResult { NothingToDo = true };
        }

        progress.Report($"　还有 {remaining} 只待补，开始一轮"
            + (cap.HasValue ? $"（本轮限 {cap} 只——{deadline:HH:mm} 前要收尾）" : "")
            + "…（点【停止】可中断，已抓的不会白费）");
        return await _orchestrator.RunFetchFinancialsAsync(progress, ct, cap);
    }

    /// <summary>
    /// 回看年数：优先用计划里那一行填的，留空就用【手动】页那个框；都不成立时兜底 3 年——
    /// 计划是无人值守跑的，不能因为一个格式问题整轮不跑。
    ///
    /// ⚠ 这个值**只决定"本地一条K线都没有的标的第一次抓多久历史"**：已经抓过的永远从自己
    /// 上次抓到那天续，改大它不会让已有标的的历史往前延长（那要用「拉取区间数据」）。
    /// </summary>
    private int ParseLookbackYears(string? rowValue = null)
    {
        if (int.TryParse((rowValue ?? "").Trim(), out var fromRow) && fromRow > 0) return fromRow;
        return int.TryParse(LookbackYearsText.Trim(), out var y) && y > 0 ? y : 3;
    }
}
