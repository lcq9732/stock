using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Scheduling;
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
    /// <summary>
    /// 【手动】页那种横跨所有数据源的大任务正在跑。它只管手动页按钮的可用性——
    /// 界面上"在跑什么"已经由 <see cref="RunningTasks"/> 逐项显示了。
    ///
    /// ⚠ 它**不代表"程序忙不忙"**：并发跑的任务只登记在占用表里、不动这个标志
    /// （所以以前顶上那句"空闲"常常在有任务跑着的时候出现，2026-09-05 已经撤掉）。
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    private CancellationTokenSource? _cts;
    private readonly Services.WebView2JsonFetcher? _browserChannel;

    /// <summary>
    /// 照当时的配置文件重造一个板块成分股通道（App 传进来，见 <see cref="ReloadConfig"/>）。
    /// 造法留在 App 那边——限流参数、浏览器通道这些装配细节不该漏进 ViewModel。
    /// </summary>
    private readonly Func<IBoardFetcher>? _recreateBoardFetcher;
    private bool _verifyingEastMoney;
    private readonly StreamWriter? _logFileWriter;
    private readonly FetchPaths _paths;

    /// <summary>还有哪些东西等着重试（见 FetchOrchestrator.GetFailedRetrySummary）——
    /// 只在 <see cref="HasFailed"/> 为真时"重新拉取失败股票"按钮才可点。
    ///
    /// 2026-08-19 由原来的"一个总数"改成分类汇总：流通市值是整轮扫描，失败时会把整批代码记进
    /// 名单，加总后按钮上会显示"（5547）"，被读成丢了5547只票的数据，实际只是一次市值快照没取到
    /// 外加3只资金流。现在按钮直接显示"K线 0 只 · 市值 1 轮 · 净流入 3 只"这样的分类文字。</summary>
    // 初值是 null 而不是 new()：**"还没读到" 和 "读到了、是空的" 必须分得开**。
    // 用 new() 顶上会让统计失败时界面显示"无失败"，用户据此判断"不用管了"——而实际上
    // 名单可能有几千只没补（2026-09-03 用户："要不能用户无法判断数据是否取正确了"）。
    private FailedRetrySummary? _failedRetry;
    public FailedRetrySummary? FailedRetry
    {
        get => _failedRetry;
        private set
        {
            Set(ref _failedRetry, value);
            Raise(nameof(FailedRetryText));
            Raise(nameof(HasFailed));
        }
    }

    /// <summary>按钮上那行字。还没统计出来时明说，别冒充"无失败"。</summary>
    public string FailedRetryText => _failedRetry?.Describe() ?? "失败名单还没读到";

    private int _pendingQfqRepair = -1;   // -1 = 还没算出来，别跟"0 已补齐"混为一谈
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
        PendingQfqRepair < 0 ? "待重取数还没算出来"
        : PendingQfqRepair > 0 ? $"待重取 {PendingQfqRepair} 只" : "无待重取";

    private int _pendingRawBars = -1;   // -1 = 还没算出来，别跟"0 已补齐"混为一谈
    /// <summary>还差多少只个股没补不复权日线。</summary>
    public int PendingRawBars
    {
        get => _pendingRawBars;
        private set { Set(ref _pendingRawBars, value); Raise(nameof(PendingRawBarsText)); }
    }
    public string PendingRawBarsText =>
        PendingRawBars < 0 ? "待补数还没算出来"
        : PendingRawBars > 0 ? $"待补 {PendingRawBars} 只" : "已补齐";

    private int _pendingAdjRebuild = -1;   // -1 = 还没算出来，别跟"0 已补齐"混为一谈
    /// <summary>还有多少只个股的回测序列要重算（不复权比它新，或者还没算过）。</summary>
    public int PendingAdjRebuild
    {
        get => _pendingAdjRebuild;
        private set { Set(ref _pendingAdjRebuild, value); Raise(nameof(PendingAdjRebuildText)); }
    }
    public string PendingAdjRebuildText =>
        PendingAdjRebuild < 0 ? "待重算数还没算出来"
        : PendingAdjRebuild > 0 ? $"待重算 {PendingAdjRebuild} 只" : "已是最新";

    private (int Total, int NeedFill)? _manualFill;
    /// <summary>
    /// 「待手工回填清单」里还剩多少项要人动手（PDF 解析不出来的指标）。
    /// 摆在【导入手工数据】那一行——不显示的话用户根本不知道有活等着自己干
    /// （2026-09-03 用户："主要是让用户一眼知道有需要手工处理的数据"）。
    /// </summary>
    public (int Total, int NeedFill)? ManualFill
    {
        get => _manualFill;
        private set { Set(ref _manualFill, value); Raise(nameof(ManualFillText)); }
    }

    public string ManualFillText =>
        ManualFill is not { } m ? "待手工处理数还没算出来"
        : m.Total == 0 ? "没有待手工处理的"
        : m.NeedFill > 0 ? $"⚠ 有 {m.NeedFill} 项要手工填（共 {m.Total} 项）"
        : $"清单 {m.Total} 项都填好了，点「执行」导入";

    private int _pendingEarnings = -1;   // -1 = 还没算出来，别跟"0 已补齐"混为一谈
    /// <summary>还有多少只股票的本期财报没披露——就是下一轮要复查披露日的那批。</summary>
    public int PendingEarnings
    {
        get => _pendingEarnings;
        private set { Set(ref _pendingEarnings, value); Raise(nameof(PendingEarningsText)); }
    }
    public string PendingEarningsText =>
        PendingEarnings < 0 ? "待披露数还没算出来"
        : PendingEarnings > 0 ? $"待披露 {PendingEarnings} 只" : "本期已披露完";

    private (int Todo, int Never)? _pendingMoneyFlow;
    /// <summary>分档资金流还有多少只的 120 天历史没补齐。null = 还没算出来。</summary>
    public (int Todo, int Never)? PendingMoneyFlow
    {
        get => _pendingMoneyFlow;
        private set { Set(ref _pendingMoneyFlow, value); Raise(nameof(PendingMoneyFlowText)); }
    }

    /// <summary>
    /// 这一项的文案说的是**补历史那条路还剩多少活**（2026-09-06 改）。
    ///
    /// 原来写的是"今天待抓 N 只"，那是按"今天抓过没有"算的；自从加了全市场快照通道
    /// （每天一次、几十秒把当天全市场写全），每只票每天都被写过，那个数会永远显示 5900 只、
    /// 看着像永远落后，其实当天数据早就齐了。真正还差的只有历史：接口只给 120 个交易日，
    /// 只能一只只补，补齐一只就少一只，归零之后日常就全靠快照了。
    /// </summary>
    public string PendingMoneyFlowText =>
        PendingMoneyFlow is not { } m ? "待补数还没算出来"
        // 一轮还没铺开时这两个数常常相等（不齐的就是一行都没有的），相等就别重复报一遍
        : m.Never > 0 && m.Never == m.Todo ? $"还有 {m.Never} 只没有历史"
        : m.Never > 0 ? $"还有 {m.Todo} 只历史不全（其中 {m.Never} 只一行都没有）"
        : m.Todo > 0 ? $"还有 {m.Todo} 只历史不全"
        : "历史已补齐，日常走当日快照";

    private (int Todo, int Never, int Total)? _pendingBoardMembers;
    /// <summary>板块成分股还剩多少个板块要抓。null = 还没算出来。</summary>
    public (int Todo, int Never, int Total)? PendingBoardMembers
    {
        get => _pendingBoardMembers;
        private set { Set(ref _pendingBoardMembers, value); Raise(nameof(PendingBoardMembersText)); }
    }

    /// <summary>
    /// 这一项跟【分档资金流】一样是跨轮才做得完的活（1031 个板块 ≈ 2500 个 push2 请求），
    /// 但判据是"7 天内抓过没有"而不是"今天抓过没有"——所以补完一轮之后它**会**归零，
    /// 停在某个数不动才说明抓不动了，值得报出来。
    /// </summary>
    public string PendingBoardMembersText =>
        PendingBoardMembers is not { } b ? "待抓数还没算出来"
        : b.Todo == 0 ? $"{b.Total} 个板块都是最近抓的"
        : b.Never > 0 ? $"待抓 {b.Todo}/{b.Total} 个板块（{b.Never} 个从没抓过）"
        : $"待抓 {b.Todo}/{b.Total} 个板块";

    private int _pendingFinancials = -1;   // -1 = 还没算出来，别跟"0 已补齐"混为一谈
    /// <summary>还有多少只股票的财务报表没补（报告期落后、或科目集版本落后于当前 v4）。</summary>
    public int PendingFinancials
    {
        get => _pendingFinancials;
        private set { Set(ref _pendingFinancials, value); Raise(nameof(PendingFinancialsText)); }
    }
    public string PendingFinancialsText =>
        PendingFinancials < 0 ? "待补数还没算出来"
        : PendingFinancials > 0 ? $"待补 {PendingFinancials} 只" : "已补齐";

    // 名单还没读到（null）时按"没有失败"算：这个属性是【重新拉取失败】按钮的 CanExecute，
    // 界面一渲染就会调到，那会儿后台统计还没跑完。文字那边会照实说"失败名单还没读到"。
    public bool HasFailed => _failedRetry?.Any == true;

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

    /// <summary>
    /// 鼠标停在数据状态那行上显示的**逐项**明细（2026-09-02 新增）：每个任务上次什么时候跑完的。
    ///
    /// 拆细之后"上次抓取：xx（龙虎榜）"只说得清最后收尾的那一项，回答不了"名册今天刷新了没""
    /// 融资余额补到哪天了"——那些答案在这份明细里。
    /// </summary>
    private string _dataStatusDetail = "";
    public string DataStatusDetail { get => _dataStatusDetail; private set => Set(ref _dataStatusDetail, value); }

    /// <summary>把 manifest 里按任务记的运行时间排成给人看的多行文本（最多 15 行，太长的 ToolTip 没人看）。</summary>
    private static string BuildTaskRunDetail(DataStatus status)
    {
        if (status.RecentTaskRuns.Count == 0)
            return "还没有任何任务跑完的记录。";
        var lines = status.RecentTaskRuns.Take(15).Select(r =>
            $"{r.At:MM-dd HH:mm}  {r.Task}" + (r.ErrorCount > 0 ? $"（{r.ErrorCount} 条错误）" : ""));
        return "各任务上次跑完的时间：\n" + string.Join("\n", lines)
             + (status.RecentTaskRuns.Count > 15 ? $"\n…（还有 {status.RecentTaskRuns.Count - 15} 项）" : "");
    }

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

    /// <summary>打开东财人工验证窗口（2026-09-04）。见 <see cref="VerifyEastMoneyAsync"/>。</summary>
    public RelayCommand VerifyEastMoneyCommand { get; }

    /// <summary>【重新读取配置】——见 <see cref="ReloadConfig"/>。</summary>
    public RelayCommand ReloadConfigCommand { get; }
    public RelayCommand RunPlanItemNowCommand { get; }
    public RelayCommand AddPlanTemplateCommand { get; }
    public RelayCommand RunPlanGroupNowCommand { get; }

    /// <param name="browserChannel">
    /// 东财的浏览器通道（2026-09-04）。界面上只用它做一件事：把那个平时藏着的浏览器窗口
    /// 显示出来，让人手工过一次东财的人工验证——验证只能人点，程序代替不了。
    /// 给 null 就是没有这个能力，按钮点了会说明原因。
    /// </param>
    public MainViewModel(FetchPaths paths, FetchOrchestrator orchestrator, List<NamedBarSource> availableSources,
                         Services.WebView2JsonFetcher? browserChannel = null,
                         Func<IBoardFetcher>? recreateBoardFetcher = null)
    {
        _browserChannel = browserChannel;
        _recreateBoardFetcher = recreateBoardFetcher;
        _orchestrator = orchestrator;
        _paths = paths;
        AvailableSources = availableSources;
        // Default to Tencent, not the first entry — EastMoney gets network-limited/blocked much
        // faster on some machines (see doc/data-platform-design.md), Tencent+新浪 has proven
        // stable in practice. Falls back to the first source if "Tencent" isn't in the list.
        _selectedSource = ResolveBarSource(availableSources);
        // 记下启动时这两项的值。【重新读取配置】拿它们跟新读到的比，日志才写得出"从什么变成什么"
        // ——只报当前值的话，人分不清"我刚改的那下生效了没有"。App 造 boardFetcher 跟这里读的
        // 是同一份文件、同一时刻，所以这份"已应用值"跟实际造出来的对象是对得上的。
        _appliedBoardChannel = FetcherSettings.ReadBoardChannel(paths.SettingsPath);
        _appliedNic = FetcherSettings.ReadString(paths.SettingsPath, "Push2NetworkInterface");
        _appliedMemberHost = FetcherSettings.ReadBoardMemberHost(paths.SettingsPath);

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
            _ => StopEverything("用户点了停止"),
            // 并发跑的任务不占 IsBusy（它们只登记在占用表里），所以这里也要看占用表，
            // 否则手动并发跑着的时候【停止全部】是灰的、点不动。
            _ => IsBusy || IsPlanRunning || Occupancy.AnyRunning);
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
        // 计划页那个【停止】＝停止全部（2026-09-02 用户要求）：以前它只在"计划正在跑"时可用，
        // 于是手动点某一行【执行】跑起来之后，在计划页上根本没有能停的按钮（得切回【手动】页）。
        // ⚠ **故意不判 IsBusy / IsPlanRunning**（2026-09-04，跟下面【执行】按钮同一个道理）：
        // 那两个条件会让按钮变成禁用态，而禁用的按钮点下去什么都不发生、也不说为什么，
        // 深色主题下连"它是灰的"都看不出来。实况是数据源在限流熔断里、顶上写着"空闲"，
        // 人想停掉排着的自动重试却发现按钮点不动。拦截和说明都放进 StopEverything。
        StopPlanCommand = new RelayCommand(_ => StopEverything("用户点了停止"), _ => true);

        // 跟【执行】【停止全部】同一个原则：永远可点，点了被拒也要在日志里说清楚为什么，
        // 别做成一个灰着的、点下去毫无反应的按钮。
        VerifyEastMoneyCommand = new RelayCommand(async _ => await VerifyEastMoneyAsync(), _ => true);
        // 手动页那几个大按钮在跑时不给点：它们横跨所有数据源，这时候换任何东西都可能
        // 换到正在用的对象底下。计划在跑不挡——按源记账的并发下，多数计划项跟配置里这几项
        // 毫不相干，为了改个网卡去停整个计划不合理（真冲突的那一项在 ReloadConfig 里单独挡）。
        ReloadConfigCommand = new RelayCommand(_ => ReloadConfig(), _ => !IsBusy);
        // 每行一个【执行】按钮，参数就是那一行——比"先选中再点右边的按钮"少一步
        RunPlanItemNowCommand = new RelayCommand(
            async p => await RunPlanItemNowAsync(p as PlanItemViewModel),
            // ⚠ 这里**故意不判 IsBusy / IsPlanRunning**（2026-09-03 用户反馈"点了没反应"）：
            // 那两个条件会让按钮变成禁用态，而禁用的按钮点下去什么都不发生、也不说为什么，
            // 深色主题下连"它是灰的"都看不出来。真正的拦截放在 RunPlanItemNowAsync 里，
            // 那儿会往日志写清楚"没执行，因为计划正在运行中"。宁可点了被拒绝，也别静默。
            p => p is PlanItemViewModel);
        AddPlanTemplateCommand = new RelayCommand(_ => AddPlanTemplate(), _ => !IsPlanRunning);
        RunPlanGroupNowCommand = new RelayCommand(
            async p => await RunPlanGroupNowAsync(p as PlanGroupViewModel),
            p => p is PlanGroupViewModel);      // 同上：拦截和说明都在 RunPlanGroupNowAsync 里

        // 日志批量刷新（见 Log 的注释）：攒 120ms 一批，高频写日志时 UI 线程才有空处理点击
        _logFlushTimer = new DispatcherTimer { Interval = LogFlushInterval };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogLines();
        _logFlushTimer.Start();

        // 占用表一变就刷界面那行。回调可能来自后台线程，切回 UI 线程再动绑定属性。
        Occupancy.Changed += () => OnUi(SyncRunningTasks);

        RefreshDataStatus();
        RefreshFailedCodeCount();
        LoadPlan();
        // 界面上没有数据源选项了，日志里得说明这一轮用的是哪个。
        // 只写事实，不写"它是怎么回事""想换去改哪个文件"——那些属于配置文件里的说明，
        // 写进运行日志只会挤掉真正的执行记录（2026-09-06 用户指出）。
        Log($"K线数据源：{SelectedSource.Name}");
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

    /// <summary>
    /// 公告关键词：优先用计划行里填的那个（2026-09-02 从全局参数挪进行里），留空就用【手动】页那个框。
    /// 跟"首次回看几年""日期"是同一套规矩——参数属于行，全局框只当兜底。
    /// </summary>
    private List<string> ParseAnnouncementKeywords(string? rowValue = null)
    {
        var text = string.IsNullOrWhiteSpace(rowValue) ? AnnouncementKeywordsText : rowValue;
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

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
            string detail = "";
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
                detail = BuildTaskRunDetail(status);
            }
            catch (Exception ex)
            {
                text = $"读取数据范围失败：{ex.Message}";
            }
            // DataStatusText 的 setter 触发 PropertyChanged → 必须回到 UI 线程更新绑定
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                DataStatusText = text;
                DataStatusDetail = detail;
            });
        });
    }

    /// <summary>还没搬上界面的日志行（新的在后面）。见 <see cref="Log"/>。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pendingLogLines = new();

    /// <summary>攒多久刷一次。120ms 肉眼看不出延迟，又能把一秒内几十条合成一次界面更新。</summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(120);

    private DispatcherTimer? _logFlushTimer;

    /// <summary>
    /// 记一行日志。
    ///
    /// ════ 为什么要攒着批量刷（2026-09-02）════
    /// <see cref="LogLines"/> 是绑到界面上的 ObservableCollection，每 Insert 一次就触发一次
    /// 布局+渲染，而且**必须在 UI 线程上**。高频写日志时 UI 线程被这些更新占满，
    /// 用户的点击事件排在后面——实测撞过一次：一个任务 4 秒一轮无限重跑、每轮刷 4 行，
    /// 【停止全部】按钮点了没反应（事件进了队列，但前面堆着几百次渲染）。
    ///
    /// 所以界面更新改成"攒 120ms 刷一批"：单次点击/单条日志的体感没变化，
    /// 高频时几十条合成一次渲染，UI 线程始终有空处理输入。
    ///
    /// ⚠ **文件写入保持即时**（AutoFlush）：程序崩溃或被强制结束时，
    /// 磁盘上那份 fetch.log 得能看到最后发生了什么——这正是它存在的意义。
    /// </summary>
    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logFileWriter?.WriteLine(line);
        _pendingLogLines.Enqueue(line);
    }

    /// <summary>把攒下的日志一次性搬上界面（新的在最上面）。定时器每 120ms 调一次。</summary>
    private void FlushPendingLogLines()
    {
        if (_pendingLogLines.IsEmpty) return;
        var batch = new List<string>();
        while (_pendingLogLines.TryDequeue(out var line)) batch.Add(line);

        // 界面是"最新在最上面"，所以这一批要倒着插——插完之后组内先后仍是对的
        for (int i = batch.Count - 1; i >= 0; i--) LogLines.Insert(0, batch[i]);
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

    /// <summary>
    /// 数据源占用表（2026-09-04）——计划和手动执行**共用这一份**，是"能不能并发"的唯一依据。
    ///
    /// 原来靠一个全局 IsBusy："有任务在跑就一律不许再开"。可【板块成分股】走东财 push2、
    /// 要人守着过图片验证码，【个股日K】走腾讯要跑一个半小时——两个压根不抢同一个源，
    /// 却只能排队，板块几天都追不上时效性（1000 个板块跑一天才拿下 207 个）。
    /// 换成按源记账后，源不重叠就能同时跑。
    /// </summary>
    public SourceOccupancy Occupancy { get; } = new();

    /// <summary>
    /// 「这一项现在能不能开跑」的裁决（2026-09-05）——让路还是抢占，全在
    /// <see cref="SourceAdmission"/> 里，那个类不依赖 UI 所以能完整测。
    /// 这边只负责把它的结论渲染成界面上的文字。
    /// </summary>
    private SourceAdmission? _admissionField;

    private SourceAdmission _admission =>
        _admissionField ??= new SourceAdmission(Occupancy, Log);

    /// <summary>
    /// 界面上「正在执行」那几行（2026-09-05）——占用表的镜像，每行带自己的用时和【停止】。
    ///
    /// 为什么要镜像而不是每次重建整个列表：行里有"停止中"这种**界面自己的状态**，
    /// 整表重建会把它冲掉，用户点完【停止】按钮又变回可点的样子，像是没生效。
    /// 所以按 Id 增删，已有的行原样留着（见 <see cref="SyncRunningTasks"/>）。
    /// </summary>
    public ObservableCollection<RunningTaskViewModel> RunningTasks { get; } = [];

    /// <summary>有任务在跑——界面据此显示/隐藏「正在执行」那个小标题。
    /// 没人在跑时整块不占地方（不再写一句"空闲"，见 <see cref="IsBusy"/> 的注释）。</summary>
    public bool HasRunningTasks => RunningTasks.Count > 0;

    /// <summary>每秒走一次，只干一件事：刷新上面那几行的用时。没人在跑时停掉。</summary>
    private DispatcherTimer? _runningTimer;

    /// <summary>
    /// 把占用表的变化搬进 <see cref="RunningTasks"/>：新登记的加进去，跑完的移走，
    /// 还在的原样保留（保住"停止中"的状态和开始时刻）。必须在 UI 线程上调。
    ///
    /// ⚠ 只动占用表来的行——【手动】页那种大任务的行不在占用表里，由
    /// <see cref="BeginManualRow"/>/<see cref="EndManualRow"/> 自己管，这里碰它会把它抹掉。
    /// </summary>
    private void SyncRunningTasks()
    {
        var live = Occupancy.Snapshot();
        var liveIds = live.Select(t => t.Id).ToHashSet();

        for (int i = RunningTasks.Count - 1; i >= 0; i--)
            if (RunningTasks[i].FromOccupancy && !liveIds.Contains(RunningTasks[i].Id))
                RunningTasks.RemoveAt(i);

        var have = RunningTasks.Select(v => v.Id).ToHashSet();
        foreach (var t in live)
            if (!have.Contains(t.Id))
                RunningTasks.Add(RunningTaskViewModel.FromTask(t, StopRunningTask));

        SyncPlanRowsWithOccupancy(live);
        AfterRunningTasksChanged();
    }

    /// <summary>
    /// 把「谁在跑」反映到计划页对应的行上（2026-09-05）。
    ///
    /// 为什么需要：同一项任务有两个入口——计划页那一行的【执行】，和【手动】页的按钮。
    /// 走计划页时 <see cref="RunPlanItemNowAsync"/> 会把行状态改成"▶ 执行中…"；
    /// 走【手动】页时没人管那一行，它还挂着上一次的结果，于是界面上
    /// 「正在执行 已跑 2 小时 41 分」配着那一行的「✘ 01:33 失败」，看着像程序在自相矛盾。
    ///
    /// 占用表是唯一知道"现在到底谁在跑"的地方，所以从它反推：在跑的标上，跑完的清掉标记
    /// （清掉之后 RecalcTimeline 会重画成上次的结果）。
    /// </summary>
    private void SyncPlanRowsWithOccupancy(IReadOnlyList<RunningTask> live)
    {
        if (PlanItems.Count == 0) return;
        var running = live.Select(t => t.Name).ToHashSet();

        foreach (var vm in PlanItems)
        {
            if (running.Contains(vm.Name))
            {
                // 已经是"执行中/排队等待"这类进行态就别覆盖——那些文案比这里的更具体
                if (vm.StatusLevel != 3)
                {
                    vm.StatusText = "▶ 执行中…";
                    vm.StatusLevel = 3;
                    vm.RefreshStatus();
                }
            }
            else if (vm.StatusLevel == 3)
            {
                // 跑完了但没人改回来（典型是从【手动】页跑的）——清掉进行态，
                // 让下一次重画恢复成这一项真正的上次结果
                vm.StatusLevel = 0;
                vm.RefreshStatus();
            }
        }
    }

    /// <summary>列表增删之后：刷新占位提示，并按需开关那个每秒刷用时的定时器。</summary>
    private void AfterRunningTasksChanged()
    {
        Raise(nameof(HasRunningTasks));

        // 定时器按需开关：一直开着一个每秒 tick 只为刷新一块空白区域没意义
        if (RunningTasks.Count > 0)
        {
            if (_runningTimer == null)
            {
                _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _runningTimer.Tick += (_, _) =>
                {
                    foreach (var v in RunningTasks) v.UpdateElapsed();
                };
            }
            _runningTimer.Start();
        }
        else _runningTimer?.Stop();
    }

    /// <summary>
    /// 单独停掉某一项（每行后面那个【停止】，2026-09-05）。跟【停止全部】的区别要说清楚：
    /// 并发跑着两三项时，另一项可能已经跑了一个多小时，不该被连坐。
    /// </summary>
    private void StopRunningTask(RunningTaskViewModel row)
    {
        if (row.Stopping) return;
        if (!row.Cancel())
        {
            Log($"【{row.Name}】刚好已经跑完了，不用停。");
            return;
        }
        row.Stopping = true;
        Log($"已向【{row.Name}】发出停止信号，等它收尾（已抓到的数据不会丢）。其它正在跑的任务不受影响。"
          + (row.FromOccupancy
                ? "如果它是计划里的一项，计划会当这一项被取消、接着跑后面的项——要连计划一起停用【停止全部】。"
                : ""));
    }

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
            // ⚠ 整段包在 try/finally 里：中途任何一次抛异常（连关窗时的 Dispatcher.Invoke 都算）
            //    要是把重置漏掉，标志就永久卡在 1，**之后每一次刷新都被静默跳过**，
            //    界面上这些数字从此不再变化，而且一点提示都没有（2026-09-03 用户发现）。
            try
            {
                FailedRetrySummary? failed = null;
                // null = 这一项没算出来。别用 0 顶替——"取不到"和"真的是 0"在界面上是两句话，
                // 混在一起用户就没法判断"该不该做、做了没有"了。
                int? qfq = null, raw = null, adj = null, fin = null, earn = null;
                try { failed = _orchestrator.GetFailedRetrySummary(); } catch { }
                // 待重取前复权的计数跟失败名单同源（都在 manifest.json 里），一起刷新
                try { qfq = _orchestrator.GetPendingQfqRepairCount(); } catch { /* 只是个计数 */ }
                try { raw = _orchestrator.GetPendingRawBarCount(); } catch { }
                try { adj = _orchestrator.GetPendingAdjRebuildCount(); } catch { }
                try { earn = _orchestrator.GetPendingEarningsCount(); } catch { }
                // 这一个要读 StockMeta、退市表、财务状态表，而它恰好在每项任务跑完时触发——
                // 那会儿库常常还被写锁占着，抛 SqliteException 是常事，所以更不能拿 0 顶。
                try { fin = _orchestrator.GetFinancialFetchPlan().AllPending.Count; } catch { }
                (int, int)? manual = null;
                try { manual = _orchestrator.GetManualFillPending(); } catch { }
                // 这一个只查一条 GROUP BY，比上面几项便宜得多，放在这里不会拖慢刷新
                (int Todo, int Never)? flow = null;
                try { flow = _orchestrator.GetPendingMoneyFlowCount(); } catch { }
                // 三个 COUNT(*) 走 BoardMemberFetchState（千把行）和 Board，同样很便宜
                (int Todo, int Never, int Total)? boards = null;
                try { boards = _orchestrator.GetPendingBoardMemberCount(); } catch { }
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (failed is not null) FailedRetry = failed;
                    if (qfq is { } v1) PendingQfqRepair = v1;
                    if (raw is { } v2) PendingRawBars = v2;
                    if (adj is { } v3) PendingAdjRebuild = v3;
                    if (fin is { } v4) PendingFinancials = v4;
                    if (earn is { } v5) PendingEarnings = v5;
                    if (manual is { } v6) ManualFill = v6;
                    if (flow is { } v7) PendingMoneyFlow = v7;
                    if (boards is { } v8) PendingBoardMembers = v8;
                });
            }
            finally { Interlocked.Exchange(ref _refreshingCounts, 0); }
        });
    }

    /// <summary>
    /// 【手动】页的大任务开跑：往「正在执行」列表里加一行（2026-09-05，替掉原来的心跳
    /// ElapsedText）。这类任务**不进占用表**——它横跨所有数据源，登记进去只会把自己挡在门外——
    /// 所以这里手工加、跑完手工去（见 <see cref="EndManualRow"/>），不然它跑着的时候
    /// 界面上一行都没有，看着像什么都没在干。
    /// </summary>
    private RunningTaskViewModel BeginManualRow(string name, CancellationTokenSource cts)
    {
        var row = RunningTaskViewModel.FromManualRun(name, cts, StopRunningTask);
        OnUi(() =>
        {
            RunningTasks.Add(row);
            AfterRunningTasksChanged();
        });
        return row;
    }

    /// <summary>手动大任务收工：把那一行去掉。**必须放在 finally 里**，否则它会永远挂在界面上。</summary>
    private void EndManualRow(RunningTaskViewModel? row)
    {
        if (row == null) return;
        OnUi(() =>
        {
            RunningTasks.Remove(row);
            AfterRunningTasksChanged();
        });
    }

    // ── 空闲自动补的定时器 ──

    // ── 界面设置的持久化 ──

    /// <summary>板块通道／网卡／成分股域名当前**真正生效**的值（不是文件里的值），见 <see cref="ReloadConfig"/>。</summary>
    private string _appliedBoardChannel;
    private string? _appliedNic;
    private string? _appliedMemberHost;

    /// <summary>
    /// 【重新读取配置】（2026-09-05）：不重启程序，把 <c>data/fetcher-settings.json</c> 的改动吃进来。
    ///
    /// ════ 三项配置，两种生效方式 ════
    /// · <c>BarSource</c>——只是"下一轮抓 K 线用列表里的哪一个"。三个源的对象启动时就都造好了，
    ///   换的是个引用，所以随时能换。正在跑的那一轮不受影响：它早把对象传进去了。
    /// · <c>BoardMemberChannel</c> / <c>Push2NetworkInterface</c>——决定**造哪个类、构造参数是什么**，
    ///   光重读文件不换对象等于没改。所以这里要重造一个 fetcher 塞回 orchestrator。
    ///
    /// ════ 为什么板块那两项可能"这次没换成" ════
    /// 重造对象要求**没有任务正在用它**。手动页的大按钮由命令的 CanExecute 挡住了（那些横跨
    /// 所有数据源）；但计划是**按数据源并发**跑的（见 SourceOccupancy），完全可能这会儿正有一项
    /// 占着东财 push2 抓成分股——跑到一半把它脚下的对象换掉，事件订阅和限流器状态都会错乱。
    /// 这种时候就跳过这一项、把话说清楚，而不是拒绝整个重载：另外两项该生效还是得生效。
    ///
    /// 有意不做的事：不改写配置文件（那会洗掉用户的注释，见 FetcherSettings 类注释），
    /// 也不碰浏览器通道（重建它等于丢掉攒下的 Cookie 身份，而这几项设置也不影响它）。
    /// </summary>
    private void ReloadConfig()
    {
        Log("===== 【重新读取配置】开始 =====");
        Log($"　配置文件：{_paths.SettingsPath}");

        if (!File.Exists(_paths.SettingsPath))
        {
            // 正常情况下启动时 EnsureTemplate 就写出来了，走到这儿多半是人手删了
            Log("　⚠ 配置文件不存在，所有设置继续用代码里的默认值。重启程序会自动写一份带说明的模板。");
            Log("===== 【重新读取配置】结束 =====");
            return;
        }

        // ── ① K 线数据源 ──
        var oldBar = SelectedSource.Name;
        SelectedSource = ResolveBarSource(AvailableSources);
        Log($"　K线数据源 BarSource：{ChangeText(oldBar, SelectedSource.Name)}");

        // ── ② 板块成分股通道 + push2 网卡 ──
        var newChannel = FetcherSettings.ReadBoardChannel(_paths.SettingsPath);
        var newNic = FetcherSettings.ReadString(_paths.SettingsPath, "Push2NetworkInterface");
        var newHost = FetcherSettings.ReadBoardMemberHost(_paths.SettingsPath);
        bool boardChanged = newChannel != _appliedBoardChannel
                         || !string.Equals(newNic, _appliedNic, StringComparison.Ordinal)
                         || !string.Equals(newHost, _appliedMemberHost, StringComparison.OrdinalIgnoreCase);

        if (!boardChanged)
        {
            Log($"　板块通道 BoardMemberChannel：{ChangeText(_appliedBoardChannel, newChannel)}");
            Log($"　push2 网卡 Push2NetworkInterface：{ChangeText(NicText(_appliedNic), NicText(newNic))}");
            Log($"　成分股域名 BoardMemberHost：{ChangeText(HostText(_appliedMemberHost), HostText(newHost))}");
        }
        else if (_recreateBoardFetcher is null)
        {
            // 兜底：App 没传重造委托（测试里构造的 ViewModel 就是这样）。宁可说清楚也别假装换了。
            Log("　⚠ 板块通道这次没换：程序没有提供重造通道的能力（改动会在下次启动时生效）。");
        }
        else if (Occupancy.Snapshot().FirstOrDefault(BoardFetcherInUse) is { } busy)
        {
            // 跑到一半换掉它脚下的对象，事件订阅和限流熔断计数都会错乱，不如等
            Log($"　⚠ 板块通道这次没换：【{busy.Name}】正在用它。"
              + "等它跑完再点一次【重新读取配置】就会生效（另外两项已经生效了）。");
        }
        else
        {
            _orchestrator.ReplaceBoardFetcher(_recreateBoardFetcher());
            // 换过通道之后，板块那两行的"数据源"列可能要从"东财行情"变成"本地文件"
            // （FetchTaskCatalog.BoardChannel 在造通道时已经跟着换了），重播一次绑定
            foreach (var vm in PlanItems) vm.RefreshStatus();
            Log($"　板块通道 BoardMemberChannel：{ChangeText(_appliedBoardChannel, newChannel)}");
            Log($"　push2 网卡 Push2NetworkInterface：{ChangeText(NicText(_appliedNic), NicText(newNic))}");
            Log($"　成分股域名 BoardMemberHost：{ChangeText(HostText(_appliedMemberHost), HostText(newHost))}");
            // 只有真的换成了才更新"已应用值"——没换成的话下次点还得再报一次差异
            _appliedBoardChannel = newChannel;
            _appliedNic = newNic;
            _appliedMemberHost = newHost;
        }

        Log("===== 【重新读取配置】结束 =====");
    }

    /// <summary>
    /// 这个正在跑的任务，是不是**正用着板块 fetcher**——决定【重新读取配置】能不能把它换掉。
    ///
    /// 判据有两条，缺一不可（2026-09-06 补的第二条）：
    ///   · 占着东财 push2 的——那是走网络的三条通道（page/browser/http）在抓成分股；
    ///   · **名字就是板块那两项的**——terminal 通道下它们读本地文件、在占用表里按
    ///     "不占源"登记（见 <see cref="FetchTaskCatalog.IsLocalOnlyNow"/>），
    ///     光看 push2 会漏掉，于是跑到一半被热换掉对象。
    ///
    /// 名字取自目录、不写字面量：目录里改了显示名，这里得跟着变，比字符串常量可靠。
    /// </summary>
    private static bool BoardFetcherInUse(RunningTask t)
        => t.Sources.Contains(DataSourceId.EmPush2)
        || t.Name == FetchTaskCatalog.Info(FetchActionId.StepBoardMembers).Name
        || t.Name == FetchTaskCatalog.Info(FetchActionId.StepBoardList).Name;

    /// <summary>"旧 → 新 ✔ 已生效"或者"值（没变）"。人一眼要能看出自己刚改的那下算不算数。</summary>
    private static string ChangeText(string oldValue, string newValue)
        => oldValue == newValue ? $"{newValue}（没变）" : $"{oldValue} → {newValue}  ✔ 已生效";

    /// <summary>成分股域名没配就是走代码默认，日志里要写成人看得懂的样子而不是空字符串。</summary>
    private static string HostText(string? host)
        => string.IsNullOrWhiteSpace(host)
            ? $"（默认 {EastMoneyBoardFetcherBase.DefaultMemberHost}）" : host;

    /// <summary>网卡没配就是走默认路由，日志里得写出来——空字符串看着像读失败。</summary>
    private static string NicText(string? nic)
        => string.IsNullOrWhiteSpace(nic) ? "（默认路由）" : nic;

    /// <summary>
    /// 定下这一次运行用哪个 K 线源：配置文件里指定了就用它，没有/认不出来就用腾讯。
    /// 见 <see cref="SelectedSource"/> 的说明——界面上没有这个选项了，改源靠改配置。
    /// </summary>
    private NamedBarSource ResolveBarSource(List<NamedBarSource> sources)
    {
        // 走 FetcherSettings 读（2026-09-05）：配置文件现在是**带 // 注释的 JSONC**，
        // 用普通的 JsonDocument.Parse 会当场抛异常 → 整份配置被忽略 → BarSource 静默失效。
        var want = FetcherSettings.ReadString(_paths.SettingsPath, "BarSource");

        var picked = sources.FirstOrDefault(
            s => string.Equals(s.Name, want, StringComparison.OrdinalIgnoreCase));
        if (picked != null) return picked;

        if (!string.IsNullOrWhiteSpace(want))
            Log($"⚠ 配置里的 BarSource=\"{want}\" 不认识，改用腾讯。可选：{string.Join(" / ", sources.Select(s => s.Name))}");
        return sources.FirstOrDefault(s => s.Name == "Tencent") ?? sources[0];
    }

    /// <summary>
    /// 这一项要是因为数据源限流熔断而没开工，返回预计恢复的时刻（"HH:mm"）；别的情况返回 null。
    /// 信息来自 <see cref="FetchPlanItem.LastMessage"/>——那句话是 FetchResult.SkippedReason 原样存下来的。
    /// 取不到就退回"已跳过（前置失败）"的老文案，不至于显示错。
    /// </summary>
    /// <summary>
    /// 「跳过」这一轮该怎么显示（2026-09-04）。原来不管什么原因都写"已跳过（前置失败）"，
    /// 可 Skipped 有好几种来由：限流熔断没轮到、数据源前置条件不满足、真的前置项失败……
    /// 显示成不存在的原因比不显示更糟——人会顺着那个方向白查一通。
    ///
    /// 真实原因存在 <see cref="FetchPlanItem.LastMessage"/> 里（＝FetchResult.SkippedReason 原样），
    /// 完整那句在鼠标悬停的提示里能看到，状态列只放最短的那一截。
    /// </summary>
    private static string SkipStatusText(FetchPlanItem m, string 时刻)
    {
        var msg = m.LastMessage ?? "";
        if (PausedResumeAt(m) is { } resume) return $"⏸ 暂停中，{resume} 恢复";
        if (msg.Contains("全部失败")) return $"⏭ {时刻} 没抓到（数据源拒绝）";
        if (msg.Contains("前置")) return "⏭ 已跳过（前置失败）";
        if (msg.Length > 0) return $"⏭ {时刻} 跳过";
        return "⏭ 已跳过";
    }

    private static string? PausedResumeAt(FetchPlanItem m)
    {
        if (m.LastMessage is not { Length: > 0 } msg || !msg.Contains("熔断中")) return null;
        var mm = System.Text.RegularExpressions.Regex.Match(msg, @"预计 (\d\d:\d\d) 恢复");
        return mm.Success ? mm.Groups[1].Value : null;
    }

    /// <summary>
    /// 读界面设置。现在这里只剩一件事：把**老版本的**「空闲时自动补财务」开关迁移进计划
    /// （2026-08-31）——那个复选框已经并成了计划里的重复规则「空闲时」，见 MigrateLegacyIdleSetting。
    /// </summary>
    private bool ReadLegacyIdleFinancialSetting()
    {
        try
        {
            return FetcherSettings.ReadBool(
                _paths.SettingsPath, "AutoFillFinancialsWhenIdle");
        }
        catch
        {
            return false;   // 设置文件坏了不影响程序启动
        }
    }

    /// <summary>
    /// 迁移完就把老键去掉，免得下次启动又迁一遍、把用户后来的修改盖回去。
    ///
    /// ⚠ 2026-09-05 修：原来这里是 <c>File.WriteAllText(路径, "{}")</c>——**把整个设置文件
    /// 清空**，用户配的 BarSource、Push2NetworkInterface 一起没了，而且不报错。
    /// （那多半就是这个文件后来变成 0 字节的原因。）现在只删这一个键，其余原样保留。
    /// </summary>
    private void ClearLegacyIdleFinancialSetting() =>
        FetcherSettings.RemoveKey(_paths.SettingsPath, "AutoFillFinancialsWhenIdle");

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
    private int PendingRetryCount()
        => _failedRetry is { } f ? f.PerStockTotal + (f.MarketCapPending ? 1 : 0) : 0;

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
        _cts = new CancellationTokenSource();
        var row = BeginManualRow(name, _cts);
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
            EndManualRow(row);
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
        _cts = new CancellationTokenSource();
        // 等待期间也算"在执行"：那段时间同样只能点【停止】把它撤掉，界面上得看得见它排着
        var row = BeginManualRow($"{label}（定时 {ScheduleTimeText}）", _cts);
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
            EndManualRow(row);
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

    /// <summary>
    /// 手动点某一行/某一组【执行】用的取消令牌。**跟计划循环的 <see cref="_planCts"/> 分开**——
    /// 2026-09-03 之前两者共用一个：那时手动执行的按钮在计划运行时是禁用的，共用不会打架。
    /// 现在计划"在待命"（没在跑任务、只是等下一个时刻）也允许手动插一项，共用就会出事：
    /// 手动那一轮结束时把 _planCts 置 null，计划循环的令牌就此丢失，【停止全部】对它再也不起作用。
    /// </summary>
    private CancellationTokenSource? _manualCts;
    private DispatcherTimer? _planTimer;

    /// <summary>
    /// 计划里的**组**（2026-09-02）：界面上一个可折叠的块，组头管时间和重复规则。
    /// 组顺序即组间执行顺序。
    /// </summary>
    public ObservableCollection<PlanGroupViewModel> PlanGroups { get; } = [];

    /// <summary>
    /// 所有任务摊平后的视图（按组顺序 + 组内顺序），给"当前在跑哪一项""重画状态列"这类
    /// 不关心分组的逻辑用。跟 <see cref="PlanGroups"/> 里的是**同一批** PlanItemViewModel 实例。
    /// </summary>
    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = [];

    /// <summary>
    /// 计划排得有没有明显问题（2026-09-02）——现在只查"要等收盘的项会不会在收盘前开跑"，
    /// 见 <see cref="FetchPlan.CheckSchedule"/>。空字符串＝没问题，界面上那条黄字就不显示。
    /// **只提醒、不拦**：用户完全可能是故意那么排的。
    /// </summary>
    private string _planWarningText = "";
    public string PlanWarningText
    {
        get => _planWarningText;
        private set { Set(ref _planWarningText, value); Raise(nameof(HasPlanWarning)); }
    }
    public bool HasPlanWarning => PlanWarningText.Length > 0;

    // ── 计划模板（2026-09-02，配合【拉取全部】拆成 13 个原子项）──

    public IReadOnlyList<PlanTemplate> PlanTemplates { get; } = FetchPlanTemplates.All;

    private PlanTemplate? _selectedPlanTemplate = FetchPlanTemplates.All[0];
    public PlanTemplate? SelectedPlanTemplate
    {
        get => _selectedPlanTemplate;
        set => Set(ref _selectedPlanTemplate, value);
    }

    /// <summary>
    /// 把选中的模板铺进计划：**每一行都是独立的一项**，铺完就跟手动排的没有区别
    /// （模板只是排版动作，运行时不存在"组"这个概念，见 FetchPlanTemplates 的类注释）。
    ///
    /// 规则见 <see cref="FetchPlanTemplates.Apply"/>：已经被用户配过的项一律不动，
    /// 其余按模板配置好、按模板顺序连成一串排在末尾。
    /// </summary>
    private void AddPlanTemplate()
    {
        var tpl = SelectedPlanTemplate;
        if (tpl == null || _plan == null) return;

        // 具体怎么铺（哪些配置、哪些保持原样）在 FetchPlanTemplates.Apply 里，那边有单元测试守着
        var (group, applied, untouched) = FetchPlanTemplates.Apply(_plan, tpl);

        RebuildPlanItems();
        SavePlan();
        RecalcTimeline();

        Log($"已按模板铺好「{group.Name}」组：{applied.Count} 项（{group.RepeatText}"
          + (group.NotBefore.HasValue ? $"，{group.TriggerText}" : "") + "）"
          + (untouched.Count > 0
                ? $"；另有 {untouched.Count} 项你已经在别处排着了，保持原样没动：{string.Join("、", untouched)}"
                : "")
          + "。组头改时间和重复规则，组里改顺序和参数。");
    }

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
        ReportPlanMigration();
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

        var fin = _plan.AllItems.FirstOrDefault(i => i.Action == FetchActionId.FetchFinancials);
        if (fin != null)
        {
            // 分组之后"空闲时"是**组**的规则：把它挪进空闲组就等于原来那个开关
            fin.Owner?.Items.Remove(fin);
            var idle = _plan.GroupOf(PlanGroupKind.Periodic);
            fin.Enabled = true;
            fin.Owner = idle;
            idle.Items.Add(fin);
            _planStore?.Save(_plan);
            Log("【空闲时自动补财务】这个开关已经并进计划了——"
              + "已把计划里的「拉取财务报表」设成 启用 + 重复「空闲时」，效果跟原来一样："
              + "程序空着就补一批、到点前自动给定时任务让路。以后在【计划】页调它就行。");
        }
        ClearLegacyIdleFinancialSetting();
    }

    /// <summary>
    /// 把加载时做过的"退役动作 → 原子项"替换讲清楚（2026-09-02）。计划表会当场变样，
    /// 不说一声人会以为自己排的东西丢了。替换规则见 <see cref="FetchPlan.MigrateRetired"/>。
    /// </summary>
    private void ReportPlanMigration()
    {
        if (_plan == null || _plan.MigrationNotes.Count == 0) return;
        Log("计划里有几个动作已经拆成更细的任务，已自动替换（设置照搬，顺序不变）：");
        foreach (var note in _plan.MigrationNotes) Log("　" + note);
        Log("　好处是能挑着跑、自己排顺序，某一项失败也不会拖累其它项。不满意就直接在表里改。");
        _planStore?.Save(_plan);   // 迁移结果落盘，免得每次启动都重算一遍
    }

    /// <summary>
    /// 按 _plan 重建界面上的两层结构：组 → 组里的任务（2026-09-02）。
    /// <see cref="PlanItems"/> 是所有子项摊平后的视图，给"当前在跑哪一项""按序号找行"这类逻辑用。
    /// </summary>
    private void RebuildPlanItems()
    {
        PlanGroups.Clear();
        PlanItems.Clear();
        foreach (var g in _plan.Groups)
        {
            var gvm = new PlanGroupViewModel(g, OnPlanItemEdited);
            foreach (var item in g.Items)
            {
                var ivm = new PlanItemViewModel(item, OnPlanItemEdited);
                gvm.Items.Add(ivm);
                PlanItems.Add(ivm);
            }
            PlanGroups.Add(gvm);
        }
        RenumberPlanItems();
        RefreshPlanWarning();
    }

    /// <summary>
    /// 重排"#"列的执行序号（2026-09-02）。序号是**组内**的位置，不存进 json——
    /// 顺序本来就是列表顺序，另存一份编号只会多一个可能对不上的真相。
    /// 建表、拖动排序、模板铺开之后都要调一次。
    /// </summary>
    private void RenumberPlanItems()
    {
        // ① 编号：组号.序号（日更 1.x、季度 2.x、按需 3.x）
        var numberOf = new Dictionary<FetchActionId, string>();
        for (int gi = 0; gi < PlanGroups.Count; gi++)
        {
            var g = PlanGroups[gi];
            for (int i = 0; i < g.Items.Count; i++)
            {
                g.Items[i].GroupIndex = gi + 1;
                g.Items[i].Order = i + 1;
                numberOf[g.Items[i].Model.Action] = g.Items[i].Number;
            }
            g.RefreshHeader();
        }

        // ② 依赖列：把前置动作翻译成编号。硬前置带 *（它失败了这一项直接跳过），软的不带。
        //    编号得等①算完才有，所以分两趟。
        foreach (var vm in PlanItems)
        {
            var info = vm.Info;
            var parts = new List<string>();
            if (info.DependsOn is { } hard)
                parts.Add((numberOf.TryGetValue(hard, out var hn) ? hn : FetchTaskCatalog.Info(hard).Name) + "*");
            foreach (var soft in info.SoftDependsOn ?? [])
                parts.Add(numberOf.TryGetValue(soft, out var sn) ? sn : FetchTaskCatalog.Info(soft).Name);
            // 按编号从小到大排：目录里的声明顺序是按"哪条依赖更重要"写的，摆到界面上就成了
            // "1.12 1.02"这种乱序，读的人得自己在脑子里排一遍。
            // 序号补足两位之后，字符串序就是数值序，直接排即可（带 * 的硬前置也落在正确位置）。
            parts.Sort(StringComparer.Ordinal);
            vm.DependsText = string.Join(" ", parts);
        }
    }

    /// <summary>跑一次计划自检，把提醒放到表格上方那条黄字里（没问题就清空）。</summary>
    private void RefreshPlanWarning()
    {
        if (_plan == null) return;
        SyncPlanFromUi();
        PlanWarningText = string.Join("\n", _plan.CheckSchedule());
    }

    /// <summary>
    /// 界面上的顺序才是权威顺序（拖动排序改的是它），把它写回 _plan。
    /// 组的顺序、组内任务的顺序都在这里同步，顺便回填每个任务的 Owner。
    /// </summary>
    private void SyncPlanFromUi()
    {
        if (_plan == null) return;
        _plan.Groups = PlanGroups.Select(g =>
        {
            g.Model.Items = g.Items.Select(v => v.Model).ToList();
            return g.Model;
        }).ToList();
        _plan.LinkOwners();
    }

    /// <summary>任何一项被改动（勾选/时间/重复/参数）都会走到这里：立刻存盘 + 重画时间轴 + 自检。</summary>
    private void OnPlanItemEdited()
    {
        SavePlan();
        RecalcTimeline();
        RefreshPlanWarning();
    }

    private void SavePlan()
    {
        SyncPlanFromUi();
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
                if (m.EffectiveEnabled && m.IsDueOn(now) && m.Pacing != RunPacing.WhenIdle)
                    cursor = (cursor > now ? cursor : now) + m.EffectiveEstimate;
                continue;
            }

            // ② 本轮已经有结果了——这一列最该说的就是它（空闲项也一样，跑过就报结果）。
            //    "本轮"看的是**当期锚点之后开工**，不是"结束于今天"，也不是"今天的到点之后"：
            //    18:00 开工跑到次日凌晨，那些项属于**同一轮**，第二天白天看界面就该看到
            //    它们昨晚的结果——2026-09-03 用户："程序跑完了，但我从界面上看不出昨天哪些跑了"。
            //    锚点跟 PlanRunner 判"该不该跑"用的是同一个，界面说的和引擎想的才对得上。
            bool hasRoundResult = m.LastOutcome != RunOutcome.None
                && m.DueAnchorAt(now) is { } anchor
                && (m.LastStart ?? m.LastEnd) >= anchor;
            if (hasRoundResult)
            {
                // 跨天的结果要带上日期，否则昨晚 23:10 跑完的显示成"✔ 23:10 完成"，
                // 今天早上看还以为是今天跑的。
                var 时刻 = m.LastEnd?.Date == now.Date ? $"{m.LastEnd:HH:mm}" : $"{m.LastEnd:M/d HH:mm}";
                (vm.StatusText, vm.StatusLevel) = m.LastOutcome switch
                {
                    // 带上存量进度（2026-09-04）：板块成分股要跨好几轮，
                    // 只写"完成"看不出还剩多少，人只能去翻日志。
                    RunOutcome.Ok        => (m.LastMessage is { Length: > 0 } prog && prog.StartsWith("已抓")
                                             ? $"✔ {时刻} {prog}" : $"✔ {时刻} 完成", 1),
                    RunOutcome.Failed    => ($"✘ {时刻} 失败", 2),
                    // Skipped 有好几种来由，显示要分开，别一律写成"前置失败"——
                    // 板块列表压根没有前置依赖，却因为这条兜底显示成"已跳过（前置失败）"，
                    // 人看了会去找根本不存在的前置项（2026-09-04 用户指出）。
                    RunOutcome.Skipped   => (SkipStatusText(m, 时刻), 2),
                    RunOutcome.Cancelled => ($"⏹ {时刻} 被停止", 2),
                    _                    => ($"· {时刻}", 0),
                };
                continue;
            }

            // ③ 今天还要跑的**定时**项：算预计时段，并占掉时间轴。
            //    空闲项填的是别人不用的空隙，没有"预计几点跑"这回事，也不该占时间轴。
            if (m.EffectiveEnabled && m.IsDueOn(now)
                && m.Pacing != RunPacing.WhenIdle && m.Repeat != RepeatKind.Manual)
            {
                var earliest = m.DueTimeOn(now) is var due && due > now.Date ? due : cursor;
                var start = earliest > cursor ? earliest : cursor;
                var end = start + m.EffectiveEstimate;
                vm.StatusText = $"{start:HH:mm} → {end:HH:mm}";
                vm.StatusLevel = 0;
                cursor = end;
                continue;
            }

            // ④ 今天不跑的（没启用、手动、空闲时、今天不是它的日子）：拿上一次的结果顶上。
            //    比"未启用"有用得多——那只票到底补没补过、上次是成是败，本来得翻日志才知道。
            (vm.StatusText, vm.StatusLevel) = LastRunBrief(m);
        }

        RecalcGroupHeaders(now);
    }

    /// <summary>
    /// 组头那段时间轴（2026-09-02）："18:00 → 21:12"。
    /// 算法跟子项那条一样是**顺序累加**，只是取的是每组第一项的开始和最后一项的结束；
    /// 折叠着的组光看这一行就知道它占掉晚上哪一段。
    /// </summary>
    private void RecalcGroupHeaders(DateTime now)
    {
        var cursor = now;
        foreach (var g in PlanGroups)
        {
            var model = g.Model;
            g.RefreshHeader();

            if (!model.Enabled || model.Pacing == RunPacing.WhenIdle || model.Repeat == RepeatKind.Manual || !model.IsDueOn(now))
            {
                g.TimelineText = model.Pacing == RunPacing.WhenIdle ? "空档里自动补"
                    : model.Repeat == RepeatKind.Manual ? "不会自动跑"
                    : !model.Enabled ? ""
                    : $"下次 {model.PeriodStartOn(now.AddMonths(1)):MM-dd}";
                continue;
            }

            var due = model.DueTimeOn(now);
            var start = due > cursor ? due : cursor;
            var total = TimeSpan.FromSeconds(g.Items
                .Where(i => i.Model.EffectiveEnabled)
                .Sum(i => i.Model.EffectiveEstimate.TotalSeconds));
            if (total <= TimeSpan.Zero) { g.TimelineText = ""; continue; }

            var end = start + total;
            g.TimelineText = $"{start:HH:mm} → {end:HH:mm}";
            cursor = end;
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
    public void MovePlanItem(PlanGroupViewModel group, int from, int to)
    {
        if (from < 0 || from >= group.Items.Count) return;
        if (to < 0 || to >= group.Items.Count || from == to) return;
        var vm = group.Items[from];
        group.Items.Move(from, to);
        // 摊平视图跟着重排：它跟组里装的是同一批实例，顺序得保持一致
        PlanItems.Clear();
        foreach (var g in PlanGroups) foreach (var i in g.Items) PlanItems.Add(i);
        SelectedPlanItem = vm;
        RenumberPlanItems();
        SavePlan();
        RecalcTimeline();
        RefreshPlanWarning();
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
            // fromPlan: true —— 这条是**计划**在跑，定时项遇到数据源被手工任务占着时可以抢占
            // （见 AcquireOrPreemptAsync）。手动点【执行】那条路走的是同一个方法但传 false，
            // 只让路不抢占：人点的东西不该被另一个人点的东西掐掉。
            (item, deadline, progress, ct) =>
                ExecutePlanItemAsync(item, deadline, progress, ct, fromPlan: true),
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

                    // ⚠ 正在跑的项不能清标记（2026-09-05 修）。
                    //    下面那句"先把进行中的标记清掉"原本是无差别清空的，可 StatusLevel==3
                    //    不只属于"计划当前这一项"——**手动插的任务也占着它**。手动任务不在
                    //    计划的执行序列里（state.Current 不是它），标记一清就退回上次的结果，
                    //    于是界面成了「顶上说已跑 2 小时 41 分，那一行却写着凌晨 01:33 失败」。
                    //    占用表是唯一知道"现在到底谁在跑"的地方，从它反推最可靠。
                    var runningNames = Occupancy.Snapshot().Select(t => t.Name).ToHashSet();

                    // 先把"进行中"的标记清掉，再给当前那一行打上——否则上一轮正在跑的那行会
                    // 一直挂着"执行中"，RecalcTimeline 认得 StatusLevel==3 就不覆盖它了。
                    foreach (var vm in PlanItems)
                    {
                        if (vm.StatusLevel == 3 && !runningNames.Contains(vm.Name)) vm.StatusLevel = 0;
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
            },
            // 一轮跑完才排一次自动重试（2026-09-02）：以前它挂在每一项的 finally 里，
            // 【拉取全部】拆成 13 项之后，同一天会被重排十几次。
            onRoundFinished: () => System.Windows.Application.Current?.Dispatcher.Invoke(ScheduleAutoRetry));

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

    /// <summary>
    /// **停止全部**（2026-09-02）：正在跑的计划、单独执行的那一项、手动页触发的抓取、
    /// 还有排定中的自动重试，一起停掉。"停止"表达的是"别再跑了"，就该停到底——
    /// 只停其中一样的话，人点完发现日志还在刷，会以为按钮坏了。
    ///
    /// ⚠ 三个 CTS 都要取消，一个都不能省：
    ///   · <c>_planCts</c>——计划循环；
    ///   · <c>_manualCts</c>——手动点某一行/某一组【执行】那条路（2026-09-03 从 _planCts 分出来，
    ///     因为计划待命时也能手动插一项，两者可能同时存在）；
    ///   · <c>_cts</c>——当前那个抓取任务（计划项和手动按钮派生出来的都是它）；
    ///   · 自动重试有自己的定时器，不取消的话过一小时它又自己跑起来。
    /// 已经写进数据库的部分不回滚，下次跑会跳过已有数据。
    /// </summary>
    /// <summary>停止信号发出去之后，在后台等各任务收尾完毕（占用表清空），然后如实回话。</summary>
    private async void WaitStopFinishedAsync(int signalled)
    {
        Log($"已向 {signalled} 个正在跑的任务发出停止信号，等它们收尾（已抓到的数据不会丢）…");
        // 超时给得宽：收尾里可能要写几万行库。但**必须有上限**——卡在不响应
        // CancellationToken 的同步调用里的任务谁也掐不动，不能让界面永远停在"正在收尾"。
        bool clean = await Occupancy.WaitAllStoppedAsync(TimeSpan.FromMinutes(3));
        if (clean)
        {
            Log("✔ 全部停止：所有任务都已收尾退出。");
        }
        else
        {
            var left = Occupancy.Snapshot();
            Log($"⚠ 等了 3 分钟还有 {left.Count} 个任务没退出："
              + string.Join("、", left.Select(t => $"【{t.Name}】"))
              + "。它们可能卡在不响应停止的调用里（比如死等一把数据库锁），"
              + "占用的数据源暂时也放不出来。要彻底清掉只能重启程序。");
        }
        OnUi(SyncRunningTasks);
    }

    private void StopEverything(string why)
    {
        bool somethingRunning = IsPlanRunning || IsBusy || _planCts != null || _manualCts != null;
        bool hadRetry = HasAutoRetry;

        if (IsPlanRunning) StopPlan(why);
        else if (somethingRunning)
            Log($"正在停止当前任务（{why}）——已抓到的数据不会丢。");

        _planCts?.Cancel();
        _manualCts?.Cancel();
        _cts?.Cancel();
        // 占用表里每一项都带着自己的 CTS（并发跑的那几个就在这儿），逐个叫停。
        int signalled = Occupancy.CancelAll();
        CancelAutoRetry(why);

        // ════ "点了停止"和"真的停干净了"是两件事（2026-09-04 按用户要求分开）════
        // 任务收到取消信号后还要收尾（把已抓的写库、记下"下次从哪接"），而它们是在自己的
        // finally 里才从占用表移除的——所以**占用表清空 == 全部收尾完毕**，不需要每个任务
        // 再手写一个返回 bool 的 Stop 方法（40 个手写方法漏一个就永远等不到那个 true）。
        //
        // 这里不阻塞 UI：后台等，等到了再回话。
        if (signalled > 0) WaitStopFinishedAsync(signalled);

        // 什么都没在跑也要回话——按钮以前在这种时候是禁用的，点下去毫无反应，
        // 深色主题下还看不出它是灰的。宁可说一句"没什么可停的"，也别静默。
        if (!somethingRunning && !hadRetry)
        {
            var paused = _orchestrator.GetPausedSources();
            if (paused.Count > 0)
                Log("现在没有任务在跑，没什么可停的。" +
                    string.Join("；", paused.Select(p => $"{p.Source} 还在限流熔断中，预计 {p.Until:HH:mm} 恢复")) +
                    "——熔断是数据源那边的配额限制，停不掉也不用停，到点会自己接着跑。");
            else
                Log("现在没有任务在跑，没什么可停的。");
        }
    }

    /// <summary>
    /// 打开东财人工验证窗口（2026-09-04）。
    ///
    /// 程序抓 push2 用的是自己那套浏览器数据（data/local/webview2），跟用户日常的 Chrome/Edge
    /// 是两套。那套刚建起来时在东财眼里是个生面孔，可能先要过一道人工验证才给数据——
    /// 而验证只能人来点。过完之后 Cookie 留在那个目录里，抓取就带着它走，跨次启动也还在。
    ///
    /// 它同时是个排错窗口：板块一直抓不到时打开看看东财到底返回了什么
    /// （验证页？空白？正常数据？），比对着日志猜快得多。
    /// </summary>
    private async Task VerifyEastMoneyAsync()
    {
        if (_browserChannel == null)
        {
            Log("这个版本没有浏览器通道，用不了东财验证窗口。");
            return;
        }
        // 连点防抖：第一次要几秒才出窗口，人看不到反馈就会一直点（实测被连点了六次），
        // 结果是六次 Navigate 打断彼此、窗口反而更难出来。
        if (_verifyingEastMoney)
        {
            Log("验证窗口正在打开中，稍等一下——第一次要几秒（启动浏览器内核 + 打开东财页面）。");
            return;
        }
        _verifyingEastMoney = true;
        try
        {
            Log("正在打开东财验证窗口……第一次要几秒（启动浏览器内核 + 打开东财页面）。");

            // ⚠ 一定要带超时（2026-09-05 加）：这个调用要是永不返回，下面的 finally 就走不到，
            //    _verifyingEastMoney 永远卡在 true，之后**每次点按钮都只回一句"正在打开中"**，
            //    人看到的就是"这个按钮彻底废了"。实测撞过一次（WebView2 初始化里的死锁）。
            //    根因已经修掉，但这道保险要留着：卡住的原因可以有很多，按钮不能跟着一起废。
            //    90 秒——WebView2 首次初始化通常几秒到十几秒，留足余量。
            await _browserChannel.ShowForManualVerificationAsync()
                                 .WaitAsync(TimeSpan.FromSeconds(90));
            Log("验证窗口已打开：按页面提示过一次验证，完了直接关掉那个窗口即可，"
              + "Cookie 会留在 data/local/webview2，后面的板块抓取会带着它走。");
        }
        catch (Exception ex)
        {
            Log($"打开东财验证窗口失败：{ex.Message}");
        }
        finally { _verifyingEastMoney = false; }
    }

    // 【新建组】和【右键移到别的组】2026-09-02 做了又撤掉：空组建出来没法往里放任务，
    // 而跨组移动要连带换重复规则和触发时刻，牵扯到"这一项今天跑过没有"该怎么算。
    // 先把默认那五个组用顺了再说（用户原话："先把现在的组理顺再说"）。
    // 组的模型本身支持任意多个组，将来要恢复这两个功能，加回命令 + 界面入口即可。

    // 组之间的换位（↑↓）2026-09-02 做了又撤：组的先后**不决定**执行先后——真正决定的是
    // 各组的「不早于」和重复规则档位（见 PlanRunner.FindDue，同时到期且同档时才看排列顺序）。
    // 而现有这三个组（每工作日+到点就跑 / 每月+空闲时补 / 手动）永远凑不出"同时到期且同档"，
    // 那个顺序一次都用不上。组**内**子项的排序仍在（组里是严格按顺序串行跑的）。

    /// <summary>
    /// 把一整组**立刻**跑一遍（2026-09-02）：从上到下，只跑组里勾选了的项。
    ///
    /// ⚠ 仍是**一项一项串行**跑，跟计划引擎的做法一样——组不是"一次调用"，
    /// 所以某一项失败不影响后面的，中途点【停止全部】也停得下来。
    /// </summary>
    private async Task RunPlanGroupNowAsync(PlanGroupViewModel? group)
    {
        if (group == null) return;
        // 同 RunPlanItemNowAsync：只拦"有任务在跑"，计划待命时照样能手动跑
        if (IsBusy) { Log($"「{group.Name}」没有执行：另一个任务正在跑，等它结束再点（要停当前任务用【停止全部】）。"); return; }

        var todo = group.Items.Where(i => i.Enabled).ToList();
        if (todo.Count == 0) { Log($"「{group.Name}」里没有勾选的项，什么都没跑。"); return; }

        Log($"===== 手动执行整组「{group.Name}」（{todo.Count} 项，按顺序串行）=====");
        // 整组共用一个令牌：点【停止全部】要能把"后面还没跑的项"一起掐掉，而不是只停当前这一项
        _manualCts?.Dispose();
        _manualCts = new CancellationTokenSource();
        try
        {
            foreach (var vm in todo)
            {
                if (_manualCts?.IsCancellationRequested == true) { Log("已停止，后面的项不再执行。"); break; }
                await RunPlanItemNowAsync(vm);
                // 一项失败不拖累后面的——这正是拆分和分组要保住的东西，所以这里不 break
            }
        }
        finally
        {
            // ⚠ 必须在 finally 里收（2026-09-04）：中途抛出去的话，一个**已取消**的令牌会留在
            // _manualCts 上，下次手动执行 `??=` 正好捡到它，任务一开跑就被立刻取消——
            // 表现是"点了执行，秒结束，什么也没干"，而且看不出为什么。
            _manualCts?.Dispose();
            _manualCts = null;
        }
        Log($"===== 整组「{group.Name}」执行结束 =====");
    }

    /// <summary>把选中的那一项**立刻**跑一次（不走队列，也不影响它的重复规则）。</summary>
    /// <summary>弹一句提示，同时写进日志——日志留痕，弹窗保证人当场看见。</summary>
    private void Notify(string title, string message)
    {
        Log($"{title}：{message.Replace('\n', ' ')}");
        OnUi(() => System.Windows.MessageBox.Show(
            message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information));
    }

    /// <summary>问一句是/否。只在**手动**路径上用——自动跑时没人点，问了会把计划卡死。</summary>
    private bool Confirm(string title, string message)
    {
        bool yes = false;
        OnUi(() => yes = System.Windows.MessageBox.Show(
            message, title, System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes);
        return yes;
    }

    private async Task RunPlanItemNowAsync(PlanItemViewModel? vm)
    {
        if (vm == null) return;
        // ⚠ 拦的是"**有任务正在跑**"，不是"计划开着"（2026-09-03 用户要求）：
        // 计划大部分时间处于**待命**——没在抓任何东西，只是等下一个到点时刻。那会儿程序是空闲的，
        // 没道理不让手动插一项。真正不能并发的只有"同时抓两份数据"（共用限流器和数据库，
        // 并发只会一起撞数据源配额），那由 IsBusy 挡住就够了。
        //
        // 原来这里连同按钮的 CanExecute 一起判了 IsPlanRunning，于是计划一开着按钮就是禁用的，
        // 点下去什么都不发生、也不说为什么——深色主题下连"它是灰的"都看不出来（用户："点执行没反应"）。
        //
        // 2026-09-04 起拦截分三道，全部**当场弹窗告知**（自动侧则是静默让路，见 PlanRunner.IsSourceBusy）：
        //   ① 【手动】页的大任务在跑——那些横跨所有数据源，跟谁都不能并发；
        //   ② 要用的数据源被占——同时抓会一起撞限流；
        //   ③ 前置今天还没跑成功——问一句要不要照样跑。
        if (IsBusy)
        {
            Notify($"【{vm.Name}】没有执行",
                "【手动】页有大任务正在跑，它横跨所有数据源，不能并发。\n等它结束再点（要停当前任务用【停止全部】）。");
            return;
        }

        // ② 数据源占用检查
        var need = vm.Model.Info.EffectiveSources;
        foreach (var t in Occupancy.Snapshot())
        {
            var clash = need.Where(t.Sources.Contains).Select(DataSourceCatalog.NameOf).ToList();
            if (clash.Count == 0) continue;
            Notify($"【{vm.Name}】不能现在跑",
                $"它要用的数据源【{string.Join("、", clash)}】已被【{t.Name}】占用"
              + $"（{(DateTime.Now - t.StartedAt).TotalMinutes:F0} 分钟前开始）。\n\n"
              + "同时抓会一起撞数据源的限流，所以这一项没有执行。\n"
              + "等那一项结束后再点；或者挑一个**用别的数据源**的任务，那个可以跟它并行跑。");
            return;
        }

        // ③ 前置没完成就问一句。只在手动路径上问——自动跑时没人点，问了会把整份计划卡住。
        //    "今天有没有跑成功"用的是 AlreadyRanOn，跟"今天还要不要再跑"同一个判据。
        if (vm.Model.Info.DependsOn is { } dep)
        {
            var depItem = _plan?.AllItems.FirstOrDefault(x => x.Action == dep);
            if (depItem != null && !depItem.AlreadyRanOn(DateTime.Now))
            {
                var depName = FetchTaskCatalog.Info(dep).Name;
                if (!Confirm($"【{vm.Name}】的前置没完成",
                        $"它依赖【{depName}】，而那一项今天还没跑成功。\n\n"
                      + "现在跑可能取不到要的数据。还是要执行吗？"))
                {
                    Log($"【{vm.Name}】没有执行：前置【{depName}】今天还没跑成功，你选了不执行。");
                    return;
                }
                Log($"【{vm.Name}】的前置【{depName}】今天还没跑成功，你选了照样执行。");
            }
        }

        Log($"===== 手动执行计划项【{vm.Name}】 =====");
        // 状态列立刻打上标记——有些任务开头要先扫库算待办量，几十秒不出声，
        // 没这个标记就看不出到底点没点上。
        vm.StatusText = "▶ 执行中…";
        vm.StatusLevel = 3;
        // 整组执行时令牌由外层建好、外层负责回收；单独跑一项时自己建自己收。
        // 分清"谁拥有"很要紧：组里逐项调这里，要是每项跑完都把令牌置 null，
        // 点【停止全部】就只能掐掉当前这一项，后面还没跑的照样往下走。
        bool ownsCts = _manualCts == null;
        var cts = _manualCts ??= new CancellationTokenSource();
        try
        {
            var result = await ExecutePlanItemAsync(vm.Model, null, new Progress<string>(Log), cts.Token);
            vm.Model.LastEnd = DateTime.Now;

            // 「根本没开工」不能记成完成（2026-09-04）：原来这里无条件写 Ok、连返回值都没看，
            // 结果数据源还在限流熔断里、一行都没抓，界面上照样是绿勾"09:25 完成"。
            // 更要命的是 FetchPlanItem.AlreadyRanOn 只认 Ok——记成完成的话今天就不会再跑了。
            if (result?.SkippedReason is { } why)
            {
                vm.Model.LastOutcome = RunOutcome.Skipped;
                vm.Model.LastMessage = why;
                Log($"⏸ 【{vm.Name}】本轮没开工——{why}。今天恢复之后还会再来。");
            }
            else
            {
                vm.Model.LastOutcome = RunOutcome.Ok;
                // 跨轮才做得完的活（板块成分股这种）把存量进度显示出来，别只说"完成"
                vm.Model.LastMessage = result?.Progress;
            }
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
            if (ownsCts) { _manualCts?.Dispose(); _manualCts = null; }
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
    // ── 抢占：计划的定时项优先于手工任务（2026-09-05 用户定的规则）────────────────
    //
    // 缘起：用户手动跑了一项占满数据源的任务（那时【重新拉取失败】没声明 Sources，
    // Mixed 兜底成全部 9 个源），跑了 2 小时 41 分，期间计划里的项全部让路——
    // 界面还一直显示"今天没有待执行的项了"。计划实际停摆了小半天。
    //
    // 规则（讨论后定的，跟最初设想有三处不同，理由写在各自的常量上）：
    //   · 只有**定时项**抢占。空闲项（WhenIdle）语义就是"有空才补"，
    //     没理由为它掐掉用户主动点的任务——今天被误伤的两项恰好都是空闲项。
    //   · 等被抢占者收尾最多 PreemptWaitLimit，**不是 30 分钟**：实测正常停止是秒级
    //     （限流等待、网络请求全带 ct），只有【优化数据库】的大索引是分钟级。
    //   · 等不到就**放弃这一轮**，绝不"不管它、硬上"：停不下来只可能是死锁，
    //     那时候硬启动第二个任务等于主动制造数据竞争——占用机制存在的全部意义就是防这个。


    /// <summary>
    /// 拿数据源占用；拿不到时按规则决定**抢占**还是**让路**。
    /// </summary>
    /// <returns>
    /// (拿到的占用, 说明)。占用为 null 表示这一轮不跑，说明就是记进 SkippedReason 的原因；
    /// 占用不为 null 而说明也不为 null，表示中间等过/抢过，调用方据此多打一行"可以开工了"。
    /// </returns>
    /// <summary>
    /// 拿数据源占用；拿不到时按规则决定抢占还是让路。
    ///
    /// **决策本身在 <see cref="SourceAdmission"/> 里**（2026-09-05 挪过去的）——那个类不依赖 UI，
    /// 所以让路/抢占/超时/被第三方截胡这几条路径都能用假任务在毫秒级测完。
    /// 这里只剩两件事：把结果渲染成界面上的文字，以及记那几行给人看的日志。
    /// </summary>
    private async Task<(RunningTask? Lease, string? Note)> AcquireOrPreemptAsync(
        FetchPlanItem item, PlanItemViewModel? row, IProgress<string> progress,
        CancellationTokenSource itemCts, bool fromPlan, CancellationToken ct)
    {
        var rowName = row?.Name ?? "下一项";

        // 抢占开始时把行状态和顶部文案改掉——这一段可能要等上两分钟，
        // 不说的话界面看着像卡住了。SourceAdmission 不碰 UI，所以钩子放这儿。
        void OnPreemptStart(string blockerName)
        {
            SetRowState(row, "⏫ 抢占中…", 3);
            OnUi(() => PlanStatusText = $"【{rowName}】正在抢占——停止【{blockerName}】…");
        }

        var r = await _admission.AcquireAsync(
            item.Info.Name, item.Info.EffectiveSources,
            isTimedItem: item.Pacing == RunPacing.Immediate,
            fromPlan: fromPlan,
            manualBigTaskRunning: IsBusy,
            itemCts, progress, ct, OnPreemptStart);

        switch (r.Kind)
        {
            case AdmissionKind.Acquired:
                return (r.Lease, null);

            case AdmissionKind.AcquiredAfterPreempt:
                return (r.Lease, r.Reason);

            case AdmissionKind.PreemptTimedOut:
                OnUi(() => PlanStatusText =
                    $"⚠【{r.Blocker}】停不下来（已等 {SourceAdmission.DefaultWaitLimit.TotalMinutes:0} 分钟），"
                    + "可能卡死了——计划暂时跑不了");
                return (null, r.Reason);

            default:    // GaveWay
                if (r.Blocker != null && r.BlockedSource is { } src)
                    OnUi(() => PlanStatusText =
                        $"【{rowName}】让路中——{DataSourceCatalog.NameOf(src)} 被【{r.Blocker}】占着");
                return (null, r.Reason);
        }
    }

    private async Task<FetchResult> ExecutePlanItemAsync(
        FetchPlanItem item, DateTime? deadline, IProgress<string> progress, CancellationToken ct,
        bool fromPlan = false)
    {
        // PlanRunner 是**挑中**这一项就回调报告"当前项 = 它"的，而下面这个等待循环可能让它在这儿
        // 排上十几分钟。不区分的话状态列会写着「▶ 执行中…」，其实一个请求都还没发
        // （2026-09-01 用户反馈："这个其实是没开始做的，是在等前一个完成"）。
        var row = PlanItems.FirstOrDefault(x => ReferenceEquals(x.Model, item));
        bool warned = false;

        // ════ 排队等的是「同一个数据源」，不再是「任何任务」（2026-09-04 改）════
        // 原来等的是全局 IsBusy——只要有任务在跑就排队，"任务严格串行，不会并发抓取"。
        // 可【板块成分股】走东财 push2、要人守着过图片验证码，【个股日K】走腾讯要跑一个半小时，
        // 两个压根不抢同一个源却只能排队，板块几天都追不上时效性（1000 个板块跑一天拿下 207 个）。
        // 现在按源记账：源不重叠直接并发，重叠的才排队。
        //
        // IsBusy 仍然要等——它现在只代表【手动】页那几个大按钮（拉取全部/区间/失败重取），
        // 那些横跨所有数据源，跟谁都不能并发。
        RunningTask? lease = null;
        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var (acquired, giveUpReason) =
                await AcquireOrPreemptAsync(item, row, progress, itemCts, fromPlan, ct);
            ct.ThrowIfCancellationRequested();

            if (acquired == null)
            {
                // 让路：**不算失败**。记成 Skipped 之后 AlreadyRanOn 仍然是 false，
                // 调度循环下一分钟重扫时源要是空了就自然接上（跟挑选阶段的让路同一个语义）。
                SetRowState(row, "⏸ 让路中", 0);
                return new FetchResult { SkippedReason = giveUpReason };
            }
            lease = acquired;
            warned = giveUpReason != null;

            if (warned)
            {
                progress.Report($"　可以开工了，开始【{row?.Name ?? item.Action.ToString()}】。");
                OnUi(() => PlanStatusText = $"正在执行【{row?.Name ?? item.Action.ToString()}】");
            }
            SetRowState(row, "▶ 执行中…", 3);       // 到这儿才是真的开跑

            try
            {
                return await DispatchPlanActionAsync(item, deadline, progress, itemCts.Token);
            }
            finally
            {
                // 用时和【停止】都由「正在执行」里那一行负责（它就是上面 TryAcquire 拿到的
                // 占用表项），这里不用再管——原来那个全局心跳已经撤掉（2026-09-05）。
                RefreshDataStatus();
                RefreshFailedCodeCount();
                // 自动重试到点忙就顺延，跟计划天然不打架，而它"等到当天 21:00 之后再试"
                // 的时机是计划排不出来的（数据源盘后逐步更新）。
                // ⚠ 2026-09-02：计划正在跑的时候**不在这里排**——那样每跑完一项就重排一次，
                //   【拉取全部】拆成 13 项后一天要重排十几次。改成计划一轮收尾时统一排一次
                //   （见 StartPlanAsync 传给 PlanRunner 的 onRoundFinished）。
                //   手动点某一行【执行】时 IsPlanRunning 是 false，仍旧立刻排，行为不变。
                if (!IsPlanRunning) ScheduleAutoRetry();
            }
        }
        finally
        {
            // ⚠ 释放占用必须在这一层的 finally：排队中被取消、执行中抛异常、正常收工，
            //    哪条路出去都要放开源——漏一次那个源就永久锁死，之后所有同源任务都被挡。
            //    "收尾做完才释放"也正是【停止全部】判断"全部停干净了"的依据
            //    （见 SourceOccupancy.WaitAllStoppedAsync）。
            Occupancy.Release(lease);
            itemCts.Dispose();
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

            case FetchActionId.FetchEarningsSchedule:
                return _orchestrator.RunFetchEarningsScheduleAsync(progress, ct);

            case FetchActionId.FetchEarningsForecast:
                return _orchestrator.RunFetchEarningsForecastAsync(progress, ct);

            case FetchActionId.FetchLhbSeat:
                return _orchestrator.RunFetchLhbSeatAsync(progress, ct);

            case FetchActionId.FetchMarketEvents:
                return _orchestrator.RunFetchMarketEventsAsync(progress, ct);

            case FetchActionId.FetchStockBoardMap:
                return _orchestrator.RunFetchStockBoardMapAsync(progress, ct);

            case FetchActionId.FetchMoneyFlowDetail:
                // 估的是**补历史**那一段：一只约 2 秒（限流器间隔占大头），按空窗剩余时间估几只。
                // 前面还有个全市场快照（约 60 个请求、一两分钟），它不受这个数控制——
                // 快照是"一整天要么有要么没有"的事，抓一半没有意义。空窗短的话就是快照跑完、
                // 补历史抓不了几只，下轮接着来。
                return _orchestrator.RunFetchMoneyFlowDetailAsync(
                    progress, ct, DeadlineToCount(deadline, TimeSpan.FromSeconds(2)));

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
                    SelectedSource, startYear, endYear, ParseAnnouncementKeywords(item.KeywordsText),
                    progress, ct, item.OverwriteQfq);
            }

            case FetchActionId.OptimizeDatabase:
                return _orchestrator.RunOptimizeDatabaseAsync(progress, ct);

            // ───── 【拉取全部】拆出来的 13 个原子项（2026-09-02）─────
            // 它们调的是编排层 FetchOrchestrator.Steps.cs 里的单项入口，跟【拉取全部】内部走的是
            // 同一段抓取逻辑（那边只是包了个壳、自己建 errors/stats），所以两边行为一致。

            case FetchActionId.StepRoster:
                return _orchestrator.RunStepRosterAndMarketCapAsync(SelectedSource, progress, ct);

            case FetchActionId.StepNetInflow:
                return _orchestrator.RunStepNetInflowAsync(progress, ct, SpecificDayOf(item));

            case FetchActionId.StepAnnouncements:
                return _orchestrator.RunStepAnnouncementsAsync(
                    ParseAnnouncementKeywords(item.KeywordsText), progress, ct, specificDay: SpecificDayOf(item));

            case FetchActionId.StepIndexBars:
                return _orchestrator.RunStepIndexBarsAsync(
                    SelectedSource, ParseLookbackYears(item.LookbackYearsText), progress, ct);

            case FetchActionId.StepStockDayBars:
                // 「只抓某一天」＝原【补指定历史日】那一路（不看水位线、不补断档）
                return item.EffectiveMode == FetchMode.SpecificDay
                    ? _orchestrator.RunStepStockDayBarsForDayAsync(
                        SelectedSource, SpecificDayOf(item) ?? DateTime.Today, progress, ct)
                    : _orchestrator.RunStepStockDayBarsAsync(
                        SelectedSource, ParseLookbackYears(item.LookbackYearsText), progress, ct);

            case FetchActionId.StepStockHfqBars:
                return _orchestrator.RunStepStockHfqBarsAsync(
                    SelectedSource, ParseLookbackYears(item.LookbackYearsText), progress, ct);

            case FetchActionId.StepStockRawBars:
                // 「首次整段回补」＝原【补不复权历史】：把每只补到跟前复权一样长，支持按空窗限量分批
                return item.EffectiveMode == FetchMode.FirstBackfill
                    ? _orchestrator.RunFetchRawBarsAsync(
                        SelectedSource, progress, ct, DeadlineToCount(deadline, TimeSpan.FromSeconds(4)))
                    : _orchestrator.RunStepStockRawBarsAsync(
                        SelectedSource, ParseLookbackYears(item.LookbackYearsText), progress, ct);

            case FetchActionId.StepEtfBars:
                return _orchestrator.RunStepEtfBarsAsync(
                    SelectedSource, ParseLookbackYears(item.LookbackYearsText), progress, ct);

            case FetchActionId.StepDelistedTails:
                return _orchestrator.RunStepDelistedTailsAsync(SelectedSource, progress, ct);

            case FetchActionId.StepBoardIndex:
                return _orchestrator.RunStepSynthesizeBoardIndexAsync(progress, ct);

            case FetchActionId.StepMargin:
                // 「首次整段回补」＝原【一键补齐每日历史】的融资那半边
                return item.EffectiveMode == FetchMode.FirstBackfill
                    ? _orchestrator.RunStepBackfillMarginAsync(progress, ct)
                    : _orchestrator.RunStepMarginRecentAsync(ParseOptionalDate(item.DateText), progress, ct);

            case FetchActionId.StepLhb:
                return item.EffectiveMode == FetchMode.FirstBackfill
                    ? _orchestrator.RunStepBackfillLhbAsync(progress, ct)
                    : _orchestrator.RunStepLhbDayAsync(ParseOptionalDate(item.DateText), progress, ct);

            case FetchActionId.StepDayCoverage:
                return _orchestrator.RunStepDayCoverageCheckAsync(progress, ct);

            // ───── 另外三处复合动作拆出来的（2026-09-02）─────

            case FetchActionId.StepIndexCons:
                return _orchestrator.RunStepIndexConsOnlyAsync(progress, ct);

            case FetchActionId.StepIndexWeight:
                return _orchestrator.RunStepIndexWeightOnlyAsync(progress, ct);

            case FetchActionId.StepEtfIndexMap:
                return _orchestrator.RunStepEtfIndexMapAsync(progress, ct);

            case FetchActionId.StepBoards:
                // 已退役，老计划里还排着的话仍按原样跑（列表+成分股）；
                // FetchPlan.MigrateRetired 会把它换成下面那两项。
                return _orchestrator.RunStepBoardsOnlyAsync(progress, ct);

            case FetchActionId.StepBoardList:
                return _orchestrator.RunStepBoardListAsync(progress, ct);

            case FetchActionId.StepBoardMembers:
                return _orchestrator.RunStepBoardMembersAsync(progress, ct);

            case FetchActionId.StepFullAudit:
                // 纯查库但很重（千万行级比对），推到线程池，别让界面假死
                return Task.Run(() => _orchestrator.RunStepFullAuditAsync(progress, ct, item.ThoroughAudit), ct);

            case FetchActionId.StepReparseBankPdf:
                // 纯 CPU（PDF 解析/OCR），必须推到线程池，理由同 BankRegulatory 那一项
                return Task.Run(() => _orchestrator.RunStepReparseBankReportsAsync(progress, ct), ct);

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

    /// <summary>
    /// 计划行上那个"日期"格（可留空）。留空 = 用今天，这是融资余额/龙虎榜这类**按交易日**的项
    /// 的日常用法；填了就是补那一天。格式不对直接抛——引擎会把这一项记成失败并写清原因，
    /// 比默默按今天跑要好（那样人会以为补上了）。
    /// </summary>
    /// <summary>
    /// 这一行在「只抓某一天」模式下要抓的日期：日期格填了就用它，留空＝今天。
    /// 不是这个模式就返回 null（＝增量，不按天）。
    /// </summary>
    private static DateTime? SpecificDayOf(FetchPlanItem item) =>
        item.EffectiveMode == FetchMode.SpecificDay
            ? ParseOptionalDate(item.DateText) ?? DateTime.Today
            : null;

    private static DateTime? ParseOptionalDate(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        if (!DateOnly.TryParseExact(t, "yyyy-MM-dd", out var date))
            throw new InvalidOperationException($"日期格式不对：\"{t}\"，要 yyyy-MM-dd");
        return date.ToDateTime(TimeOnly.MinValue);
    }
}
