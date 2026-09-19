using System.Collections.Concurrent;
using System.Diagnostics;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// Ties together fetching, local storage, and week/month aggregation. This is the Fetcher
/// program's core, independent of its UI. Everything writes directly into
/// <see cref="FetchPaths.CurrentDb"/> — no separate master/daily output-file production step
/// (removed 2026-07-09, see class remarks history below): that scheme was a holdover from an
/// abandoned no-server multi-machine-via-netdisk sharing design that was never actually wired up
/// on the Analyzer side, so producing those files served no purpose. The real, current workflow
/// (2026-08-21) has no copy step at all: the Analyzer opens this very same
/// <see cref="FetchPaths.CurrentDb"/> read-only — both programs live in the same folder and share
/// one data/local (see AnalyzerPaths). Just run the Fetcher; the Analyzer sees the new data.
///
/// Every run uses exactly one data source, chosen by the caller (see doc/data-platform-design.md
/// section 3.4) — no automatic mixing or failover between vendors, since different vendors can
/// compute derived fields slightly differently and silently blending sources within one dataset
/// is worse than a clear, deliberate manual switch when a source stops working.
///
/// Three fetch modes, sharing the same per-stock fetch/write/aggregate logic
/// (<see cref="ProcessOneStockAsync"/>) and manifest-updating tail (<see cref="FinishFetchRun"/>):
/// - <see cref="RunFetchAsync"/> ("拉取全部"): refreshes the market-wide stock list, then for each
///   stock resumes from wherever it last left off (its own latest day-bar date in the local
///   database) up to today. This makes it safe to stop and re-run at any time — an interrupted
///   run or a handful of per-stock failures just get retried/caught up on the next run, since
///   nothing advances a stock's watermark unless that stock's fetch actually succeeded.
///   末尾还会刷新退市名单、补新退市股缺失的最后几天K线（<see cref="CatchUpDelistedTailsAsync"/>，
///   "拉取当天"末尾同样会跑）——股票一退市数据源就不再有它的新数据，那几天不补就永久缺失了。
/// - <see cref="RunFetchDayAsync"/> ("补指定历史日"，界面按钮2026-07-31从"拉取当天"改名而来，原因见
///   本条末尾的复核结论): re-fetches exactly one caller-specified calendar
///   day for every stock already known locally, ignoring each stock's own watermark, for K线 —
///   does NOT re-scan the market-wide stock LIST for that purpose (run 拉取全部 at least once
///   first). Useful for manually topping up a specific day (e.g. today, after the market closed)
///   without re-scanning the whole market's K线 history — day bars are INSERT OR IGNORE, so
///   re-requesting an already-present day is harmless. **Market cap IS a full market-wide scan
///   even here** (see <see cref="FetchMarketCapAsync"/>/<see cref="SinaListMarketCapFetcher"/>) —
///   a deliberate exception, confirmed acceptable by the user (2026-07-08) even though it makes
///   拉取当天 slower than just "K线 for known stocks" would otherwise be.
///
///   ⚠️ 2026-07-31 复核（用户问"这两个是不是没区别"，答案基本是"日常确实没区别"）：稳态下（昨天跑过、
///   今天再跑）两者的实际动作几乎完全重合——都要扫一遍全市场流通市值、都要逐只请求约7400个标的的
///   1天K线、资金净流入都是逐只一次请求、指数与ETF**两边都走水位线增量**（不是"只抓这一天"）、融资/
///   龙虎/板块指数合成也一样；唯一较明显的差别是公告窗口（全部=近14天 vs 当天=1天），相对7400次逐只
///   请求可以忽略。请求条数相同 + 同一个限速器（maxConcurrency 3、每请求间隔1秒）⇒ 耗时必然相同，
///   实测日常运行都约120分钟。
///   两者真正的差别只有一条，但很关键：**"拉取全部"按每只标的自己的水位线补齐任意长度的断档，
///   "拉取当天"只抓指定的那一天、漏掉的日子会永久留空**。实证：2026-07-30 那次"拉取全部"自动补回了
///   07-28、07-29 两天约5500只个股的日K（那两晚没跑完个股），耗时也因此从约120分钟变成244分钟。
///   ⇒ 结论：日常收盘后一律用"拉取全部"；"拉取当天"只在"要补某个过去的具体日期"时才有意义
///   （"拉取全部"永远跑到今天，做不到这件事）。界面 ToolTip 已按此说明改写。
///   **这是用户的日常入口**（2026-07-29确认："拉取全部"只有第一次会点），所以退市股收尾也挂在这里。
/// - <see cref="RunRetryFailedAsync"/> ("重新拉取失败股票"): retries whatever's recorded in the
///   three failed-code lists on <see cref="Manifest"/>, without re-scanning the market list or
///   re-running announcements.
/// - <see cref="RunFetchYearAsync"/> ("拉取指定年份区间", 2026-07-29新增，同日从单年改为区间): 往回补
///   [起始年, 结束年] 的历史——把区间里"能取到历史的"各类数据一次取齐（K线/**退市股名单与历史**/资金净
///   流入/融资余额/龙虎榜/公告），逐标的、逐交易日只补本地还缺的部分。起止相同即单年。快照型数据（流通
///   市值、板块行情与成分、指数成分与权重）天生只有"当下"、没有历史可取，会明确跳过并在日志里说明原因。
///   注意它只往**后**补（补到各标的本地最早那天为止），不会抓今天的新数据——日常增量仍靠"拉取全部"。
/// </summary>
public partial class FetchOrchestrator
{
    /// <summary>
    /// 「我还活着」的旁路通道（2026-09-08）——只写日志，**不算进度**。
    ///
    /// 为什么不复用 progress：调度层拿 progress 当心跳判断任务卡没卡死（见 QuietWatchdog），
    /// 而这条通道播的是"仍在建索引，已用时 8 分钟"这类定时话术，它证明不了有前进——
    /// 卡在写锁上时它照样按时吐。混进 progress 就等于让卡死的任务自己给自己开健康证明。
    ///
    /// 调用方（MainViewModel）把它接到日志上即可；不接就是不播报，功能不受影响。
    /// </summary>
    public Action<string>? Liveness { get; set; }

    /// <summary>
    /// 待办自己补的那些任务的转交口（2026-09-18）——见 <see cref="ITaskBacklogRunner"/>。
    /// null＝没注入，所有待办都按老路在这里编排（行为跟 2026-09-18 之前一致）。
    /// </summary>
    public ITaskBacklogRunner? BacklogRunner { get; set; }

    private readonly FetchPaths _paths;
    private readonly IManifestStore _manifestStore;
    private readonly IFundamentalMetricRepository _fundamentalRepository;
    private readonly IMarketCapFetcher _marketCapFetcher;
    private readonly INetInflowFetcher _netInflowFetcher;
    private readonly AnnouncementFetchOrchestrator _announcementOrchestrator;
    /// <summary>
    /// ⚠ 不是 readonly：<see cref="ReplaceBoardFetcher"/> 会在运行期换掉它（【重新读取配置】按钮）。
    /// 这一条是 <c>BoardMemberChannel</c>/<c>Push2NetworkInterface</c> 两个设置的落点——
    /// 它们决定的是**造哪个类、用什么限流参数**，光重读文件不换对象是不生效的。
    /// </summary>
    private IBoardFetcher _boardFetcher;
    private readonly IBoardRepository _boardRepository;

    /// <summary>
    /// 板块名单的主源（2026-09-05）：东财行情中心左侧菜单那份静态 JSON，一个请求拿全量、
    /// 不碰 push2、不会弹验证。给了就优先走它，拿不到才退回 <see cref="_boardFetcher"/> 的
    /// push2 分页。详见 <see cref="Remote.EastMoneySideMenuBoardListProvider"/>。
    /// </summary>
    private readonly Remote.EastMoneySideMenuBoardListProvider? _sideMenuBoardList;
    private readonly IStockListProvider? _etfListProvider;

    /// <summary>ETF 名单比库里存量少这个比例以上就判定为"半截名单"、改用存量兜底。
    /// 5% 这个阈值沿用 <c>TotalSharesTask.MinKeepRatio</c>。</summary>
    private const double EtfListMinKeepRatio = 0.95;
    private readonly IIndexConsProvider _indexConsProvider;
    private readonly IIndexWeightProvider _indexWeightProvider;
    private readonly ILhbProvider _lhbProvider;
    private readonly IIndexConsRepository _indexRepository;
    private readonly ILhbRepository _lhbRepository;
    // 股东这两个依赖 2026-09-18 起本类已经不用了（整项迁去 ShareholderTask）。字段和构造
    // 参数暂留：删它们要动这个 40 个参数的构造签名和所有调用点，跟分红那次的处理保持一致。
    private readonly IShareholderProvider _shareholderProvider;
    private readonly IShareholderRepository _shareholderRepository;
    private readonly IMarginProvider _marginProvider;
    private readonly IMarginRepository _marginRepository;
    /// <summary>交易日历（2026-09-08，TradingDay 表）——逐日回补靠它跳过节假日。
    /// 可空：没配就退回"只跳周末"的老行为，见 <see cref="LoadTradingCalendar"/>。</summary>
    private readonly ITradingDayRepository? _tradingDayRepository;
    /// <summary>"确认这天就是没有"的名单（2026-09-08，DailyFetchNoData 表）。可空同上。</summary>
    private readonly IDailyFetchNoDataRepository? _dailyNoDataRepository;
    private readonly IDelistedListProvider? _delistedListProvider;
    private readonly IFinancialProvider? _financialProvider;
    private readonly IDividendProvider? _dividendProvider;
    private readonly IDividendRepository? _dividendRepository;
    private readonly Remote.CninfoPrebookProvider? _prebookProvider;
    /// <summary>业绩预告/快报（2026-09-03，东财）。没有回退源——新浪/腾讯/交易所都不提供结构化预告，
    /// 巨潮只有公告原文。所以东财不可用时这一项整体跳过，不像板块那样有备胎。</summary>
    private readonly Remote.EastMoneyEarningsForecastProvider? _forecastProvider;
    private readonly IEarningsForecastRepository? _forecastRepository;
    /// <summary>分档资金流（2026-09-03，东财 push2his）。跟 NetInflow 是同一件事的不同精度。</summary>
    // 类型是接口而不是那个具体的 provider（2026-09-14）：逐股补历史现在有两条通道
    // （HttpClient／真浏览器），这里只用它报熔断状态，谁在跑都一样。
    private readonly Logic.Abstractions.IMoneyFlowDetailFetcher? _moneyFlowProvider;
    /// <summary>分档资金流的全市场当日快照（2026-09-06，push2delay）。跟上面那个是同一份数据的两种切法。</summary>
    private readonly Remote.EastMoneyMoneyFlowSnapshotProvider? _moneyFlowSnapshotProvider;
    private readonly INetInflowDetailRepository? _moneyFlowRepository;
    /// <summary>机构调研/限售解禁/股东增减持（2026-09-03，东财）。本地此前全都没有。
    /// ⚠ 大宗交易 2026-09-17 拆出去了，见 <c>BlockTradeTask</c>——它要按日整日替换，
    /// 跟这三张"按年切片、几分钟跑完"的节奏对不上。</summary>
    private readonly Remote.EastMoneyMarketEventProvider? _marketEventProvider;
    private readonly IMarketEventRepository? _marketEventRepository;

    /// <summary>
    /// 市场事件表增量时额外往前回看的天数（见 <see cref="RunFetchMarketEventsAsync"/> 里
    /// RunOne 的注释）。
    ///
    /// 这个 30 天原本是为**大宗交易的滞后字段**定的（"事件后 N 日涨跌幅"最长 20 个交易日
    /// ≈ 28 自然日）。大宗 2026-09-17 拆去 <c>BlockTradeTask</c> 之后，留给剩下三张表的作用
    /// 变成"公告补发/修订的兜底"——那三张都是按年切片的公告类数据，多抓一小段成本极低。</summary>
    /// </summary>
    private const int LaggingFieldLookbackDays = 30;
    /// <summary>个股行业/题材归属（2026-09-03，东财 datacenter）。补证监会分类的粒度不足。</summary>
    private readonly Remote.EastMoneyStockBoardMapProvider? _boardMapProvider;
    private readonly IStockBoardMapRepository? _boardMapRepository;


    /// <summary>
    /// 板块层级树的来源（2026-09-07）：东财终端落在本地的一份文件，不联网、不占抓取配额。
    /// 传 null 就是这一项没数据——层级树只是让板块能往上卷成一级行业，缺了不影响任何现有功能。
    /// </summary>
    private readonly Local.EastMoneyTerminalHierarchyProvider? _boardHierarchy;
    private readonly object _dbLock = new();

    // 拉取全部对同一批关键词、同一天窗口重复扫描是安全的（OrderWinAnnouncement 主键去重），所以
    // 不需要像K线那样维护"上次抓到哪"的水位线，固定回看这么多天足够覆盖两次拉取全部之间的间隔，
    // 代价很小（cninfo全文检索本来就比逐只股票查K线快得多）。
    private const int AnnouncementLookbackDaysForFetchAll = 14;

    // 主力净流入表里还没有记录的股票（新股票/第一次跑），从截止日往前回溯这么多天开始补，
    // 覆盖"耀哥法"新规则要看的最近3天再留足缓冲，不需要跟K线的lookbackYears一样长。
    private const int NetInflowInitialLookbackDays = 60;

    // "拉取当天"/"重新拉取失败股票"里，遇到本地完全没有K线历史的股票（尤其是"拉取当天"扫市值时
    // 顺带发现的新股）时的回看年数——这两个入口没有像"拉取全部"那样的用户可调回看框，用这个固定
    // 值兜底，跟"拉取全部"的默认值保持一致。数据源只会返回上市日之后的数据，请求这么长的窗口对
    // 新股实际只会拿到"上市→当天"的完整历史，不会有多余。
    private const int DefaultLookbackYears = 3;

    /// <summary>"拉取指定年份"允许的最早年份——A股1990年底开市，再早没有任何数据可抓。</summary>
    private const int FirstAShareYear = 1990;

    /// <summary>
    /// A股开市首日（上交所第一个交易日）。区间回补的起点会被钳到这一天，**不是为了少抓那 11 个月**，
    /// 而是因为填 1990 会让整个"跳过已补齐标的"的优化静默失效：
    ///
    /// <see cref="YearGapCalculator"/> 的跳过分支要求 <c>calendar.CoversFrom(yearStart)</c>——日历
    /// 是从库里 day_raw 归纳的，它的首日就是这天，于是 <c>CoversFrom(1990-01-01)</c> 恒为 false，
    /// 那个分支一次都不会命中。后果是个股三套日线 + ETF + 指数的**每一只**都至少发一次请求，
    /// 包括 2020 年才上市、那段必然为空的票（2026-07-30 实测：光这个判断能省 19 分钟、1150 个请求）。
    ///
    /// ⚠ 为什么用硬常量而不是"日历自己的首日"：那正是 2026-09-06 漏抓 2360 只老股的成因——日历
    /// 缺哪段就瞎哪段，拿它当"市场起点"会把"日历不知道"误读成"确实没开市"。开市日是事实，不是推断。
    /// </summary>
    private static readonly DateTime AShareMarketOpen = new(1990, 12, 19);

    /// <summary>后复权阶段的熔断门槛：完成这么多只之后才开始判断失败率（样本太少容易被偶发失败误伤）。</summary>
    private const int HfqAbortCheckAfter = 30;

    /// <summary>
    /// 复权基准漂移的比对回看天数（2026-07-30新增）。数据源一页固定返回 640 根K线（≈2.5年），
    /// 不管我们请求几天——所以把"确实要抓"的窗口向前放宽到这个天数**不增加任何网络请求**，
    /// 却能拿回几百根"数据源当前基准下的正确值"用来跟库里比对。注意只在本来就要发请求时放宽，
    /// 水位线判定的跳过逻辑不变（否则每只股票每天都要发一次请求，"拉取全部/当天"会全面变慢）。
    /// </summary>
    private const int DriftCheckLookbackDays = 400;

    /// <summary>财务报表每轮最多抓多少只（2026-08-27）。新浪的 vDOWN 报表接口配额很严（见
    /// Fetcher/App.xaml.cs 里那段限速注释），降速后约 10 请求/分钟、每只 3 个请求，所以 300 只
    /// 差不多要 1.5 小时。没抓完的下轮自动继续——靠 FinancialFetchState 记录的报告期和科目集
    /// 版本断点续传，抓过的不会重抓。全市场 5780 只分几天补齐，而不是一次跑 24 小时。</summary>

    /// <summary>
    /// 判定某根K线是否发生了复权基准漂移：库里存的值与数据源当前给出的值不一致。
    /// 阈值取"相对 0.2% 与绝对 0.005 元的较大者"——足够小以捕捉几分钱的现金分红调整，
    /// 又不会被浮点噪声和低价股的分位误差误判。
    /// </summary>
    private static bool IsDrifted(double stored, double fresh) =>
        Math.Abs(stored - fresh) > Math.Max(0.005, Math.Abs(fresh) * 0.002);

    /// <summary>判据本体在 <see cref="IncrementalWindowCalculator"/>（Logic 层纯函数，有单测覆盖）——
    /// 这里只是转发，别在这儿再写一份。2026-09-09 抽走的理由见那个类的注释。</summary>
    private static bool IsConfirmedFinal(DateTime fetchedAt, DateTime tradingDay) =>
        IncrementalWindowCalculator.IsConfirmedFinal(fetchedAt, tradingDay);

    /// <summary>同上，转发给 <see cref="IncrementalWindowCalculator.IncrementalStart"/>。</summary>
    private static DateTime IncrementalStart(
        (DateTime PeriodStart, DateTime FetchedAt)? latest, DateTime end, int lookbackYears) =>
        IncrementalWindowCalculator.IncrementalStart(latest, end, lookbackYears);

    /// <summary>"mm\:ss"格式的TimeSpan在超过1小时后会把小时部分直接丢掉（比如1小时5分12秒会被
    /// 打印成"05:12"，看起来像是时间变短了/重置了，而不是继续在涨）——全市场扫描现在经常跑到
    /// 一小时以上，这个格式化统一换成超过1小时时带上小时数。</summary>
    /// 实现在 <see cref="ElapsedText.Format"/>——这里只转发，好让 34 处调用点不用动
    /// （2026-09-10：同一份实现原来在 FullAuditTask 里也有一份）。
    private static string FormatElapsed(TimeSpan elapsed) => ElapsedText.Format(elapsed);

    public FetchOrchestrator(
        FetchPaths paths,
        IManifestStore manifestStore,
        IFundamentalMetricRepository fundamentalRepository,
        IMarketCapFetcher marketCapFetcher,
        INetInflowFetcher netInflowFetcher,
        AnnouncementFetchOrchestrator announcementOrchestrator,
        IBoardFetcher boardFetcher,
        IBoardRepository boardRepository,
        IIndexConsProvider indexConsProvider,
        IIndexWeightProvider indexWeightProvider,
        ILhbProvider lhbProvider,
        IIndexConsRepository indexRepository,
        ILhbRepository lhbRepository,
        IShareholderProvider shareholderProvider,
        IShareholderRepository shareholderRepository,
        IMarginProvider marginProvider,
        IMarginRepository marginRepository,
        IStockListProvider? etfListProvider = null,
        IDelistedListProvider? delistedListProvider = null,
        IFinancialProvider? financialProvider = null,
        IDividendProvider? dividendProvider = null,
        IDividendRepository? dividendRepository = null,
        Remote.CninfoPrebookProvider? prebookProvider = null,
        Remote.EastMoneyEarningsForecastProvider? forecastProvider = null,
        IEarningsForecastRepository? forecastRepository = null,
        Logic.Abstractions.IMoneyFlowDetailFetcher? moneyFlowProvider = null,
        INetInflowDetailRepository? moneyFlowRepository = null,
        Remote.EastMoneyMarketEventProvider? marketEventProvider = null,
        IMarketEventRepository? marketEventRepository = null,
        Remote.EastMoneyStockBoardMapProvider? boardMapProvider = null,
        IStockBoardMapRepository? boardMapRepository = null,
        Remote.EastMoneySideMenuBoardListProvider? sideMenuBoardList = null,
        Remote.EastMoneyMoneyFlowSnapshotProvider? moneyFlowSnapshotProvider = null,
        Local.EastMoneyTerminalHierarchyProvider? boardHierarchy = null,
        ITradingDayRepository? tradingDayRepository = null,
        IDailyFetchNoDataRepository? dailyNoDataRepository = null)
    {
        _tradingDayRepository = tradingDayRepository;
        _dailyNoDataRepository = dailyNoDataRepository;
        _boardHierarchy = boardHierarchy;
        _sideMenuBoardList = sideMenuBoardList;
        _boardMapProvider = boardMapProvider;
        _boardMapRepository = boardMapRepository;
        _moneyFlowProvider = moneyFlowProvider;
        _moneyFlowSnapshotProvider = moneyFlowSnapshotProvider;
        _moneyFlowRepository = moneyFlowRepository;
        _marketEventProvider = marketEventProvider;
        _marketEventRepository = marketEventRepository;
        _prebookProvider = prebookProvider;
        _forecastProvider = forecastProvider;
        _forecastRepository = forecastRepository;
        _paths = paths;
        _manifestStore = manifestStore;
        _fundamentalRepository = fundamentalRepository;
        _marketCapFetcher = marketCapFetcher;
        _netInflowFetcher = netInflowFetcher;
        _announcementOrchestrator = announcementOrchestrator;
        _boardFetcher = boardFetcher;
        _boardRepository = boardRepository;
        _indexConsProvider = indexConsProvider;
        _indexWeightProvider = indexWeightProvider;
        _lhbProvider = lhbProvider;
        _indexRepository = indexRepository;
        _lhbRepository = lhbRepository;
        _shareholderProvider = shareholderProvider;
        _shareholderRepository = shareholderRepository;
        _marginProvider = marginProvider;
        _marginRepository = marginRepository;
        _etfListProvider = etfListProvider;
        _delistedListProvider = delistedListProvider;
        _financialProvider = financialProvider;
        _dividendProvider = dividendProvider;
        _dividendRepository = dividendRepository;
    }

    // ── 2026-09-08：【手动】页撤掉时一并删掉的四个"整包"方法 ─────────────────────────
    //   RunFetchAsync（拉取全部）／RunFetchDayAsync（补指定历史日）／RunFetchBoardsAsync（拉取板块）
    //   ／RunBackfillDailyHistoryAsync（一键补齐每日历史）／RunFetchPeriodicAsync（一键拉取定期数据）。
    //   它们 2026-09-02 就被拆成了计划里的原子项（见 FetchOrchestrator.Steps.cs 和
    //   FetchTaskCatalog.RetiredInto），此后只剩【手动】页那几个按钮还在调；那一页撤掉之后
    //   全无调用方。同一件事留两条实现路径，改了一边忘另一边是迟早的事，所以直接删。
    //   它们串起来的每一步（FetchMarketCapAsync / FetchStockDayBarsAsync / FetchBoardsCoreAsync …）
    //   都还在，被各自的 RunStepXxxAsync 单项入口调用。

    /// <summary>
    /// 只抓板块行情与成分股、写库，**不合成板块指数**（2026-09-02 从 RunFetchBoardsAsync 抽出）。
    /// 抽出来是为了让"抓取"和"本地合成"能各自成为一个计划项：合成是纯 CPU、失败原因和重跑代价
    /// 都跟联网抓取完全不同，混在一起时"这一项失败了要不要重试"没法判断。
    /// </summary>
    /// <summary>
    /// 各数据源当前的限流熔断状态，给界面显示用（2026-09-04）。
    /// 没有它的话，熔断期间顶上只写"空闲"——人会以为随时能开工，点了才发现干等十几分钟。
    /// </summary>
    public IReadOnlyList<(string Source, DateTime Until)> GetPausedSources()
    {
        var list = new List<(string, DateTime)>();
        if (_boardFetcher is Remote.EastMoneyBoardFetcherBase emb && emb.PausedUntil is { } a)
            list.Add(("东财行情", a));
        if (_moneyFlowProvider?.PausedUntil is { } b)
            list.Add(("东财 push2his", b));
        if (_moneyFlowSnapshotProvider?.PausedUntil is { } c)
            list.Add(("东财 push2delay", c));
        return list;
    }

    /// <summary>
    /// 换掉板块成分股的取数通道（2026-09-05，给【重新读取配置】用）。
    ///
    /// 为什么非换对象不可：<c>BoardMemberChannel</c> 决定的是 <see cref="Remote.EastMoneyBoardHttpFetcher"/>
    /// 还是 <see cref="Remote.EastMoneyBoardFetcher"/>，<c>Push2NetworkInterface</c> 是构造参数——
    /// 两个都是"造的时候就定死"的东西，重读一遍配置文件不换对象等于没改。
    ///
    /// ⚠ 只能在**没有任务在跑**的时候调。抓取过程里 <c>OnStatus</c> 是临时订阅、用完就退订的
    /// （见 FetchBoardsCoreAsync），跑到一半换掉字段会让那个 -= 退订到新对象上、旧对象的
    /// 事件挂着不放。调用方（MainViewModel.ReloadConfig）已经用 IsBusy 挡住了。
    /// </summary>
    public void ReplaceBoardFetcher(IBoardFetcher fetcher) => _boardFetcher = fetcher;

    /// <summary>板块成分股这一轮之后的存量进度，给界面显示用（见 FetchResult.Progress）。</summary>
    private string? _memberProgressText;

    /// <summary>
    /// 成分股「抓过多久算还新鲜」——这么久之内抓过的板块本轮跳过。
    ///
    /// 2026-09-06 从写死的 7 天改成**问当前通道要**（<see cref="IBoardFetcher.MemberFreshFor"/>）：
    /// 这个值的本质是重抓一遍的代价，而各通道差着几个数量级——push2 那几条跑一轮三小时起，
    /// 读本地文件的那条几毫秒。一个常量伺候不了两种情况。
    ///
    /// ⚠ 抓取那边（FetchBoardMembersCoreAsync）和界面上的待抓计数（GetPendingBoardMemberCount）
    /// 必须用**同一个**值，不然会出现"界面说还剩 300 个、跑起来说 0 个要抓"这种对不上的情况。
    /// 两处都读这里，所以 <see cref="ReplaceBoardFetcher"/> 换掉通道时它们会一起变。
    /// </summary>
    private DateTime BoardMemberFreshSince
    {
        get
        {
            var fresh = _boardFetcher.MemberFreshFor;
            // <= 0 ＝ 不节流，每轮全量覆盖。把时间线推到 MaxValue，没有任何记录算得上
            // "新鲜"，于是全部重抓一遍——读本地文件的通道就是这样（948 个板块 1 秒）。
            return fresh <= TimeSpan.Zero ? DateTime.MaxValue : DateTime.Today - fresh;
        }
    }

    /// <summary>
    /// 统计/展示"抓得怎么样了"用的时间界——**刻意跟 <see cref="BoardMemberFreshSince"/> 分开**
    /// （2026-09-07）。
    ///
    /// 那个值回答的是"要不要重抓"，不节流的通道下它是 <see cref="DateTime.MaxValue"/>，
    /// 含义是"没有任何记录算新鲜、全部重抓一遍"——对抓取决策完全正确。但拿同一个值去做
    /// **统计**就全错了：`fetched_at >= MaxValue` 恒为假，于是每个板块都落进"待重试"。
    /// 09-07 那轮 terminal 通道 1031 个板块**全部抓成功**，日志却写着
    /// 「最新 0 个、待重试 1031 个」，后面还跟一句"再跑一次会从没抓到的接着来"——
    /// 照着它再跑一轮纯属白跑。界面上的待抓计数是同一个毛病，注释里写的"这个数应该会归零"
    /// 在这条通道上永远不成立。
    ///
    /// 所以统计换一个有意义的界：不节流的通道按**今天抓过就算最新**（它每轮全量刷新
    /// `fetched_at`，跑完一秒钟的事，今天跑过就是全新的）；节流的通道跟抓取判据保持一致，
    /// 免得出现"界面说还剩 300 个、跑起来说 0 个要抓"。
    /// </summary>
    /// <param name="memberFreshFor">当前通道的新鲜期，见 <see cref="IBoardFetcher.MemberFreshFor"/>。</param>
    /// <param name="today">当天零点（传进来是为了可测）。</param>
    public static DateTime BoardMemberStatsSince(TimeSpan memberFreshFor, DateTime today)
        => memberFreshFor > TimeSpan.Zero ? today - memberFreshFor : today;

    /// <summary>当前通道的统计时间界，见 <see cref="BoardMemberStatsSince(TimeSpan, DateTime)"/>。</summary>
    private DateTime BoardMemberStatsSinceNow
        => BoardMemberStatsSince(_boardFetcher.MemberFreshFor, DateTime.Today);

    /// <summary>
    /// 板块成分股还剩多少个板块要抓（2026-09-06 新增，界面任务行里显示）——
    /// 返回 (本轮要抓的板块数, 其中从没抓过的, 板块总数)，取不到返回 null。
    ///
    /// 跟【分档资金流】那个计数一样，这一项也是「跨好几轮才做得完」的活：1031 个板块 ≈ 2500 个
    /// push2 请求，限流下一轮跑不完是常态。但跟资金流不同的是，**这个数应该会归零**——
    /// 判据是"7 天内抓过没有"，不是"今天抓过没有"，所以补完一轮之后它会一直是 0，
    /// 直到某个板块的记录满 7 天才重新出现。
    ///
    /// ⚠ 判据用 <see cref="BoardMemberStatsSinceNow"/> 而不是 <see cref="BoardMemberFreshSince"/>
    /// （2026-09-07 修）：后者在不节流的通道下是 MaxValue，会让这个数永远等于板块总数，
    /// 上面那句"补完一轮之后它会一直是 0"在那条通道上永远不成立。
    /// </summary>
    public (int Todo, int Never, int Total)? GetPendingBoardMemberCount()
    {
        try
        {
            var (ok, stale, never) = _boardRepository.GetMemberFetchProgress(BoardMemberStatsSinceNow);
            var total = ok + stale + never;
            return total == 0 ? null : (stale + never, never, total);
        }
        catch { return null; }
    }

    /// <summary>
    /// 还在限流熔断里吗——是的话返回该说的话，调用方直接收工。
    /// 进去也只是在限流器里干等到超时报失败（实测干等过 8 分钟），不如把话说清楚。
    /// </summary>
    private string? Push2PausedReason()
    {
        if (_boardFetcher is not Remote.EastMoneyBoardFetcherBase emb) return null;
        if (emb.PausedUntil is not { } until) return null;
        var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
        return $"东财行情接口限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
    }

    /// <summary>
    /// 只抓**板块名单和行情快照**（2026-09-04 从原来的板块任务里拆出来）。约 10 个请求，很快。
    ///
    /// 为什么要跟成分股分开：限流器现在是"每 15 个请求主动歇 2 分钟"（东财实测连发 16~35 个
    /// 就被切），而列表开头就要 9~10 个请求——合在一起时每轮三分之二的配额花在列表上，
    /// 只剩 5 个才轮到那 2500 个成分股请求。而且列表一挂整项就退出，成分股一个都跑不成，
    /// 可库里明明有上一次的名单、照样能接着抓成分。
    /// </summary>
    /// <returns>没开工时返回原因；正常跑完返回 null。</returns>

    /// <summary>
    /// 把东财终端本地文件里的板块父子关系导进 Board.parent_code / Board.board_level（2026-09-07）。
    ///
    /// 补的是一个说小不小的窟窿：我们库里只有"股票 → 属于哪个板块 + 第几级"，**没有"板块 → 父板块"**。
    /// 少了这层，三级行业就没法往上卷成一级来看，而风口分析里"这波钱落在哪个大行业"恰恰要按一级聚合。
    /// 东财网页侧不给这份数据（查过 /cjhy/、站内搜、板块页面都没有），终端把它落在了本地。
    ///
    /// ⚠ 那个文件格式是**逆向出来的、没有文档**，所以写库前拿 <c>StockIndustryEm.board_level</c>
    ///   对一遍——那是走网络接口拿的，跟本地文件完全独立的一份来源。东财哪天改了文件结构，
    ///   解析结果会是一堆"看着像模像样的错关系"，不校验的话没人会发现。
    ///
    /// 三种情况都不算失败，只记一句：没装终端（文件不存在）、库里还没有 StockIndustryEm（对不了）、
    /// 校验不过（这一轮不写，留着上一次的树）。层级树缺了不影响任何现有功能。
    /// </summary>
    private void ImportBoardHierarchy(ConcurrentBag<string> errors, IProgress<string>? progress)
    {
        if (_boardHierarchy == null) return;

        void Forward(string m) => progress?.Report(m);
        _boardHierarchy.OnStatus += Forward;
        List<Logic.Models.BoardHierarchyEdge> edges;
        try { edges = _boardHierarchy.Read(); }
        catch (Exception ex)
        {
            progress?.Report($"⚠ 板块层级树读取失败（{ex.Message}），库里保留上一次的树。");
            return;
        }
        finally { _boardHierarchy.OnStatus -= Forward; }

        if (edges.Count == 0) return;   // 文件不存在时 Read 自己已经报过一句了

        // ── 交叉校验：拿 StockIndustryEm 还原出的真实父子链逐条对 ──
        //
        // ⚠ 对的是**父子归属**，不是层级数字。这是拿真数据换来的教训：解析器第一版用层级栈，
        //   层级数字 932 处全对、看着毫无破绽，实际 51 条边的父是错的（"银行Ⅱ 挂在石油石化下"）。
        //   错位之后每个子板块照样挂在一个层级正确的父上，只对层级的校验会一路放行。
        var known = _boardMapRepository?.GetBoardParents();
        if (known is { Count: > 0 })
        {
            var bad = new List<string>();
            int compared = 0;
            foreach (var e in edges)
            {
                if (!known.TryGetValue(e.BoardCode, out var truth)) continue;   // 我们没有的板块，对不了
                compared++;
                if (truth.Level != e.Level)
                    bad.Add($"{e.BoardCode} 文件说 {e.Level} 级、库里是 {truth.Level} 级");
                else if (truth.Parent != null && !string.Equals(truth.Parent, e.ParentCode, StringComparison.OrdinalIgnoreCase))
                    bad.Add($"{e.BoardCode} 文件说父是 {e.ParentCode}、库里是 {truth.Parent}");
            }

            // 拒绝的门槛：**一条都不能错**。这不是洁癖——对不上通常意味着解析错位，
            // 那种情况下剩下那些"对得上"的边也未必是真的，挑着写进去比整批不写更糟。
            if (bad.Count > 0)
            {
                var sample = string.Join("；", bad.Take(5));
                progress?.Report($"⚠ 板块层级树跟库里的行业分类对不上（{bad.Count}/{compared} 条，如 {sample}），"
                               + "这一轮不写，库里保留上一次的树。多半是东财改了文件格式——"
                               + "去看 EastMoneyTerminalHierarchyProvider 的格式说明。");
                errors.Add($"板块层级树校验未通过（{bad.Count}/{compared} 条父子关系不一致），已拒绝写入。");
                return;
            }
            progress?.Report($"板块层级树校验通过：{compared} 条父子关系跟库里的行业分类逐条一致。");
        }
        else
        {
            // StockIndustryEm 还没抓过。写还是不写？写——层级树本身不会让任何现有数据变糟，
            // 而且【个股行业题材】那一步跑完之后下一轮自然就校验上了。
            progress?.Report("⚠ 库里还没有东财行业分类，板块层级树这一轮没法交叉校验，先按文件写入。");
        }

        var (updated, unknown, cleared) = _boardRepository.UpdateHierarchy(edges);
        progress?.Report($"板块层级树已更新：{updated} 个板块写入父子关系"
                       + (cleared > 0 ? $"（覆盖原有 {cleared} 行）" : "")
                       + (unknown > 0 ? $"；另有 {unknown} 个板块终端有、我们的板块表里没有，已跳过。" : "。"));
    }

    private async Task<string?> FetchBoardListCoreAsync(
        ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        var skipped = await FetchBoardListNamesAsync(errors, progress, ct);

        // 层级树跟在名单后面跑（2026-09-07）。**不管名单这一轮成没成功都跑**：
        // 它只读本地一份文件、不发请求，而正表里的板块名单就算是上一轮的，父子关系照样对得上。
        // 名单被限流挡住的那些轮，正好是最该把这种"不花配额的活"干掉的时候。
        ImportBoardHierarchy(errors, progress);

        return skipped;
    }

    /// <summary>名单本体，三条路依次退：终端本地文件 → 菜单 JSON → push2 分页。</summary>
    private async Task<string?> FetchBoardListNamesAsync(
        ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        _boardRepository.EnsureSchema();

        // ══ 最优先（2026-09-06）：东财终端落在本地的那份文件 ══
        //
        // 成分股已经走它了（BoardMemberChannel=terminal），名单也走同一份，图的是
        // **同源同时点**。名单和成分来自两个源时会对不上，而且对不上的那一个每轮都失败：
        // BK1362 就是活例子——sidemenu 的名单里有它，终端文件里没有，于是成分股那步
        // 每轮都为它报一次"板块不在本地文件里"。两边同源之后这类不一致从根上消失。
        //
        // 这一步本来就只取名单、不取行情（涨跌幅/成交额由【板块指数合成】用本地日K回填），
        // 所以本地文件够用。取不到就往下走菜单 JSON，名单这一项不会因此停摆。
        if (_boardFetcher is Remote.EastMoneyTerminalBoardFetcher term)
        {
            void ForwardTerm(string s) => progress?.Report(s);
            term.OnStatus += ForwardTerm;
            try
            {
                if (term.TryGetBoardList(out var fromFile, out var termMsg))
                {
                    progress?.Report(termMsg);
                    return CommitBoardListFromMenu(fromFile, errors, progress);
                }
                // ⚠ 走到这儿意味着**这一轮真的要发请求**，而 terminal 通道下这一项在占用表里
                // 是按"纯本地"登记的（见 FetchTaskCatalog.IsLocalOnlyNow）——也就是说它可能
                // 正跟别的东财任务并发跑。这是有意的取舍（要两层同时失效才会走到这儿，
                // 而菜单 JSON 只有 1 个请求），但**必须让人看见**：真被限流时，
                // 日志里没这一句的话，谁也想不到"不占源的那一项"会去打东财。
                progress?.Report($"⚠ {termMsg}——板块名单本轮改走网络（菜单 JSON，再不行退回 push2 分页）。"
                               + "注意：terminal 通道下这一项不登记数据源占用，可能跟别的东财任务同时在跑。");
            }
            finally { term.OnStatus -= ForwardTerm; }
        }

        // ══ 主路（2026-09-05）：行情中心左侧菜单那份静态 JSON，一个请求拿全量、不碰 push2 ══
        //
        // 换过来的理由和等价性实测见 EastMoneySideMenuBoardListProvider 的类注释（概念 504 个
        // 代码名称一个不差，行业只多一个三级行业）。收益不在"省下这 10 个请求"，而在于
        // 这一项从此**不需要人守着过图片验证码**，整轮 push2 配额也全留给了成分股。
        //
        // push2 那条路留着当回退：万一东财哪天把这个文件挪走或改结构，还能退回去抓。
        if (_sideMenuBoardList != null)
        {
            void ForwardMenu(string s) => progress?.Report(s);
            _sideMenuBoardList.OnStatus += ForwardMenu;
            try
            {
                var all = await _sideMenuBoardList.FetchBoardListAsync(ct);
                return CommitBoardListFromMenu(all, errors, progress);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 取不到/解析不了才退回 push2。**护栏拒绝不走到这儿**——那是
                // CommitBoardListFromMenu 自己返回原因，数据可疑时再去烧 push2 配额没有意义。
                progress?.Report($"⚠ 板块菜单取数失败（{ex.Message}）——"
                               + "退回 push2 分页抓取，会慢很多、而且可能要人过图片验证。");
                errors.Add($"板块菜单取数失败，已退回 push2：{ex.Message}");
            }
            finally { _sideMenuBoardList.OnStatus -= ForwardMenu; }
        }

        // ══ 回退：push2 clist 分页（约 10 个请求，会撞验证码）══
        if (Push2PausedReason() is { } paused)
        {
            progress?.Report($"{paused}，本轮不开工。库里保留上一次的板块名单。");
            return paused;
        }

        void Forward(string s) => progress?.Report(s);
        _boardFetcher.OnStatus += Forward;
        try
        {
            if (_boardFetcher is Remote.EastMoneyBoardFetcherBase em2)
            {
                // 浏览器通道初始化要几秒（建 WebView2 + 打开东财页面拿 Cookie），
                // 放在这儿而不是第一个请求里，免得把那几秒算进限流节奏
                await em2.PrepareAsync(ct);
                // 网卡那段没配时是空串（走默认路由是常态，写进日志等于没说），整段跳过
                var nicem2 = em2.DescribeBinding();
                progress?.Report(em2.DescribeChannel()
                    + (string.IsNullOrWhiteSpace(nicem2) ? "" : "；" + nicem2));
            }

            _boardRepository.EnsureSchema();

            // 按页续传 + 凑齐才提交——循环本体和它藏过的两个 bug 见 BoardListFetchLoop
            if (_boardFetcher is not Remote.EastMoneyBoardFetcherBase pager)
            {
                progress?.Report("当前板块数据源不支持按页续传，跳过板块列表。");
                return "板块数据源不支持按页续传";
            }

            var (committed, unfinished) = await BoardListFetchLoop.RunAsync(
                fetchPage: (t, page, c) => pager.FetchBoardListPageAsync(t, page, c),
                repo: _boardRepository,
                report: m => progress?.Report(m),
                addError: errors.Add,
                ct: ct);

            if (committed == 0)
            {
                progress?.Report("板块列表这一轮没有哪一类凑齐，正表保持上次的完整快照，"
                               + "已抓到的页存在暂存区，下轮从断点接着抓。");
                return $"板块列表未凑齐（{string.Join("、", unfinished)}）";
            }
            progress?.Report($"板块列表更新完成：本轮提交 {committed} 个"
                           + (unfinished.Count > 0
                                ? $"；{string.Join("、", unfinished)}还没凑齐，下轮从断点接着抓。" : "。"));
        }
        finally
        {
            _boardFetcher.OnStatus -= Forward;
        }
        return null;
    }

    /// <summary>
    /// 把菜单拿到的**全量名单**提交进正表（2026-09-05）：按类走护栏 → 暂存区 → 一次事务搬过去。
    ///
    /// 为什么还要绕暂存区那一道（明明一次就拿全了）：<c>CommitStaged</c> 里的"清僵尸 + 写入"
    /// 是在同一个事务里做的，直接调 <c>UpsertBoards</c> 也一样，但走同一条路能保证两种数据源
    /// 提交出来的正表状态完全一致，也顺手清掉 push2 那条路可能留下的暂存内容。
    /// </summary>
    /// <returns>一类都没提交时返回原因（调用方当作"本轮没开工"）；提交了就返回 null。</returns>
    private string? CommitBoardListFromMenu(
        List<Board> all, ConcurrentBag<string> errors, IProgress<string>? progress)
    {
        int committedTotal = 0;
        var rejected = new List<string>();

        // 遍历**所有**类型而不是写死两个（2026-09-06 加地域时改）：写死的话，以后再加类型
        // 会漏在这儿，而且不报错——只是那一类永远不写库。
        foreach (var type in Enum.GetValues<BoardType>())
        {
            var label = type.Label();
            var items = all.Where(b => b.Type == type).ToList();
            var existing = _boardRepository.QueryBoards(type);

            // 这一批数据源**根本不提供**这一类（菜单 JSON 就没有地域板块）——那是"没有"，
            // 不是"掉光了"，跟护栏要防的情况是两回事。快照语义下空名单本来也不该提交，
            // 直接跳过；库里已有的原样留着，也别当成错误刷屏。
            if (items.Count == 0)
            {
                if (existing.Count > 0)
                    progress?.Report($"{label}板块：这个源不提供这一类，库里 {existing.Count} 个原样保留。");
                continue;
            }

            // 护栏：名单掉得太多就不写。正表是快照语义，少掉的会被当成已下架，
            // 连 BoardMember 和 BoardMemberFetchState 一起删——而成分股跨好几轮才攒得齐。
            if (Remote.EastMoneySideMenuBoardListProvider.CheckAgainstExisting(
                    type, items.Count, existing.Count) is { } why)
            {
                rejected.Add(label);
                errors.Add(why);
                progress?.Report("⚠ " + why);
                continue;
            }

            // ⚠ 菜单里**没有行情**，而 CommitStaged 是拿暂存区的值去覆盖正表的。不把库里现有的
            //   涨跌幅/成交额带上，就会把【板块指数合成】刚回填的值清成 0，直到下次合成跑完——
            //   热度页会有一段时间全是 0。领涨股同理（虽然眼下没人读它）。
            //   新板块在库里没有旧值，保持 0，等合成那一步补上。
            var carry = existing.ToDictionary(
                b => b.BoardCode, b => (b.ChangePct, b.Amount, b.LeaderCode, b.LeaderName));
            foreach (var b in items)
                if (carry.TryGetValue(b.BoardCode, out var q))
                    (b.ChangePct, b.Amount, b.LeaderCode, b.LeaderName) = q;

            _boardRepository.ClearStaged(type);
            _boardRepository.StageBoards(items);
            var (committed, pruned) = _boardRepository.CommitStaged(type);

            // push2 那条路可能留着页级断点。菜单一次拿全之后它就作废了——留着的话，
            // 万一下轮退回 push2，会从一个半截的暂存区接着抓。
            _boardRepository.ClearListState(type);

            committedTotal += committed;
            progress?.Report($"{label}板块已更新：{committed} 个"
                           + (pruned > 0 ? $"（清掉 {pruned} 条下架板块的残留）" : "") + "。");
        }

        if (committedTotal == 0)
            return $"板块名单没通过护栏（{string.Join("、", rejected)}），本轮不更新，库里保留上次的快照";

        progress?.Report(
            $"板块列表更新完成：共 {committedTotal} 个，取自东财行情中心菜单，**整轮没用到 push2**。"
            + (rejected.Count > 0
                ? $" ⚠ {string.Join("、", rejected)}没通过护栏，那一类保持上次的快照。" : ""));
        return null;
    }

    /// <summary>
    /// 逐个板块抓**官方成分名单**（2026-09-04 从原来的板块任务里拆出来）。约 2500 个请求，
    /// 是 push2 上最耗配额的一项。
    ///
    /// 成分股必须走 push2 的官方名单：实测 datacenter 的 F10 报表会系统性漏股
    /// （液冷服务器 170 只漏 4 只，含美的集团、拓普集团这种链上有实际业务的大票），
    /// 而且漏了不会报错，会一路带进板块营收中位数这类指标里。详见 EastMoneyBoardFetcher 类注释。
    ///
    /// 设计成**跑不完也没关系**：每个板块单独落库并记进度，下一轮自动跳过已成功的。
    /// 宁可跨几轮抓完，也不要用会漏股的数据凑数。
    /// </summary>
    /// <returns>没开工时返回原因；正常跑完返回 null。</returns>
    private async Task<string?> FetchBoardMembersCoreAsync(
        ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        // ⚠ 熔断只拦走网络的那三条通道（2026-09-06）：terminal 读的是东财终端落在本地的文件，
        // 一个请求都不发，被"东财接口限流中"挡住毫无道理——本地文件正是限流时唯一还能用的路。
        // 会撞上是因为 EastMoneyTerminalBoardFetcher 也继承 EastMoneyBoardFetcherBase：
        // 名单那一步退回 push2 抓失败时会给基类记上 PausedUntil，成分股这边跟着一起被拦。
        if (_boardFetcher is not Remote.EastMoneyTerminalBoardFetcher
            && Push2PausedReason() is { } paused)
        {
            progress?.Report($"{paused}，本轮不开工。已抓到的板块都在库里，恢复后接着抓没抓过的。");
            return paused;
        }

        void Forward(string s) => progress?.Report(s);
        _boardFetcher.OnStatus += Forward;
        try
        {
            if (_boardFetcher is Remote.EastMoneyBoardFetcherBase em3)
            {
                await em3.PrepareAsync(ct);
                // 网卡那段没配时是空串（走默认路由是常态，写进日志等于没说），整段跳过
                var nicem3 = em3.DescribeBinding();
                progress?.Report(em3.DescribeChannel()
                    + (string.IsNullOrWhiteSpace(nicem3) ? "" : "；" + nicem3));
            }

            _boardRepository.EnsureSchema();

            // 名单从库里读——列表那一项没跑也能干活，只是漏掉当天新增的板块（软依赖）
            var all = _boardRepository.QueryBoards();
            if (all.Count == 0)
            {
                progress?.Report("库里还没有板块名单，先跑一次【板块列表】再来抓成分股。");
                return "库里还没有板块名单（先跑【板块列表】）";
            }

            var since = BoardMemberFreshSince;
            var fresh = _boardRepository.GetBoardsWithFreshMembers(since);
            // ── 先小后大（2026-09-06 按用户要求）─────────────────────────
            // 为什么：大板块是这条路上最贵也最容易失败的一类——`pz` 被网页锁在 20，
            // 800 只就要翻 40 页、耗几分钟，中途撞上限流或验证的概率跟页数成正比，
            // 而一旦没取全就整个作废（total 对账不允许半截名单），几分钟白花。
            // 小板块一两页就完事、几乎不会失败。所以先把小的收干净，再去啃大的：
            // 同样的时间窗口里能落库的板块数最多。
            //
            // 排序键用上次抓到的只数（`MemberCount`）。**没抓过的是 0**——不能让它们排最前，
            // 那等于"完全不知道多大的先抓"，撞上 1444 只那种就前功尽弃；也不能排最后，
            // 因为库里中位数才 21 只，绝大多数没抓过的其实很小。按中等（200）对待，
            // 排在已知的小板块之后、已知的大板块之前。
            const int UnknownSizeRank = 200;
            static int SizeRank(Logic.Models.Board b) => b.MemberCount > 0 ? b.MemberCount : UnknownSizeRank;

            var todo = all.Where(b => !fresh.Contains(b.BoardCode))
                          .OrderBy(SizeRank)          // 升序＝小的先抓，>400 的自然排到后面
                          .ThenBy(b => b.BoardCode)   // 同样大小时定个稳定次序，便于对比两轮日志
                          .ToList();

            var big = todo.Count(b => SizeRank(b) > 400);
            progress?.Report($"成分股：{fresh.Count} 个板块已是最近抓的，本轮需抓 {todo.Count} 个"
                           + $"（先小后大；其中 {big} 个是 400 只以上的大板块，排在最后）。");
            if (todo.Count == 0) return null;

            int ok = 0, failed = 0, consecutiveFail = 0;
            try
            {
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var b = todo[i];
                try
                {
                    var members = await _boardFetcher.FetchMembersAsync(b.BoardCode, ct);
                    _boardRepository.ReplaceMembers(b.BoardCode, members);   // 立即落库，这是断点的粒度
                    ok++;
                    consecutiveFail = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    consecutiveFail++;
                    _boardRepository.MarkMembersFailed(b.BoardCode, "failed", ex.Message);
                    if (failed <= 5) errors.Add($"板块「{b.Name}」({b.BoardCode}) 成分股抓取失败：{ex.Message}");

                    // 失败原因当场进日志（2026-09-06 加）：原来它只写进库的 message 列，
                    // 日志里就一句"成功 2、失败 3"——人看着只知道坏了、不知道坏在哪，
                    // 得去查库才看得到"只取到 20 只"这种一眼定位问题的信息。
                    progress?.Report($"　板块「{b.Name}」({b.BoardCode}) 失败：{ex.Message}");

                    // 连续失败说明已经被限流了，再往下打只是白费请求、还会让封禁更久。
                    // 已经抓到的都落库了，剩下的下一轮继续——这正是逐板块落库的意义。
                    // 阈值 15 跟 RateLimiter 的熔断阈值对齐（2026-09-04）：实测正常波动里
                    // 连续失败能到 7 个，10 会误判成"被限流"、白白提前收尾。
                    if (consecutiveFail >= 15)
                    {
                        progress?.Report(
                            $"⚠ 连续 {consecutiveFail} 个板块失败，判定为已被限流，本轮提前收尾。" +
                            $"已成功 {ok} 个，剩余 {todo.Count - i - 1} 个下轮继续。");
                        errors.Add($"push2 限流，本轮成分股只抓到 {ok}/{todo.Count} 个板块，剩余下轮继续。");
                        break;
                    }
                }
                // 每 5 个报一次（2026-09-04 从 20 改小）：每个板块要 4~17 秒，20 个就是一两分钟不吭声——
                // 而人判断"还在跑吗"全靠日志有没有新行，静默一分钟就会以为卡死了（实际发生过好几次）。
                // 带上刚抓完的板块名，一眼能看出进度是真的在走。
                if ((i + 1) % 5 == 0 || i + 1 == todo.Count)
                    progress?.Report($"成分股：{i + 1}/{todo.Count}（成功 {ok}、失败 {failed}）"
                                   + $" 刚抓完「{b.Name}」");
            }
            }
            catch (OperationCanceledException)
            {
                // 用户点了停止：把现场交代清楚再退（2026-09-04）。每个板块是单独落库的，
                // 所以停在哪都不会留半批数据；人要知道的是"停之前做成了多少、下次从哪接"。
                progress?.Report(
                    $"板块成分股已停止：本轮成功 {ok} 个（都已落库，不会丢）、失败 {failed} 个，" +
                    $"剩 {todo.Count - ok - failed} 个没抓。下次跑会自动跳过已抓好的，从没抓的接着来。");
                throw;
            }

            // ⚠ 不能复用上面那个 since（2026-09-07）：它是抓取判据，不节流通道下等于 MaxValue，
            // 拿它统计会把刚抓成功的 1031 个全算成"待重试"。见 BoardMemberStatsSince。
            var (pOk, pFailed, pNever) = _boardRepository.GetMemberFetchProgress(BoardMemberStatsSinceNow);
            var allBoards = pOk + pFailed + pNever;
            progress?.Report(
                $"板块成分股完成：本轮成功 {ok}、失败 {failed}。" +
                $"全库成分股状态：最新 {pOk} 个、待重试 {pFailed} 个、从未抓过 {pNever} 个。" +
                (pFailed + pNever > 0 ? "（再跑一次会从没抓到的接着来）" : ""));

            // 存量进度带回给界面：这活跨好几轮才做得完，光说"完成"人不知道还剩多少
            _memberProgressText = allBoards > 0
                ? $"已抓 {pOk}/{allBoards}"
                  + (pFailed > 0 ? $"，待重试 {pFailed}" : "")
                  + (pNever > 0 ? $"，还剩 {pNever}" : "")
                : null;
        }
        finally
        {
            _boardFetcher.OnStatus -= Forward;
        }
        return null;
    }

    /// <summary>列表 + 成分股一起跑，给老的【拉取板块】按钮用，行为跟拆分前一样。</summary>
    private async Task<string?> FetchBoardsCoreAsync(
        ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        var skipped = await FetchBoardListCoreAsync(errors, progress, ct);
        if (skipped != null) return skipped;
        return await FetchBoardMembersCoreAsync(errors, progress, ct);
    }

    /// <summary>
    /// 抓取全市场 ETF 的日K（2026-07-15新增，2026-07-15晚些改为并入主流程）——ETF 不再有独立按钮，
    /// "拉取全部"/"拉取当天"末尾会顺带跑这一步（首次靠水位线一次性补齐历史，之后每天增量，跟大盘指数
    /// 一样的处理）。ETF 列表走新浪 <see cref="SinaEtfListProvider"/>（东财在用户环境不可用），代码是
    /// 带前缀的8位符号（"sh510300"），日K复用所选数据源的 BarFetcher。存进 Bar 表后因带前缀不是6位纯
    /// 数字，天然被 GetAllCodes 挡在选股全集外。跟个股共用同一套 errors/failedCodes/stats，ETF 代码也
    /// 计入 attempted，所以 ETF 失败同样进失败名单、可被"重新拉取失败股票"重试。返回本轮尝试的 ETF 代码
    /// （给调用方并入 attempted）。
    ///
    /// ════ 半截名单护栏 + 本地兜底（2026-09-16 加）════
    /// ETF 跟个股在这里有个**不对称**：个股的日常抓取清单走本地 <c>StockMeta.GetAll()</c>，
    /// 列表接口抽风最多丢新票；ETF **每轮都重新联网取名单、没有本地兜底**，名单少一段就
    /// 直接少抓一批K线，而且以前拿回 300 只还是 1600 只都照跑不误，一声不吭。
    ///
    /// 所以这里照搬 <c>TotalSharesTask</c> 的做法：本轮名单比库里存量少 5% 以上就**不用它**，
    /// 改用库里 <c>type='etf'</c> 的存量名单跑增量（名单取不到时同样兜底）。
    /// 代价只是"这一轮发现不了新上市的 ETF"，比静默少抓一批K线好得多。
    /// </summary>
    private async Task<List<string>> FetchEtfBarsAsync(
        NamedBarSource source, DateTime end, int lookbackYears, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        if (_etfListProvider == null) return new List<string>();
        progress?.Report("正在获取全市场ETF列表...");

        // 库里的存量名单——既是护栏的标尺，也是名单取不到时的兜底
        var local = SqliteStockMetaUpsert.GetByTypes(_paths.CurrentDb, SqliteStockMetaUpsert.TypeEtf)
            .Select(x => new StockListEntry(x.Code, x.Name)).ToList();

        List<StockListEntry> etfs;
        try { etfs = await _etfListProvider.GetAllStocksAsync(progress, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"获取ETF列表失败：{ex.Message}"); etfs = new List<StockListEntry>(); }

        // 护栏：空名单、或比库里存量少 5% 以上，都判定为"半截名单"，不拿它去跑
        bool fromLocal = false;
        if (etfs.Count < local.Count * EtfListMinKeepRatio)
        {
            if (local.Count == 0)
            {
                progress?.Report("ETF列表为空且库里也没有存量（接口可能不可达/被限流），本轮跳过ETF。");
                return new List<string>();
            }
            errors.Add($"ETF名单只拿到 {etfs.Count} 只、库里存量有 {local.Count} 只，判定为半截名单——"
                       + "本轮改用库里存量名单跑增量（这一轮发现不了新上市的ETF）");
            progress?.Report($"⚠ ETF名单疑似被截断（{etfs.Count} < {local.Count}），改用库里存量 {local.Count} 只。");
            etfs = local;
            fromLocal = true;
        }

        progress?.Report($"共 {etfs.Count} 只 ETF，开始抓取日K（已用时 {FormatElapsed(sw.Elapsed)}）...");
        // ETF 名称写进 StockMeta（type=etf）——让"查询"页能搜到 ETF（不影响个股选股）。
        // 走兜底时名字本来就是从这张表读出来的，没必要再写回去。
        if (!fromLocal)
            SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, etfs.Select(e => (e.Code, e.Name)), SqliteStockMetaUpsert.TypeEtf);

        int completed = 0;
        var tasks = etfs.Select(etf =>
        {
            // 跟个股/大盘指数一样的逐标的水位线：没抓过从回看窗口起点(首次补历史)，抓过从上次+1，"今天"看是否收盘确认。
            DateTime start;
            lock (_dbLock)
            {
                start = IncrementalStart(currentRepo.GetLatestBarInfo(etf.Code, Granularity.Day), end, lookbackYears);
            }
            return ProcessOneStockAsync(etf.Code, source, start, end, currentRepo, errors, failedCodes, stats, progress, etfs.Count, () => Interlocked.Increment(ref completed), sw, ct);
        });
        await Task.WhenAll(tasks);
        progress?.Report($"ETF 日K抓取完成（{etfs.Count} 只）。");
        return etfs.Select(e => e.Code).ToList();
    }

    /// <summary>
    /// 本地合成板块指数日K（2026-07-15新增）——**不联网**：用本地已有的成分股(BoardMember)+个股日K，按
    /// 等权累乘出每个板块的指数日K（见 <see cref="BoardIndexSynthesizer"/>），存进 Bar 表、code 用板块
    /// 代码(gn_xxx/new_xxx)。每次全量重算（先删该板块旧bar再写），因为成分股和个股数据会变。
    /// "拉取全部"/"拉取当天"末尾会自动跑（放在个股+ETF抓完之后，因为要读当天个股K线）；此外"合成板块
    /// 指数"按钮也调它——用于"拉取板块"更新了成分股之后、不重抓个股、单独按新成分重算一次。</summary>
    private void SynthesizeBoardIndexCore(SqliteBarRepository currentRepo, ConcurrentBag<string> errors,
        IProgress<string>? progress, CancellationToken ct)
    {
        var boards = _boardRepository.QueryBoards();
        if (boards.Count == 0)
        {
            progress?.Report("（本地还没有板块数据，跳过板块指数合成——先点一次\"拉取板块\"才有成分股可算）");
            return;
        }
        var asOf = DateTime.Now;
        var synthesizedMeta = new List<(string Code, string Name)>();
        // 板块的涨跌幅/成交额从这里回填（2026-09-03）：合成出来的板块指数最后一根就是当日板块行情，
        // 成交额本身就是成分股求和。以前这两个值是从数据源的板块榜直接抓的，现在改成本地算——
        // 既不依赖只能人工过验证的 push2，口径也跟板块K线一致。
        var quotes = new List<(string BoardCode, double ChangePct, double Amount)>();
        var tick = new ProgressThrottle(progress);
        int done = 0, withBars = 0, totalBars = 0;
        foreach (var board in boards)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var members = _boardRepository.QueryMembers(board.BoardCode);
                var bars = BoardIndexSynthesizer.Synthesize(board.BoardCode, members, currentRepo, asOf);
                lock (_dbLock)
                {
                    currentRepo.DeleteByCode(board.BoardCode, Granularity.Day);
                    if (bars.Count > 0) currentRepo.InsertOrRefreshUnconfirmed(bars);
                }
                if (bars.Count > 0)
                {
                    withBars++; totalBars += bars.Count; synthesizedMeta.Add((board.BoardCode, board.Name));
                    var last = bars[^1];
                    // 只有一根K时没有前收，涨跌幅按 0 处理（新板块或成分股数据太短）
                    var prevClose = bars.Count >= 2 ? bars[^2].Close : 0;
                    var pct = prevClose > 0 ? (last.Close / prevClose - 1) * 100 : 0;
                    quotes.Add((board.BoardCode, pct, last.Amount));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"板块「{board.Name}」({board.BoardCode}) 合成失败：{ex.Message}"); }
            // 按时间报而不是按个数（2026-09-08）：板块之间的成分股数量差一个量级，
            // 按 40 个一报实测能哑到 2 分 35 秒，那是全程最长的一段静默。
            done++;
            tick.Report(() => $"合成板块指数：{done}/{boards.Count}（已生成 {withBars} 个板块、{totalBars} 根日K）");
        }
        // 有指数K的板块名称写进 StockMeta（type=board）——让"查询"页能搜到板块、看行情（不影响个股选股）。
        if (quotes.Count > 0) _boardRepository.UpdateQuotes(quotes);
        if (synthesizedMeta.Count > 0)
            SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, synthesizedMeta, SqliteStockMetaUpsert.TypeBoard);
        // ⚠ 主动回收 WAL（2026-09-12 加）——这条路是全库写得最重的一条（950 个板块 × 约 4900 根
        // ≈ 468 万行）。SQLite 的 autocheckpoint 是**被动**的，只要有任何读连接活着就跳过；
        // 2026-09-10 就是因为一个残留进程握着库两个多小时，这 468 万行全堆在 WAL 里、涨到 162GB，
        // C 盘 931G 用到 0 可用。拿到 busy 会明确报出来——那是"有人握着库"的唯一早期信号。
        // （当时记的"单事务写 468 万行"是误判：这个循环一直是一个板块一个事务。）
        var walNote = new SqliteMaintenance(_paths.CurrentDb).CheckpointWalAndDescribe();
        if (walNote != null) progress?.Report(walNote);

        progress?.Report($"板块指数合成完成：{boards.Count} 个板块，其中 {withBars} 个成分股数据足够、已写入 {totalBars} 根日K（code=板块代码，不进个股选股）。");
    }

    /// <summary>
    /// "拉取指定年份区间"（2026-07-29新增，同日从单年扩展为区间）——往回补 **[起始年, 结束年]** 的历史数据，
    /// 把区间里"接口确实能给出历史值"的各类数据一次取齐；起止相同即只补那一年。用途：日常"拉取全部"只按
    /// "首次回看N年"建立历史（默认3年），想把更早的年份补上时填区间点一次即可（如 2016~2022），不必把回看
    /// 年数调大后重跑全量（那样已有股票的水位线不会回退、补不到更早的历史）。
    ///
    /// 本轮会抓（有历史可取）：
    /// - 个股/大盘指数/ETF 的**日K**，并顺带重算受影响标的的**周线/月线**（<see cref="ProcessOneStockAsync"/>
    ///   本来就会按该代码的全量日线重算周月线）；
    /// - **资金净流入**（新浪接口每次返回该股全历史、客户端按窗口裁剪；东财接口支持 beg/end，两者都能取往年）；
    /// - **融资余额**、**龙虎榜**（都是按交易日的官方/新浪日榜，逐日抓、本地已有的交易日跳过）；
    /// - **中标/订单公告**（巨潮全文检索支持历史区间；关键词沿用界面上填的那个，清空则不抓）。
    ///
    /// 本轮**故意跳过**（不是漏了，是取不到或没必要）：
    /// - **流通市值**：接口只给"当下"的市值快照（见 <see cref="SinaListMarketCapFetcher"/>），没有"某年某日的
    ///   市值"这种历史查询，硬抓只会把今天的值错误地当成那年的值；
    /// - **板块行情/成分股**、**指数成分/权重**：同样是当下快照（成分股会调整，历史成分接口不提供）；
    /// - **股东户数/十大股东**：每次抓取本来就返回该股**全部历史**并整体覆盖（见
    ///   <see cref="IShareholderRepository.ReplaceByCode"/>），跑一次"一键拉取定期数据"就已经包含往年，
    ///   按年份重复跑没有意义。
    ///
    /// 增量语义（跟"拉取全部"一样安全、可反复点、可随时停）：每只标的先看本地**最早**的日K日期——
    /// 已经早于区间起点就整只跳过（增量抓取保证本地历史是连续的，那段已经有了）；落在区间之内就只补
    /// "区间起点 → 最早日前一天"这段缺口；晚于区间终点就抓整个区间。逐日数据（融资/龙虎）跳过本地已有的交易日。
    /// 末尾按新补的日K重新合成一次板块指数（本地计算、不联网），让板块指数历史跟着一起变长。
    ///
    /// ⚠️ 复权基准：新抓的往年日K用的是**当前**的前复权基准，而库里很早抓入的较新K线是当时的基准，两者在
    /// 期间有分红除权的股票上可能不在同一基准上（这是本项目一直存在的取舍，见 <see cref="SqliteBarRepository.
    /// UpdateDayAmountTurnover"/> 的注释与 doc/data-platform-design.md），运行时会在日志里提示一次。
    /// </summary>
    /// <param name="overwriteQfq">true=对区间内每只标的**覆盖重抓前复权**（不看本地已有什么，整段按数据源
    /// 当前基准重写）。用来一次性抹平历史上分批入库造成的复权基准接缝，见 <see cref="RunRepairQfqAsync"/>。
    /// 代价是这一轮不再有"已有就跳过"的优化，耗时与首次回补相当。</param>
    public async Task<FetchResult> RunFetchYearAsync(
        NamedBarSource source, int startYear, int endYear, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct = default, bool overwriteQfq = false)
    {
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        _netInflowFetcher.OnStatus += ForwardStatus;
        _lhbProvider.OnStatus += ForwardStatus;
        _marginProvider.OnStatus += ForwardStatus;
        try
        {
            return await RunFetchYearInternalAsync(source, startYear, endYear, announcementKeywords, progress, ct, overwriteQfq);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
            _netInflowFetcher.OnStatus -= ForwardStatus;
            _lhbProvider.OnStatus -= ForwardStatus;
            _marginProvider.OnStatus -= ForwardStatus;
        }
    }

    private async Task<FetchResult> RunFetchYearInternalAsync(
        NamedBarSource source, int startYear, int endYear, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct, bool overwriteQfq = false)
    {
        var today = DateTime.Today;
        if (startYear < FirstAShareYear || startYear > today.Year)
            throw new InvalidOperationException($"起始年份 {startYear} 超出可抓范围（A股最早 {FirstAShareYear} 年，且不能晚于今年 {today.Year}）");
        if (endYear < startYear || endYear > today.Year)
            throw new InvalidOperationException($"结束年份 {endYear} 不对：不能早于起始年份 {startYear}，也不能晚于今年 {today.Year}");

        // 起止相同=只补那一年；结束年是今年时只补到今天为止——之后的日期还没发生，请求它们只会拿回空数据。
        var yearStart = new DateTime(startYear, 1, 1);
        if (yearStart < AShareMarketOpen)   // 见 AShareMarketOpen：填 1990 会让跳过优化整个失效
        {
            progress?.Report($"起点 {yearStart:yyyy-MM-dd} 上提到 A股开市首日 {AShareMarketOpen:yyyy-MM-dd}"
                             + "——比它更早没有任何市场数据，而且填得比开市日还早会让\"本地已补齐就跳过\"的判断失效、"
                             + "每只标的都白发一次请求。");
            yearStart = AShareMarketOpen;
        }
        var yearEnd = endYear == today.Year ? today : new DateTime(endYear, 12, 31);
        string rangeLabel = startYear == endYear ? $"{startYear}年" : $"{startYear}~{endYear}年";

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();

        var sw = Stopwatch.StartNew();
        progress?.Report($"目标区间：{yearStart:yyyy-MM-dd} ~ {yearEnd:yyyy-MM-dd}，数据源：{source.Name}");
        progress?.Report("本轮会抓：个股/指数/ETF日K(并重算周月线)、资金净流入、融资余额、龙虎榜、中标公告——都只补本地还缺的部分。");
        progress?.Report("本轮跳过：流通市值/板块行情与成分/指数成分与权重（接口只有\"当下快照\"、没有往年历史，硬抓会把今天的值当成那年的值）；" +
                         "股东户数/十大股东（每次抓取本来就返回全部历史，跑\"一键拉取定期数据\"即已包含往年）。");
        progress?.Report("提示：新抓的往年K线用当前的前复权基准，与很久以前入库的较新K线可能存在复权基准差异（本项目一直如此）。");

        // 标的全集：优先用数据源的全市场列表（这样"上市较早、但本地从没抓过"的股票也能补到），
        // 取不到就退回本地已有列表；两者取并集，避免只用一个来源而漏标的。
        var codeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var marketList = await source.StockListProvider.GetAllStocksAsync(progress, ct);
            foreach (var s in marketList) codeNames[s.Code] = s.Name;
            if (marketList.Count > 0)
                SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, marketList.Select(s => (s.Code, s.Name)));
            progress?.Report($"全市场股票列表：{marketList.Count} 只");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress?.Report($"获取全市场股票列表失败（改用本地已有列表继续）：{ex.Message}");
        }
        foreach (var (code, name) in SqliteStockMetaUpsert.GetAll(_paths.CurrentDb))
            codeNames.TryAdd(code, name);
        var stockCodes = codeNames.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (stockCodes.Count == 0)
            throw new InvalidOperationException("既取不到全市场股票列表、本地也没有股票列表，无法按年份补历史，请先执行一次\"拉取全部\"");
        progress?.Report($"待处理标的：{stockCodes.Count} 只个股（另含 {MarketIndexCatalog.All.Count} 个大盘指数与全市场ETF）");

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();

        // 一次查出所有代码的本地最早日K日期（5000+只逐个查会有5000+次往返，这里一次 GROUP BY 拿回）。
        var earliestByCode = currentRepo.GetEarliestPeriodStartByCode(Granularity.Day);
        var tradingDays = LocalTradingDays(currentRepo);

        // 上几轮已经探明"数据源在这天之前没有"的水位（见 BarProbeFloor 表）——这一轮直接把起点
        // 抬到它、抬过缺口就整只不发请求。overwriteQfq 那条路语义是"不看本地已有什么、整段重写",
        // 所以它连水位也不看（否则抹接缝的活会被跳过）。
        var floorRepo = new SqliteBarProbeFloorRepository(_paths.CurrentDb);
        floorRepo.EnsureSchema();
        var floorDay = overwriteQfq ? new Dictionary<string, DateTime>() : floorRepo.GetAll(Granularity.Day);
        var floorHfqMap = floorRepo.GetAll(Granularity.DayHfq);
        var floorRawMap = floorRepo.GetAll(Granularity.DayRaw);
        if (floorDay.Count + floorHfqMap.Count + floorRawMap.Count > 0)
            progress?.Report($"已探明的\"数据源没有更早数据\"水位：前复权 {floorDay.Count} 只、"
                           + $"后复权 {floorHfqMap.Count} 只、不复权 {floorRawMap.Count} 只——这些票的相应区间不再重复请求。"
                           + "（要作废这些结论重新探，跑【全库数据体检】并勾上「彻底体检」。）");

        // 本轮"请求成功、但返回 0 行"的记录，跑完各阶段后落成水位。三路分开收，粒度不能混。
        var emptyDay = new ConcurrentBag<(string Code, DateTime End)>();
        var emptyHfq = new ConcurrentBag<(string Code, DateTime End)>();
        var emptyRaw = new ConcurrentBag<(string Code, DateTime End)>();

        // ── 大盘指数日K ──
        progress?.Report($"开始补 {rangeLabel}大盘指数日K...");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, MarketIndexCatalog.All.Select(i => (i.Symbol, i.Name)), SqliteStockMetaUpsert.TypeIndex);
        int idxDone = 0;
        foreach (var (symbol, _) in MarketIndexCatalog.All)
        {
            var (s, e) = YearGapFor(symbol, earliestByCode, yearStart, yearEnd, tradingDays, floorDay);
            await ProcessOneStockAsync(symbol, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                MarketIndexCatalog.All.Count, () => Interlocked.Increment(ref idxDone), sw, ct,
                emptyRangeProbes: emptyDay);
        }

        // ── 个股日K（并发受数据源限速器节流，跟"拉取全部"同一套）──
        progress?.Report(overwriteQfq
            ? $"开始【覆盖重抓】{rangeLabel}个股前复权日K（不看本地已有什么，整段按数据源当前基准重写，用来抹平复权基准接缝）..."
            : $"开始补 {rangeLabel}个股日K（本地最早日已早于 {startYear} 年年初的标的会整只跳过、不发请求）...");
        int completed = 0;
        await Task.WhenAll(stockCodes.Select(code =>
        {
            var (s, e) = overwriteQfq ? (yearStart, yearEnd) : YearGapFor(code, earliestByCode, yearStart, yearEnd, tradingDays, floorDay);
            return ProcessOneStockAsync(code, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                stockCodes.Count, () => Interlocked.Increment(ref completed), sw, ct,
                Granularity.Day, overwrite: overwriteQfq, emptyRangeProbes: emptyDay);
        }));
        progress?.Report($"{rangeLabel}K线部分汇总：{stats.Summarize()}");
        RecordProbeFloors(floorRepo, emptyDay, earliestByCode, Granularity.Day, "前复权", progress);

        // ── 个股后复权日K（回测专用）：水位线独立，用 day_hfq 自己的最早日算缺口 ──
        var earliestHfq = currentRepo.GetEarliestPeriodStartByCode(Granularity.DayHfq);
        await FetchHfqBarsAsync(source, stockCodes,
            code => YearGapFor(code, earliestHfq, yearStart, yearEnd, tradingDays, floorHfqMap),
            currentRepo, errors, failedCodes, stats, progress, sw, ct, $"{rangeLabel}个股",
            emptyRangeProbes: emptyHfq);
        RecordProbeFloors(floorRepo, emptyHfq, earliestHfq, Granularity.DayHfq, "后复权", progress);

        // ── 个股不复权日K：跟后复权并列，各按各的水位线 ──
        // 往前补历史年份时这条线也得跟上，否则 day/day_hfq 有 2012 年而 day_raw 没有，
        // 回测序列（day_adj）就只能算到 day_raw 的起点为止。
        var earliestRaw = currentRepo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        await FetchHfqBarsAsync(source, stockCodes,
            code => YearGapFor(code, earliestRaw, yearStart, yearEnd, tradingDays, floorRawMap),
            currentRepo, errors, failedCodes, stats, progress, sw, ct, $"{rangeLabel}个股", Granularity.DayRaw,
            emptyRangeProbes: emptyRaw);
        RecordProbeFloors(floorRepo, emptyRaw, earliestRaw, Granularity.DayRaw, "不复权", progress);

        // ── ETF 日K ──
        var etfCodes = await FetchEtfBarsForYearAsync(source, yearStart, yearEnd, earliestByCode, currentRepo,
            errors, failedCodes, stats, progress, sw, ct, tradingDays,
            new ProbeFloors(floorRepo, floorDay, floorHfqMap, floorRawMap));

        // ── 退市股：名单 + 区间内的历史日K（前复权+后复权，消除回测幸存者偏差，见 FetchDelistedForRangeAsync）──
        var delistedCodes = await FetchDelistedForRangeAsync(source, yearStart, yearEnd, rangeLabel,
            currentRepo, earliestByCode, earliestHfq, errors, failedCodes, stats, progress, sw, ct,
            new ProbeFloors(floorRepo, floorDay, floorHfqMap, floorRawMap));

        // ── 资金净流入（按年份区间，只补本地缺的那一段）──
        await FetchNetInflowRangeAsync(stockCodes, yearStart, yearEnd, progress, ct);

        // ── 中标/订单公告（关键词沿用界面设置；清空则跳过）──
        if (announcementKeywords.Count == 0)
            progress?.Report("（公告关键词为空，跳过中标/订单公告）");
        else
            await FetchAnnouncementsAsync(announcementKeywords, DateOnly.FromDateTime(yearStart), DateOnly.FromDateTime(yearEnd), progress, ct);

        // ── 融资余额 / 龙虎榜：逐交易日，跳过本地已有的日子（复用"一键补齐每日历史"的逐日补齐器）──
        _marginRepository.EnsureSchema();
        _lhbRepository.EnsureSchema();
        var marginHave = _marginRepository.GetTradeDates();
        // 落库走 MarginDayWriter（2026-09-18）——跟【融资余额】任务、补残缺日共用同一个动作。
        var marginWriter = new MarginDayWriter(_marginProvider, _marginRepository, _dbLock);
        await BackfillDailyAsync($"{rangeLabel}融资余额", IDailyFetchNoDataRepository.MarginDataset, DateOnly.FromDateTime(yearStart), DateOnly.FromDateTime(yearEnd), marginHave, d => marginWriter.RefetchAsync(d, ct),
            errors, progress, sw, _marginProvider.EarliestAvailable, ct);

        var lhbHave = _lhbRepository.GetTradeDates();
        // 落库走 LhbDayWriter（2026-09-17）——跟【龙虎榜】任务、补残缺日共用同一套口径：
        // 派生对应值 + 整天替换。这条路径原来走 InsertOrIgnore 且不派生，补进来的历史行
        // deviation 永远是空的。
        var lhbWriter = new LhbDayWriter(_paths.CurrentDb, _lhbRepository, _lhbProvider, _dbLock);
        await BackfillDailyAsync($"{rangeLabel}龙虎榜", IDailyFetchNoDataRepository.LhbDataset, DateOnly.FromDateTime(yearStart), DateOnly.FromDateTime(yearEnd), lhbHave,
            d => lhbWriter.RefetchAsync(d, ct),
            errors, progress, sw, _lhbProvider.EarliestAvailable, ct);

        // ── 板块指数按新补齐的个股日K重新合成（本地计算、不联网）——让板块指数历史跟着一起变长 ──
        SynthesizeBoardIndexCore(currentRepo, errors, progress, ct);

        // ── 把"接缝可能对不上"的票交给【重取前复权】（2026-09-02 补上的通路）──
        // 这一轮补的是**更早**的空缺段，用的是数据源**当前**的前复权基准；而库里较新的那段是当时
        // 抓的、基准可能早就变了（每次分红送转都会变）。两段拼在一起，接缝处就是假跳空——
        // 正是【重取前复权】要修的那种。日常那趟的漂移检测在这里帮不上忙：它靠"请求窗口与库里
        // 已有区间重叠"来比对，而这里补的段本来就不重叠，比不出东西。所以直接按除权记录判：
        // 补的区间之后有过除权的票，接缝一定对不上。
        RecordRangeFillForQfqRepair(stockCodes, DateOnly.FromDateTime(yearStart), progress);

        progress?.Report($"{rangeLabel}补齐完毕，总用时 {FormatElapsed(sw.Elapsed)}。");
        var attempted = stockCodes
            .Concat(MarketIndexCatalog.All.Select(i => i.Symbol))
            .Concat(etfCodes)
            .Concat(delistedCodes).ToList();
        return FinishFetchRun(errors, $"拉取{rangeLabel}", attempted, failedCodes);
    }

    /// <summary>
    /// 把"复权基准漂移"的股票记进待重取名单（2026-07-30 检测，2026-08-31 改成只记名单）。
    ///
    /// <see cref="ProcessOneStockAsync"/> 在日常抓取中顺带发现某只股票的历史值与数据源当前基准
    /// 对不上（说明它分红或送转了），手上那一页（≈2.5 年）已经就地覆盖；更早的历史要整段重取，
    /// 那件事交给计划里的【重取前复权】任务，见 <see cref="RunRepairQfqAsync"/>。
    ///
    /// 为什么需要：数据源的前复权是"原价 − 之后累计分红送配"，基准随抓取时点变化；而我们的历史是分批
    /// 入库、且 INSERT OR IGNORE 不覆盖，于是同一只股票不同时间段落在不同基准上，接缝处出现假跳空
    /// （实测有股票在接缝处虚增 50%）。后复权（day_hfq）不受影响。
    ///
    /// 名单累加去重、取成一只划掉一只；漏了也不要紧，下一轮日常比对还会把它重新检出来。
    /// </summary>
    /// <summary>
    /// 区间回补之后，把**接缝一定对不上**的票记进待重取名单（2026-09-02 新增）。
    ///
    /// 判据：这只票在 <paramref name="filledFrom"/> 之后有过除权（分红/送转/配股）。
    /// 有除权就说明数据源的前复权基准在那之后变过，于是"这一轮按当前基准补的更早那段"
    /// 跟"库里当时抓的较新那段"必然不在同一基准上，接缝处会出现假跳空
    /// （实测有股票在接缝处虚增 50%）。没除权过的票基准没变，不用重取。
    ///
    /// 为什么不能靠日常那套漂移比对：那套要求"请求窗口与库里已有区间重叠"才比得出来，
    /// 而区间回补补的是**空缺段**、本来就不重叠。所以这里换成按除权记录直接判。
    ///
    /// 名单交给【重取前复权】慢慢消费（累加去重、取一只划一只），本方法只写名单、不抓数据。
    /// </summary>
    private void RecordRangeFillForQfqRepair(
        IReadOnlyList<string> filledCodes, DateOnly filledFrom, IProgress<string>? progress)
    {
        if (filledCodes.Count == 0) return;
        try
        {
            var withExDiv = new HashSet<string>(StringComparer.Ordinal);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.CurrentDb}"))
            {
                conn.Open();
                using var probe = conn.CreateCommand();
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Dividend';";
                if (Convert.ToInt32(probe.ExecuteScalar() ?? 0) == 0) return;
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RightsIssue';";
                bool hasRights = Convert.ToInt32(probe.ExecuteScalar() ?? 0) > 0;

                using var cmd = conn.CreateCommand();
                // 除权日在补的区间起点之后 = 那之后基准变过。
                // 配股是A股第四类除权事件（前三类是现金分红/送股/转增），同样会改基准，所以一起查。
                cmd.CommandText = hasRights
                    ? "SELECT DISTINCT code FROM Dividend WHERE ex_date IS NOT NULL AND ex_date >= $from "
                      + "UNION SELECT DISTINCT code FROM RightsIssue WHERE ex_date IS NOT NULL AND ex_date >= $from;"
                    : "SELECT DISTINCT code FROM Dividend WHERE ex_date IS NOT NULL AND ex_date >= $from;";
                var p = cmd.CreateParameter(); p.ParameterName = "$from";
                p.Value = filledFrom.ToString("yyyy-MM-dd"); cmd.Parameters.Add(p);
                using var r = cmd.ExecuteReader();
                while (r.Read()) withExDiv.Add(r.GetString(0));
            }

            var targets = filledCodes.Where(withExDiv.Contains).Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count == 0)
            {
                progress?.Report("（补的这一段之后没有除权记录，前复权基准没变，不用重取）");
                return;
            }

            lock (_dbLock)
            {
                var manifest = _manifestStore.Load();
                var merged = manifest.PendingQfqRepairCodes.Union(targets, StringComparer.Ordinal)
                    .OrderBy(c => c, StringComparer.Ordinal).ToList();
                int added = merged.Count - manifest.PendingQfqRepairCodes.Count;
                manifest.PendingQfqRepairCodes = merged;
                _manifestStore.Save(manifest);
                progress?.Report($"这一段补完之后，有 {targets.Count} 只票在期间除过权、前复权接缝对不上，"
                               + $"已记入待重取名单（新增 {added} 只，共 {merged.Count} 只）——"
                               + "【重取前复权】会把它们整段按当前基准重取。");
            }
        }
        catch (Exception ex)
        {
            // 记名单失败不该让整轮回补算失败：下一轮日常抓取的漂移比对多半也能把它们检出来
            progress?.Report($"（记待重取前复权名单时出错，不影响本轮补齐：{ex.Message}）");
        }
    }

    private void RecordDriftedForRepair(
        IReadOnlyList<string> driftedCodes, SqliteBarRepository currentRepo, IProgress<string>? progress)
    {
        if (driftedCodes.Count == 0) return;

        var earliestByCode = currentRepo.GetEarliestPeriodStartByCode(Granularity.Day);
        // 只有历史比"手上那一页"更长的才需要重取——短历史的股票刚才已经整段覆盖好了
        var pageCovered = DateTime.Today.AddDays(-DriftCheckLookbackDays).Date;
        var targets = driftedCodes.Distinct(StringComparer.Ordinal)
            .Where(c => earliestByCode.TryGetValue(c, out var e) && e.Date < pageCovered)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
        {
            progress?.Report($"复权基准：{driftedCodes.Distinct().Count()} 只在抓取时已就地覆盖完毕，无需重取更早历史。");
            return;
        }

        int pending;
        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            var set = new HashSet<string>(manifest.PendingQfqRepairCodes, StringComparer.Ordinal);
            foreach (var c in targets) set.Add(c);
            manifest.PendingQfqRepairCodes = set.OrderBy(c => c, StringComparer.Ordinal).ToList();
            pending = manifest.PendingQfqRepairCodes.Count;
            _manifestStore.Save(manifest);
        }
        progress?.Report($"复权基准：{targets.Count} 只股票除权了，更早的历史要按新基准重取，已记入待重取名单"
                       + $"（共 {pending} 只）——计划里的【重取前复权】会在空闲时慢慢补，也可以手动点它的【执行】。");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  回测专用价格序列（2026-08-31 新增）
    //
    //  两步：① 抓一份**不复权**日线（day_raw）——全库唯一不随分红变化的价格，抓一次永远有效；
    //        ② 用它 + 除权事件算出**乘法式**复权序列（day_adj），收益率精确等于真实收益率。
    //
    //  为什么不直接用数据源的后复权：那份是"送转乘、分红加"的混合式，加法项会阻尼波动，
    //  实测收益率相对真实值 工商银行 ×0.625、中国石化 ×0.542 —— 拿它回测，高股息股会显得
    //  波动小、回撤浅，因子排序被扭曲。详见 Granularity.DayAdj 和 AdjustFactorCalculator。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本地还有多少条预约披露记录处于"还没实际披露"（给界面显示待办量）。
    /// 这个数≈下一轮要复查的量：已经披露完的那些不用再动了。
    /// </summary>
    public int GetPendingEarningsCount()
    {
        try { return new SqliteEarningsScheduleRepository(_paths.CurrentDb).PendingCount(); }
        catch { return 0; }
    }

    /// <summary>
    /// 抓定期报告的**预约披露日**（2026-09-01 新增，数据源见 <see cref="Remote.CninfoPrebookProvider"/>）。
    ///
    /// 每次都把可用报告期**整期全量覆盖**一遍，不做任何增量——因为一期全市场 5500 条
    /// 一个请求 0.3 秒就拿回来了，省不出什么。
    ///
    /// 为什么要天天跑：预约日**会改**，实测沪市 2000 条样本 12% 改过，而且**提前的比延后的还多**
    /// （55% vs 44%，最多提前 44 天）。提前那半边尤其要紧——按原日期盯的话，财报已经出了还不知道。
    ///
    /// 报告期不能自己编，得先问接口（<c>GetAvailablePeriodsAsync</c>）——它当前只认最近两期。
    /// 所以"下一次财报日"存在一段空窗：上一期都披露完、下一期预约表还没发布时，那一列就是空的。
    /// </summary>
    public async Task<FetchResult> RunFetchEarningsScheduleAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = new FetchResult();
        if (_prebookProvider == null)
        {
            progress?.Report("没有配置预约披露数据源，跳过。");
            result.NothingToDo = true;
            return result;
        }

        var repo = new SqliteEarningsScheduleRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        var sw = Stopwatch.StartNew();

        List<(DateTime Period, string Label)> periods;
        try
        {
            periods = await _prebookProvider.GetAvailablePeriodsAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Errors.Add($"取可选报告期失败：{ex.Message}");
            progress?.Report($"⚠ 取可选报告期失败：{ex.Message}。本轮跳过。");
            return result;
        }

        if (periods.Count == 0)
        {
            progress?.Report("数据源没给出可用的报告期，本轮跳过。");
            result.NothingToDo = true;
            return result;
        }

        // 抓之前先记下旧的有效日期，抓完对一遍——改期是这个任务最该报出来的事
        var before = repo.GetUpcomingByCode().ToDictionary(kv => kv.Key, kv => kv.Value.EffectiveDate);
        int total = 0;
        var moved = new List<string>();

        foreach (var (period, label) in periods)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rows = await _prebookProvider.FetchAsync(period, null, ct);
                if (rows.Count == 0)
                {
                    progress?.Report($"【{label}】数据源返回 0 条，跳过。");
                    continue;
                }
                repo.Upsert(rows);
                total += rows.Count;

                int pending = rows.Count(r => r.Pending);
                int changed = rows.Count(r => r.ChangeCount > 0);
                progress?.Report($"【{label}】{rows.Count} 只：还没披露 {pending} 只、改过披露日 {changed} 只。");

                foreach (var r in rows)
                {
                    if (!r.Pending || r.EffectiveDate is not { } now) continue;
                    if (before.TryGetValue(r.Code, out var was) && was is { } old && old != now && moved.Count < 20)
                        moved.Add($"{r.Code} {old:MM-dd}→{now:MM-dd}（{(now - old).Days:+0;-0} 天）");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"【{label}】抓取失败：{ex.Message}");
                progress?.Report($"⚠【{label}】抓取失败：{ex.Message}");
            }
        }

        if (moved.Count > 0)
            progress?.Report($"⚠ 有 {moved.Count} 只改了披露日期：{string.Join("、", moved)}"
                           + "——盯着这几只的话记得对一下日子。");

        int left = repo.PendingCount();
        progress?.Report($"财报预约日完成：{total} 条已更新，还有 {left} 只没到披露日，"
                       + $"用时 {FormatElapsed(sw.Elapsed)}。");
        return result;
    }

    /// <summary>
    /// 抓业绩预告 + 业绩快报（2026-09-03，东财 datacenter）。本地此前完全没有这两份数据。
    ///
    /// 为什么要它：<b>比正式财报早一个月以上</b>——Q3 预告 10 月中出、财报 10 月底才出；年报预告
    /// 1 月底、年报要等到 4 月。而且强制披露规则正好对准剧变（净利变动超 50%、扭亏、首亏都必须
    /// 预告），"业绩发生剧变的公司"全在这张表里。另外 change_reason 是公司自述的变动原因，
    /// 能从中提"涨价/供不应求/产能满负荷"这类词——法定披露文件里公司自己写的，比研报转述硬。
    ///
    /// 增量水位线取本地已有的最新公告日，并且<b>从那一天本身重抓</b>而不是次日：同一天里公司是
    /// 陆续发预告的，上次抓的时候当天可能还没发完，从次日开始会漏掉当天后半段。主键 UPSERT 保证
    /// 重抓不产生重复行。首次没有任何数据时从 2016-01-01 起全量补，约 20 万行、400 页。
    /// </summary>
    public async Task<FetchResult> RunFetchEarningsForecastAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = new FetchResult();
        if (_forecastProvider == null || _forecastRepository == null)
        {
            progress?.Report("没有配置业绩预告数据源（东财），跳过。");
            result.NothingToDo = true;
            return result;
        }

        var sw = Stopwatch.StartNew();
        _forecastRepository.EnsureSchema();

        void Forward(string s) => progress?.Report(s);
        _forecastProvider.OnStatus += Forward;
        try
        {
            var today = DateTime.Today;

            // ── 业绩预告 ──
            var fStart = _forecastRepository.GetLatestForecastNoticeDate() ?? new DateTime(2016, 1, 1);
            progress?.Report($"业绩预告：从 {fStart:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}" +
                             (_forecastRepository.CountForecasts() == 0 ? "（首次全量，约 400 页）" : "（增量）"));
            try
            {
                int n = await _forecastProvider.FetchForecastsAsync(
                    fStart, today, b => _forecastRepository.UpsertForecasts(b), progress, ct);
                progress?.Report($"业绩预告写入 {n} 条，本地共 {_forecastRepository.CountForecasts()} 条。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"业绩预告抓取失败：{ex.Message}");
                progress?.Report($"⚠ 业绩预告抓取失败：{ex.Message}");
            }

            // ── 业绩快报 ──
            // 单独 try：快报是非强制披露、覆盖面本来就小，它失败不该把已经抓好的预告一起算失败。
            var eStart = _forecastRepository.GetLatestExpressUpdateDate() ?? new DateTime(2016, 1, 1);
            progress?.Report($"业绩快报：从 {eStart:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}");
            try
            {
                int n = await _forecastProvider.FetchExpressAsync(
                    eStart, today, b => _forecastRepository.UpsertExpress(b), progress, ct);
                progress?.Report($"业绩快报写入 {n} 条，本地共 {_forecastRepository.CountExpress()} 条。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"业绩快报抓取失败：{ex.Message}");
                progress?.Report($"⚠ 业绩快报抓取失败：{ex.Message}");
            }
        }
        finally
        {
            _forecastProvider.OnStatus -= Forward;
        }

        progress?.Report($"业绩预告/快报完成，用时 {FormatElapsed(sw.Elapsed)}。");
        return result;
    }

    /// <summary>
    /// 抓机构调研 / 限售解禁 / 股东增减持（2026-09-03，东财 datacenter）。
    /// 本地此前这几份数据全都没有，也都没有回退源。
    ///
    /// ⚠ **原本还有第四张：大宗交易**。2026-09-17 拆去 <c>BlockTradeTask</c>——它的主键
    /// 用了东财那个不稳定的 <c>DAILY_RANK</c>，重抓一次就多一份副本，修法是改按日整日替换，
    /// 于是切片从"按年十几片"变成"按日两千多片"，跟这三张的节奏彻底对不上。
    /// 详见 doc/block-trade-task-design.md。
    ///
    /// 剩下三张仍放一个任务里跑：它们同构、共用同一个 datacenter 配额，分成三项只会让人在
    /// 计划页上多排两行。三项各自 try：一项失败不影响其余（覆盖面和重要性都不一样，
    /// 机构调研挂了不该让已经抓好的增减持也算失败）。
    ///
    /// 增量水位线各表自己的日期列；<b>限售解禁例外，每次全量重取</b>——它含未来的解禁计划
    /// （实测有 2035 年的），按"抓到今天为止"做增量会永远漏掉未来那部分，而未来正是它的价值。
    /// </summary>
    /// <param name="fullBackfill">
    /// 「首次整段回补」——不看水位线，三张表都从 <c>floor</c> 重抓一遍（2026-09-19 接上，
    /// 此前 <c>forceStart</c> 这个参数声明了却没有任何调用方，界面上根本点不到）。
    ///
    /// 用途是**改了排序键之后把历史补回来**：排序键排不到主键末列时，深分页会跨页重复 + 遗漏，
    /// 重复那半会被主键去重自检喊出来，**遗漏那半一声不吭**——只能整段重取。
    /// 机构调研 2026-09-19 补了 tieBreaker，这一轮就是给它用的。
    ///
    /// 回补是 UPSERT 不是删重写，所以**中途停掉不会两头空**，下次接着跑即可。
    /// </param>
    public async Task<FetchResult> RunFetchMarketEventsAsync(
        IProgress<string>? progress, CancellationToken ct = default,
        bool fullBackfill = false)
    {
        var result = new FetchResult();
        if (_marketEventProvider == null || _marketEventRepository == null)
        {
            progress?.Report("没有配置市场事件数据源（东财），跳过。");
            result.NothingToDo = true;
            return result;
        }

        var sw = Stopwatch.StartNew();
        _marketEventRepository.EnsureSchema();

        void Forward(string s) => progress?.Report(s);
        // 主键重复告警必须收——它是"数据静默丢失"的唯一早期信号（龙虎榜席位曾丢 7.5% 且不报错）
        void OnWarn(string s) { progress?.Report(s); result.Errors.Add(s); }
        _marketEventProvider.OnStatus += Forward;
        _marketEventRepository.OnWarning += OnWarn;
        try
        {
            var today = DateTime.Today;
            var floor = new DateTime(2016, 1, 1);

            // 从水位线那一天**本身**重抓（不是次日）：公告是全天陆续发的，上次抓时当天可能没发完。
            // 主键 UPSERT 保证重抓不产生重复行。
            //
            // 再额外往前推 LaggingFieldLookbackDays 天，兜"公告补发/修订"——这几张都是按年
            // 切片的公告类数据，多抓一小段成本极低。（这个回看原本是为大宗交易的滞后字段设的，
            // 大宗已拆去 BlockTradeTask，那边按日整日替换、自带 30 天回看。）
            async Task RunOne(string label, string table, string dateCol, Func<DateTime, Task<int>> fetch)
            {
                try
                {
                    int before = _marketEventRepository.Count(table);
                    bool first = before == 0;
                    DateTime start;
                    string mode;
                    if (fullBackfill)
                    {
                        // 整段回补：不看水位线，从 floor 重来一遍。**不要靠删表来触发**——
                        // 那会先丢数据再重下，中途失败就两头空；这里是 UPSERT，停了再跑就行。
                        start = floor;
                        mode = "（整段回补：不看水位线，从头重取一遍）";
                    }
                    else if (first)
                    {
                        start = floor;
                        mode = "（首次全量）";
                    }
                    else
                    {
                        var mark = _marketEventRepository.GetLatestDate(table, dateCol) ?? floor;
                        start = mark.AddDays(-LaggingFieldLookbackDays);
                        if (start < floor) start = floor;
                        mode = $"（增量，含回看 {LaggingFieldLookbackDays} 天补滞后字段）";
                    }
                    progress?.Report($"{label}：从 {start:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}" + mode);
                    int n = await fetch(start);
                    int after = _marketEventRepository.Count(table);
                    // 整段回补时报"净增了多少行"：这一轮补回来的正是**此前跨页遗漏、而且从来没有
                    // 任何告警**的那部分（重复那半有主键自检喊，遗漏那半没有）。净增 0 就说明
                    // 之前没漏——这个数本身就是结论，值得留在日志里。
                    progress?.Report($"{label} 写入 {n} 行，本地共 {after} 行。"
                        + (fullBackfill
                            ? $"　整段回补对账：回补前 {before} 行 → 现在 {after} 行，"
                              + (after > before
                                  ? $"**补回此前遗漏的 {after - before} 行**（{(after - before) * 100.0 / Math.Max(after, 1):F3}%）"
                                  : "没有净增，说明此前没有跨页遗漏")
                            : ""));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Errors.Add($"{label} 抓取失败：{ex.Message}");
                    progress?.Report($"⚠ {label} 抓取失败：{ex.Message}");
                }
            }

            await RunOne("机构调研", "OrgSurvey", "notice_date", start =>
                _marketEventProvider.FetchOrgSurveysAsync(start, today,
                    b => _marketEventRepository.UpsertOrgSurveys(b), progress, ct));

            await RunOne("股东增减持", "HolderChange", "notice_date", start =>
                _marketEventProvider.FetchHolderChangesAsync(start, today,
                    b => _marketEventRepository.UpsertHolderChanges(b), progress, ct));

            // 限售解禁不传日期——全量重取，理由见方法注释
            try
            {
                int n = await _marketEventProvider.FetchShareLiftsAsync(
                    b => _marketEventRepository.UpsertShareLifts(b), progress, ct);
                progress?.Report($"限售解禁写入 {n} 行，本地共 {_marketEventRepository.Count("ShareLift")} 行。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"限售解禁抓取失败：{ex.Message}");
                progress?.Report($"⚠ 限售解禁抓取失败：{ex.Message}");
            }

            progress?.Report($"市场事件四项完成，用时 {FormatElapsed(sw.Elapsed)}。");
        }
        finally
        {
            _marketEventProvider.OnStatus -= Forward;
            _marketEventRepository.OnWarning -= OnWarn;
        }
        return result;
    }

    /// <summary>
    /// 抓个股的**行业归属**和**题材归属**（2026-09-03，东财 datacenter）。
    ///
    /// 解决的是一个实测出来的老问题：现有证监会分类 33 门类 + 84 大类，但 <b>1867 只（32.5%）
    /// 大类为空只能退回门类</b>，"制造业"一个门类就装了 3596 只（占 62%）。拿这个做行业中性化
    /// 等于没中性化——FactorLab 的中性 IC 一直不准，根子在这。东财是三级分类
    /// （一级31/二级128/三级337），最大的三级行业也才 627 只。
    ///
    /// 顺带把**题材归属 + 入选理由**也落库：理由原文（取自互动易回复、公告）加上精确匹配标记，
    /// 是判断"实质业务 vs 蹭概念"唯一能自动化的判据。
    ///
    /// <b>不走 push2</b>——那个要人工在浏览器过反爬验证、且验证会过期，不适合无人值守。
    /// 这里用 datacenter 的 F10 报表，稳定可达。
    ///
    /// 这张表没有时间维度、是当下快照，所以每次全量重取（188 页）。但**先抓到数据才清表**：
    /// 一进来就清的话，接口一挂就把行业分类清空了，所有依赖行业的分析当场失效。
    /// </summary>
    public async Task<FetchResult> RunFetchStockBoardMapAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = new FetchResult();
        if (_boardMapProvider == null || _boardMapRepository == null)
        {
            progress?.Report("没有配置个股行业/题材数据源（东财），跳过。");
            result.NothingToDo = true;
            return result;
        }

        var sw = Stopwatch.StartNew();
        _boardMapRepository.EnsureSchema();

        void Forward(string s) => progress?.Report(s);
        _boardMapProvider.OnStatus += Forward;
        try
        {
            bool cleared = false;
            var (ind, theme) = await _boardMapProvider.FetchAsync(
                (inds, themes) =>
                {
                    // 拿到第一批才清表——这样接口挂了的话库里旧数据还在
                    if (!cleared) { _boardMapRepository.ClearAll(); cleared = true; }
                    return (_boardMapRepository.UpsertIndustries(inds),
                            _boardMapRepository.UpsertThemes(themes));
                },
                progress, ct);

            int covered = _boardMapRepository.CountIndustryStocks();
            progress?.Report(
                $"个股行业/题材完成：行业 {ind} 条（覆盖 {covered} 只）、题材 {theme} 条，" +
                $"用时 {FormatElapsed(sw.Elapsed)}。" +
                "东财覆盖不到的股票仍走证监会分类兜底（StockIndustry 表保留）。");
        }
        catch (OperationCanceledException)
        {
            progress?.Report("个股行业/题材抓取中断，已落库的部分有效；这张表是快照，下次重新全量取。");
            throw;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"个股行业/题材抓取失败：{ex.Message}");
            progress?.Report($"⚠ 个股行业/题材抓取失败：{ex.Message}");
        }
        finally
        {
            _boardMapProvider.OnStatus -= Forward;
        }
        return result;
    }

    /// <summary>
    /// 哪些标的需要不复权日线：**只有个股和退市股**。
    /// 指数不除权（三种复权返回同一序列）、ETF 暂不进回测、板块指数是本地合成的——
    /// 给它们抓不复权纯属浪费（实测全库 7658 个标的里有 2000 个是这类）。
    /// </summary>
    private static bool NeedsRawBars(string? type) =>
        type is null or SqliteStockMetaUpsert.TypeStock or SqliteStockMetaUpsert.TypeDelisted;

    private HashSet<string> RawBarTargetCodes()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.CurrentDb}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT code, type FROM StockMeta;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (NeedsRawBars(r.IsDBNull(1) ? null : r.GetString(1)))
                    set.Add(r.GetString(0));
        }
        catch { /* 读不到就当没有，调用方自然什么都不做 */ }
        return set;
    }

    /// <summary>
    /// 判断一只标的的不复权日线补齐了没有。**两头都要比**：
    /// 尾巴要跟前复权一样新，**开头也要跟前复权一样早**。
    ///
    /// ⚠ 只比尾巴会出大事（2026-09-01 实测踩到）：不复权并进「拉取全部」之后，它走的是水位线窗口——
    /// 库里没有就按回看年数（3 年）抓。于是它抢在【补不复权历史】前头把最近 3 年填上，
    /// 判据一看"最新日期追上了"就归零、界面显示「已补齐」，前面 7 年**再也没人去补**。
    /// 当时 5781 只里有 5232 只就这么卡在 3 年上，而 day_hfq 是完整的 10 年。
    ///
    /// 30 天容差是给数据源留的余地：个别标的的不复权历史本来就比前复权短几天，
    /// 不留容差会让它们每一轮都被当成"没补齐"反复重抓。
    /// </summary>
    private static bool RawBarsComplete(DateTime dayEarliest, DateTime dayLatest,
                                        DateTime? rawEarliest, DateTime? rawLatest)
        => rawLatest is { } rl && rl.Date >= dayLatest.Date
        && rawEarliest is { } re && re.Date <= dayEarliest.Date.AddDays(30);

    /// <summary>本地还有多少只个股缺不复权日线（给界面显示待办量）。</summary>
    public int GetPendingRawBarCount()
    {
        try
        {
            var repo = new SqliteBarRepository(_paths.CurrentDb);
            var targets = RawBarTargetCodes();
            var dayLatest = repo.GetLatestPeriodStartByCode(Granularity.Day);
            var dayEarliest = repo.GetEarliestPeriodStartByCode(Granularity.Day);
            var rawLatest = repo.GetLatestPeriodStartByCode(Granularity.DayRaw);
            var rawEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
            return dayLatest.Count(kv => targets.Contains(kv.Key)
                && dayEarliest.TryGetValue(kv.Key, out var de)
                && !RawBarsComplete(de, kv.Value,
                        rawEarliest.TryGetValue(kv.Key, out var re) ? re : null,
                        rawLatest.TryGetValue(kv.Key, out var rl) ? rl : null));
        }
        catch { return 0; }
    }

    /// <summary>
    /// 补不复权日线（day_raw）。首次要把每只个股的历史补齐到跟前复权一样长，之后每天只增量一根。
    /// 支持 <paramref name="maxCount"/> 分轮，适合挂在计划的「空闲时」慢慢跑。
    /// </summary>
    public async Task<FetchResult> RunFetchRawBarsAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default, int? maxCount = null)
    {
        var result = new FetchResult();
        var repo = new SqliteBarRepository(_paths.CurrentDb);
        repo.EnsureSchema();

        // ⚠ 下面这四次 GetXxxPeriodStartByCode 每次都是对 Bar 表（1300 万行）做全表 GROUP BY，
        //   加起来要几十秒。**先说一声**，否则点完【执行】之后日志一直不动，
        //   用户以为没点上会反复点（2026-09-01 反馈）。
        progress?.Report("正在统计还差哪些股票的不复权日线（要扫一遍全库的日线索引，通常几十秒，请稍等）…");

        // 以前复权为准：它抓到哪天、历史从哪天起，不复权就补到一样的范围
        var dayLatest = repo.GetLatestPeriodStartByCode(Granularity.Day);
        var dayEarliest = repo.GetEarliestPeriodStartByCode(Granularity.Day);
        var rawLatest = repo.GetLatestPeriodStartByCode(Granularity.DayRaw);

        var rawEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);

        var targets = RawBarTargetCodes();          // 只要个股和退市股，见 NeedsRawBars
        var todo = dayLatest
            .Where(kv => targets.Contains(kv.Key)
                      && dayEarliest.TryGetValue(kv.Key, out var de)
                      && !RawBarsComplete(de, kv.Value,
                              rawEarliest.TryGetValue(kv.Key, out var re) ? re : null,
                              rawLatest.TryGetValue(kv.Key, out var rl) ? rl : null))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        if (todo.Count == 0)
        {
            progress?.Report("不复权日线已经跟前复权一样齐了，这一轮没什么可做。");
            result.NothingToDo = true;
            return result;
        }

        var batch = maxCount is > 0 ? todo.Take(maxCount.Value).ToList() : todo;
        progress?.Report($"补不复权日线：还差 {todo.Count} 只，本轮补 {batch.Count} 只"
                       + (batch.Count < todo.Count ? "（其余下一轮继续）" : "")
                       + "——不复权是原始成交价，抓过就永远有效，不会因为分红而失效。");

        var errors = new ConcurrentBag<string>();
        var failed = new ConcurrentBag<string>();
        var stats = new FetchStats();
        var sw = Stopwatch.StartNew();
        int done = 0;

        await Task.WhenAll(batch.Select(code =>
        {
            // 窗口从**前复权的最早那天**起，一直到今天。
            // ⚠ 不能写成"从不复权的水位线次日续"：缺的往往是**开头**而不是尾巴
            // （「拉取全部」按回看年数只填了最近 3 年，见 RawBarsComplete 的注释），
            // 从水位线往后续等于永远补不到前面那几年。
            // 整段重抓不会重复写：入库走 InsertOrIgnore，已经有的行原样跳过。
            DateTime start = dayEarliest.TryGetValue(code, out var e)
                ? e : DateTime.Today.AddYears(-DefaultLookbackYears);
            return ProcessOneStockAsync(code, source, start, DateTime.Today, repo, errors, failed,
                stats, progress, batch.Count, () => Interlocked.Increment(ref done), sw, ct,
                Granularity.DayRaw, overwrite: false);
        }));

        result.Errors.AddRange(errors);
        int left = GetPendingRawBarCount();
        progress?.Report($"不复权日线补齐 {batch.Count} 只，还差 {left} 只"
                       + (left > 0 ? "（下一轮空闲时自动继续）" : "，已全部齐了")
                       + $"，用时 {FormatElapsed(sw.Elapsed)}。");
        return result;
    }

    /// <summary>判据本体在 <see cref="Sqlite.SqliteAdjSeriesAuditor"/>（2026-09-09 抽走，原来这里和
    /// <see cref="RunRebuildAdjSeriesAsync"/> 各写了一份同样的判据）——这里只是转发。</summary>
    private HashSet<string> CodesWithStaleAdjEvents() =>
        new Sqlite.SqliteAdjSeriesAuditor(_paths.CurrentDb).CodesWithStaleEvents();

    /// <summary>
    /// 分档资金流的历史还差多少只（2026-09-04 加；2026-09-06 换了判据）——
    /// 返回 (历史不足 120 天的只数, 其中库里一行都没有的只数)，取不到返回 null。
    ///
    /// 判据本体在 <see cref="MoneyFlowBackfillPlan"/>（2026-09-11 抽走）：【拉取分档资金流】
    /// 迁成新式任务之后，排队判据和这个计数必须是同一份，否则界面显示 0 而任务还在抓、
    /// 或者反过来，谁都不会发现。这里只是转发。
    ///
    /// 便宜：一条 GROUP BY 扫 62 万行，不像 GetPendingAdjRebuildCount 那样要对 1300 万行的
    /// Bar 表扫四遍——放在 RefreshFailedCodeCount 里不会拖慢它。
    /// </summary>
    public (int Todo, int Never)? GetPendingMoneyFlowCount()
    {
        if (_moneyFlowRepository == null) return null;
        try
        {
            var codes = LocalStockCodes();
            if (codes.Count == 0) return null;
            var q = MoneyFlowBackfillPlan.Build(_paths.CurrentDb, codes, _moneyFlowRepository,
                                                MoneyFlowBackfillPlan.DefaultGapThreshold);
            return (q.Todo.Count, q.Never);
        }
        catch { return null; }
    }

    /// <summary>
    /// 【分档资金流快照】那一行要显示的**当天齐不齐**（2026-09-16 用户要求）。
    ///
    /// 为什么这一行比别的行多一个"标红"：别的项参数格里那些计数（还差多少只历史、多少个板块）
    /// 是"慢慢补"的进度，晚几天没关系；这一项说的是**今天的数据在不在**，而它的接口只给
    /// 最近一个交易日——今天没拿到，下一个交易日开盘后就永久没了。所以它不只是个数字，
    /// 缺了必须红着提醒人现在就补。
    ///
    /// 判据跟任务收尾时那次核对是同一份（<see cref="Sqlite.SqliteMoneyFlowDayAudit"/>）：
    /// 两处各写一份的话，会出现"任务说齐了、界面说不齐"这种谁也不信谁的状态。
    ///
    /// 便宜：三条走索引的 COUNT（当天日K走 ix_bar_gran_date、资金流走 ix_flowdetail_date），
    /// 本机实测 0.1 秒以内。
    /// </summary>
    public Sqlite.MoneyFlowDayStatus? GetMoneyFlowDayStatus()
    {
        try { return new Sqlite.SqliteMoneyFlowDayAudit(_paths.CurrentDb).Check(); }
        catch { return null; }
    }

    /// <summary>
    /// 本地有多少只个股的回测序列需要重算（界面刷新计数用）。判据在
    /// <see cref="Sqlite.SqliteAdjSeriesAuditor"/>，跟【重算回测序列】用的是同一份。
    ///
    /// ⚠ 那一项本身 2026-09-10 迁去了 <c>StockPlatform.Tasks/AdjSeriesRebuildTask</c>，
    /// 判据一行没动、搬走的只是外面那圈循环。**这个方法留在这儿**是因为界面拿它刷新
    /// "待重算 N 只"，走的是 orchestrator 而不是任务实例。
    /// </summary>
    public int GetPendingAdjRebuildCount() =>
        new Sqlite.SqliteAdjSeriesAuditor(_paths.CurrentDb).PendingCount();

    /// <summary>本地还有多少只股票等着重取前复权（给界面显示待办量）。</summary>
    public int GetPendingQfqRepairCount()
    {
        try
        {
            lock (_dbLock) return _manifestStore.Load().PendingQfqRepairCodes.Count;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 重取前复权全历史（2026-08-31 新增）——把 <see cref="RecordDriftedForRepair"/> 记下的股票
    /// 逐只从本地最早一根重抓到今天、整段覆盖，取成一只从名单里划掉一只。
    ///
    /// ════ 为什么要有这件事 ════
    /// 数据源的前复权是"原价 − 之后累计分红送配"，**基准随抓取时点变化**：某只票一分红，它全部
    /// 历史的前复权值就都变了。而本地历史是分批入库的，于是同一只股票不同时间段落在不同基准上，
    /// 接缝处出现假跳空（实测有股票虚增 50%）。后复权不受影响。
    ///
    /// ════ 为什么单独做成一个任务 ════
    /// 分红季一天上百只，每只要重抓十年。以前混在"拉取全部"里当场修，既拖慢当轮、又只能看到
    /// 一个数字、还得限量 200 只/轮。现在记名单、由计划里的【重取前复权】在空闲时补，
    /// 跟"拉取财务报表"一个路子：能看见还剩多少、可以随时停、取过的不会重取。
    /// </summary>
    /// <param name="maxCount">本轮最多取几只（空闲时段塞得下多少就取多少）；null=一次取完。</param>
    public async Task<FetchResult> RunRepairQfqAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default, int? maxCount = null)
    {
        var result = new FetchResult();
        List<string> pending;
        lock (_dbLock) pending = _manifestStore.Load().PendingQfqRepairCodes.ToList();

        if (pending.Count == 0)
        {
            progress?.Report("待重取前复权的名单是空的——没有股票的复权基准发生过漂移，这一轮没什么可做。");
            result.NothingToDo = true;
            return result;
        }

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        var earliestByCode = currentRepo.GetEarliestPeriodStartByCode(Granularity.Day);
        var batch = maxCount is > 0 ? pending.Take(maxCount.Value).ToList() : pending;

        progress?.Report($"待重取前复权 {pending.Count} 只，本轮取 {batch.Count} 只"
                       + (batch.Count < pending.Count ? "（其余下一轮继续）" : "")
                       + "——每只从本地最早一根按数据源当前基准整段重写。");

        var errors = new ConcurrentBag<string>();
        var failed = new ConcurrentBag<string>();
        var stats = new FetchStats();
        var sw = Stopwatch.StartNew();
        var doneCodes = new ConcurrentBag<string>();
        int done = 0;

        await Task.WhenAll(batch.Select(async code =>
        {
            var start = earliestByCode.TryGetValue(code, out var e) ? e : DateTime.Today.AddYears(-DefaultLookbackYears);
            try
            {
                await ProcessOneStockAsync(code, source, start, DateTime.Today, currentRepo, errors, failed,
                    stats, progress, batch.Count, () => Interlocked.Increment(ref done), sw, ct,
                    Granularity.Day, overwrite: true);
                doneCodes.Add(code);      // 只有真跑完的才从名单里划掉
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{code} 重取前复权失败：{ex.Message}"); }
        }));

        var finished = doneCodes.ToHashSet(StringComparer.Ordinal);
        int left;
        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.PendingQfqRepairCodes = manifest.PendingQfqRepairCodes
                .Where(c => !finished.Contains(c)).ToList();
            left = manifest.PendingQfqRepairCodes.Count;
            _manifestStore.Save(manifest);
        }

        result.Errors.AddRange(errors);
        progress?.Report($"重取前复权完成：{finished.Count} 只已按新基准重写，还剩 {left} 只"
                       + (left > 0 ? "（下一轮空闲时自动继续）" : "，名单已清空") + $"，用时 {FormatElapsed(sw.Elapsed)}。");
        return result;
    }

    /// <summary>
    /// 后复权日线阶段（2026-07-30新增，三个抓取入口都会跑）——回测专用的价格序列，理由见
    /// <see cref="Granularity.DayHfq"/>。<paramref name="windowFor"/> 由调用方给出每只标的要抓的
    /// [start, end]（"拉取全部/当天"按各自的水位线规则、"拉取区间数据"按 <see cref="YearGapFor"/> 的
    /// 缺口规则），本方法只负责统一跳过不支持的数据源、报进度、并发抓取。
    ///
    /// 注意水位线读的是 <see cref="Granularity.DayHfq"/> 自己的，与前复权互不影响——所以库里还没有
    /// 后复权数据时，各入口会自然地把它补上，不需要额外的一次性开关；但"拉取全部/当天"的窗口只回看
    /// <see cref="DefaultLookbackYears"/> 年，要一次补齐十年历史仍需跑一次"拉取区间数据"。
    /// </summary>
    /// <summary>
    /// 抓**前复权之外的另一套日线**：后复权（<see cref="Granularity.DayHfq"/>）或不复权
    /// （<see cref="Granularity.DayRaw"/>）。两者的抓取流程一模一样——独立水位线、起飞前探一只、
    /// 失败率过高熔断——所以合成一个方法，靠 <paramref name="gran"/> 区分。
    /// 能不能抓由 <c>SupportsHfq</c> 决定：这两套口径目前都只有腾讯给（新浪只有前复权）。
    /// </summary>
    private async Task<List<string>> FetchHfqBarsAsync(
        NamedBarSource source, IReadOnlyList<string> codes, Func<string, (DateTime Start, DateTime End)> windowFor,
        SqliteBarRepository currentRepo, ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes,
        FetchStats stats, IProgress<string>? progress, Stopwatch sw, CancellationToken ct, string label = "",
        string gran = Granularity.DayHfq,
        ConcurrentBag<(string Code, DateTime End)>? emptyRangeProbes = null)
    {
        string kind = gran == Granularity.DayRaw ? "不复权" : "后复权";
        if (!source.Fetcher.SupportsHfq)
        {
            progress?.Report($"（数据源 {source.Name} 不提供{kind}，跳过回测用的{kind}日线——要补请把数据源切到 Tencent）");
            return new List<string>();
        }
        if (codes.Count == 0) return new List<string>();

        // ── 先把每只的抓取窗口算出来，分清"真要抓"和"本地已是最新"（2026-09-02）──
        //
        // 为什么要提前算：① 日志能说清楚——原来只报"5554 只"，看着像要把全市场抓一遍，
        // 其实绝大多数在水位线判定里直接跳过（实测 4 秒跑完 5554 只，用户因此问"不是增量吗"）；
        // ② 全都不用抓时连探测请求都能省掉；③ 窗口只算一次，不用在循环里重查一遍库。
        var windows = codes.Select(c => (Code: c, Window: windowFor(c))).ToList();
        var toFetch = windows.Where(w => w.Window.Start.Date <= w.Window.End.Date).ToList();
        int upToDate = windows.Count - toFetch.Count;
        for (int i = 0; i < upToDate; i++) stats.Skip();   // 汇总里照样记成"跳过"

        if (toFetch.Count == 0)
        {
            progress?.Report($"{label}{kind}日K：{codes.Count} 只本地都已是最新，这一轮无需抓取（一个请求都没发）。");
            return new List<string>();
        }

        progress?.Report($"开始抓{label}{kind}日K：{codes.Count} 只里有 {toFetch.Count} 只要抓、"
                       + $"{upToDate} 只本地已是最新（按各自水位线跳过，不发请求）。回测专用；前复权已有的不受影响...");

        // 起飞前先探一只：接口挂了/被限流/换了返回格式时，立刻停这一轮并说清楚原因，
        // 而不是对着几千只股票空跑几小时（用户 2026-07-30 反馈：取不到数据就该直接停）。
        var probeCode = toFetch[0].Code;
        try
        {
            var (_, probeBars) = await source.Fetcher.FetchAsync(probeCode, gran,
                DateTime.Today.AddYears(-1), DateTime.Today, ct);
            if (probeBars.Count == 0)
                throw new InvalidOperationException("接口返回空数据（可能是返回格式变了，或该代码已无数据）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = $"{kind}探测失败（用 {probeCode} 试抓最近一年）：{ex.Message}。本轮跳过{kind}，不做无谓的空跑——" +
                      "前复权数据不受影响，排查好接口后重跑即可。";
            errors.Add(msg);
            progress?.Report("⚠ " + msg);
            return new List<string>();
        }

        // 跑起来之后再加一道熔断：本轮失败率过高就主动中止，同样是为了不空跑
        var phaseFailed = new ConcurrentBag<string>();
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int done = 0;
        bool aborted = false;
        try
        {
            // 只遍历真要抓的那些，窗口用上面算好的（别再查一遍库）
            await Task.WhenAll(toFetch.Select(async item =>
            {
                var (code, (s, e)) = item;
                await ProcessOneStockAsync(code, source, s, e, currentRepo, errors, phaseFailed, stats, progress,
                    toFetch.Count, () => Interlocked.Increment(ref done), sw, abortCts.Token, gran,
                    emptyRangeProbes: emptyRangeProbes);

                int finished = Volatile.Read(ref done);
                if (finished >= HfqAbortCheckAfter && phaseFailed.Count > finished * 0.9 && !abortCts.IsCancellationRequested)
                {
                    aborted = true;
                    progress?.Report($"⚠ {label}{kind}连续失败（已完成 {finished} 只、失败 {phaseFailed.Count} 只），主动中止本轮{kind}抓取。" +
                                     "前复权数据不受影响，排查好数据源后重跑即可。");
                    abortCts.Cancel();
                }
            }));
        }
        catch (OperationCanceledException) when (aborted && !ct.IsCancellationRequested)
        {
            // 熔断触发的取消，不是用户点了停止——吞掉，让本轮其余阶段继续
        }
        foreach (var c in phaseFailed) failedCodes.Add(c);

        progress?.Report(aborted
            ? $"{label}{kind}日K已中止（失败 {phaseFailed.Count} 只）。"
            : $"{label}{kind}日K完成（{codes.Count} 只，失败 {phaseFailed.Count} 只）。");
        return codes.ToList();
    }

    /// <summary>"拉取全部/当天"给后复权、不复权用的水位线窗口：跟前复权同一套规则，只是读该口径
    /// 自己的水位线（所以第一次跑会按 <see cref="DefaultLookbackYears"/> 年回看，不会因为前复权
    /// 已经是最新就跳过）。</summary>
    private (DateTime Start, DateTime End) HfqWatermarkWindow(
        SqliteBarRepository currentRepo, string code, DateTime end, int lookbackYears,
        string gran = Granularity.DayHfq)
    {
        lock (_dbLock)
        {
            return (IncrementalStart(currentRepo.GetLatestBarInfo(code, gran), end, lookbackYears), end);
        }
    }

    /// <summary>
    /// 退市股收尾（2026-07-29新增，"拉取当天"与"拉取全部"末尾各跑一次）——补的是一个真实存在的数据黑洞：
    /// 某只股票一旦退市，"拉取当天"的本地清单里它还在（type 还是 stock），但数据源已经不再有它的新数据；
    /// 而它**最后几个交易日**（退市整理期，往往正是跌得最惨那段、对回测最关键）如果那几天没抓到，
    /// 之后就永久缺失了。放在两个日常入口的**末尾**是刻意的：本轮的抓取清单在开头就取好了，
    /// 这里把它们标成 type='delisted' 不会影响本轮；下一轮起它们不再被日常轮询（省掉几百个必然落空的请求），
    /// 缺的尾巴由这里补。
    ///
    /// 只补满足"**本地已有历史** 且 **本地最后一根K线早于终止日** 且 **没尝试过**"的那几只——正常情况下
    /// 0 只，偶尔 1~2 只新退市的。`tail_fetched_at` 标记不可少：停牌后才退市的股票（K线止于停牌日、
    /// 永远早于终止日）没有标记就会每天徒劳重抓；抓取失败的不打标记，留待下次重试。
    /// 故意**不**在这里做全量回补（那是"拉取区间数据"的活；退市股历史补过就不会再变，每天重扫毫无意义）。
    /// 名单抓取失败只记 error、不影响本轮K线。
    /// </summary>
    private async Task<List<string>> CatchUpDelistedTailsAsync(
        NamedBarSource source, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        if (_delistedListProvider == null) return new List<string>();

        List<DelistedStockRow> all;
        try
        {
            progress?.Report("刷新退市名单（顺带补新退市股缺失的最后几天K线）...");
            all = await _delistedListProvider.GetAllAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"刷新退市名单失败（跳过，不影响本轮K线）：{ex.Message}");
            return new List<string>();
        }

        var delistedRepo = new SqliteDelistedRepository(_paths.CurrentDb);
        delistedRepo.Upsert(all);
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, all.Select(r => (r.Code, r.Name)), SqliteStockMetaUpsert.TypeDelisted);

        var tailPending = delistedRepo.GetTailPendingCodes();
        var latestByCode = currentRepo.GetLatestPeriodStartByCode(Granularity.Day);
        var latestHfq = currentRepo.GetLatestPeriodStartByCode(Granularity.DayHfq);
        var latestRaw = currentRepo.GetLatestPeriodStartByCode(Granularity.DayRaw);
        var pending = all
            .Where(r => r.DelistDate is { } dd
                        && tailPending.Contains(r.Code)
                        && latestByCode.TryGetValue(r.Code, out var latest)
                        && latest.Date < dd.Date)
            .ToList();
        if (pending.Count == 0)
        {
            progress?.Report($"退市名单已刷新（{all.Count} 只），没有需要补最后几天的退市股。");
            return new List<string>();
        }

        progress?.Report($"退市名单已刷新（{all.Count} 只），其中 {pending.Count} 只本地缺最后几天，正在补...");
        int done = 0;
        await Task.WhenAll(pending.Select(r =>
        {
            var start = latestByCode[r.Code].AddDays(1);
            return ProcessOneStockAsync(r.Code, source, start, r.DelistDate!.Value, currentRepo, errors, failedCodes,
                stats, progress, pending.Count, () => Interlocked.Increment(ref done), sw, ct);
        }));

        // 后复权的尾巴也要补（回测吃的是它）。窗口按 day_hfq 自己的最后一根算：完全没有后复权历史的
        // 就从终止日往前回看 DefaultLookbackYears 年，一次把这只退市股的后复权历史抓够。
        await FetchHfqBarsAsync(source, pending.Select(r => r.Code).ToList(),
            code =>
            {
                var row = pending.First(x => x.Code == code);
                var end = row.DelistDate!.Value;
                var start = latestHfq.TryGetValue(code, out var lh) ? lh.AddDays(1) : end.AddYears(-DefaultLookbackYears);
                return (start, end);
            },
            currentRepo, errors, failedCodes, stats, progress, sw, ct, "退市股");

        // 不复权的尾巴同样要补：day_adj 是拿它算的，缺了这几天，这只退市股的回测序列就断在这儿。
        // 而退市整理期恰恰是回测最需要的那一段（跌得最惨、也最能检验风控规则）。
        await FetchHfqBarsAsync(source, pending.Select(r => r.Code).ToList(),
            code =>
            {
                var row = pending.First(x => x.Code == code);
                var end = row.DelistDate!.Value;
                var start = latestRaw.TryGetValue(code, out var lr) ? lr.AddDays(1) : end.AddYears(-DefaultLookbackYears);
                return (start, end);
            },
            currentRepo, errors, failedCodes, stats, progress, sw, ct, "退市股", Granularity.DayRaw);

        // 成功尝试过的打标记（哪怕"没有更多数据"——停牌到退市的股票本来就不会再有K线）；失败的留到下次。
        // 前复权或后复权任一失败都不打标记，下次重试。
        var failed = failedCodes.ToHashSet(StringComparer.Ordinal);
        var attempted = pending.Select(r => r.Code).ToList();
        delistedRepo.MarkTailFetched(attempted.Where(c => !failed.Contains(c)));

        progress?.Report($"退市股收尾完成（{pending.Count} 只）：" + string.Join("、", pending.Take(5).Select(r => $"{r.Code} {r.Name}")) +
                         (pending.Count > 5 ? " 等" : ""));
        return attempted;
    }

    /// <summary>
    /// 退市股阶段（2026-07-29新增，并入"拉取区间数据"）——消除回测幸存者偏差的关键一步：日常抓取只覆盖
    /// 当前在市的股票，历史回测的股票池里因此缺了"后来退市的输家"，回测收益被系统性高估（回测期越长越严重）。
    /// ① 从沪深两所官网取"终止上市公司"全名单（见 <see cref="ExchangeDelistedListProvider"/>，2026-07-29
    /// 实测两接口可用、腾讯K线对退市代码能给到最后交易日的完整历史）→ 整体刷新 DelistedStock 表，并以
    /// type='delisted' 写入 StockMeta（不用 'stock'，避免被"拉取全部/当天"的日常轮询白抓）；
    /// ② 对终止日落在区间起点之后的退市股，补 [区间起点 → 各自终止日] 的日K（并重算周月线），增量语义与
    /// 区间内其它标的一致（<see cref="YearGapFor"/> 只补缺口、可反复点、可随时停）。
    /// 上交所部分行（转板/吸收合并）没有终止日，保守地当作"可能相关"一并尝试补取（数据源对无效区间返回空，无害）。
    /// 名单接口偶发抽风时只记一条错误、跳过这一段，不让整轮区间抓取失败。
    /// 注意：新补的退市股K线同样是当前时点的前复权口径；退市股没有后续分红除权，复权基准冻结在最后交易日，
    /// 反而没有在市股票的基准漂移问题。
    /// </summary>
    private async Task<List<string>> FetchDelistedForRangeAsync(
        NamedBarSource source, DateTime rangeStart, DateTime rangeEnd, string rangeLabel,
        SqliteBarRepository currentRepo, Dictionary<string, DateTime> earliestByCode,
        Dictionary<string, DateTime> earliestHfq,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct, ProbeFloors? floors = null)
    {
        if (_delistedListProvider == null)
        {
            progress?.Report("（未配置退市名单数据源，跳过退市股）");
            return new List<string>();
        }

        // 退市股这一段跟个股那三段一样吃"数据源没有更早数据"的水位：候选里绝大多数的本地最早日
        // 就是它自己的上市日，缺口 [区间起点, 上市日-1] 每轮都会算出来、抓回来都是空。
        // 2000+ 只 × 3 个粒度 ≈ 6000 个请求、一小时，全花在重新确认上一轮已经确认过的事情上。
        var emptyDay = new ConcurrentBag<(string Code, DateTime End)>();
        var emptyHfq = new ConcurrentBag<(string Code, DateTime End)>();
        var emptyRaw = new ConcurrentBag<(string Code, DateTime End)>();

        List<DelistedStockRow> all;
        try
        {
            progress?.Report("正在获取沪深两所官网的终止上市公司名单...");
            all = await _delistedListProvider.GetAllAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 名单接口偶发抽风不该让整轮区间抓取失败——记一条错误、跳过退市股这一段即可
            errors.Add($"获取退市名单失败（跳过退市股）：{ex.Message}");
            progress?.Report($"获取退市名单失败，跳过退市股这一段：{ex.Message}");
            return new List<string>();
        }

        progress?.Report($"两所合计 {all.Count} 只已退市A股，写入 DelistedStock 表与 StockMeta(type=delisted)。");
        new SqliteDelistedRepository(_paths.CurrentDb).Upsert(all);
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, all.Select(r => (r.Code, r.Name)), SqliteStockMetaUpsert.TypeDelisted);

        // 只有终止日在区间起点之后的退市股才在回测窗口里交易过；终止日缺失的保守纳入
        var candidates = all.Where(r => r.DelistDate == null || r.DelistDate.Value.Date >= rangeStart).ToList();
        progress?.Report($"其中终止日在 {rangeStart:yyyy-MM-dd} 之后（或缺失）的 {candidates.Count} 只，" +
                         $"开始补 [{rangeLabel}区间起点 → 各自终止日] 的日K（只补本地缺口）...");

        int done = 0;
        await Task.WhenAll(candidates.Select(r =>
        {
            var stockEnd = r.DelistDate is { } dd && dd < rangeEnd ? dd : rangeEnd;
            var (s, e) = YearGapFor(r.Code, earliestByCode, rangeStart, stockEnd, null, floors?.Day);
            return ProcessOneStockAsync(r.Code, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                candidates.Count, () => Interlocked.Increment(ref done), sw, ct,
                emptyRangeProbes: emptyDay);
        }));
        progress?.Report($"退市股日K补齐完成（{candidates.Count} 只）。");
        if (floors != null) RecordProbeFloors(floors.Repo, emptyDay, earliestByCode, Granularity.Day, "退市股前复权", progress);

        // 退市股的后复权同样要补——回测股票池里少了它们就等于幸存者偏差没修干净
        await FetchHfqBarsAsync(source, candidates.Select(r => r.Code).ToList(),
            code =>
            {
                var row = candidates.First(x => x.Code == code);
                var stockEnd = row.DelistDate is { } dd && dd < rangeEnd ? dd : rangeEnd;
                return YearGapFor(code, earliestHfq, rangeStart, stockEnd, null, floors?.Hfq);
            },
            currentRepo, errors, failedCodes, stats, progress, sw, ct, "退市股",
            emptyRangeProbes: emptyHfq);
        if (floors != null) RecordProbeFloors(floors.Repo, emptyHfq, earliestHfq, Granularity.DayHfq, "退市股后复权", progress);

        // 不复权同理——见上面那处的注释
        var earliestRawD = currentRepo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        await FetchHfqBarsAsync(source, candidates.Select(r => r.Code).ToList(),
            code =>
            {
                var row = candidates.First(x => x.Code == code);
                var stockEnd = row.DelistDate is { } dd && dd < rangeEnd ? dd : rangeEnd;
                return YearGapFor(code, earliestRawD, rangeStart, stockEnd, null, floors?.Raw);
            },
            currentRepo, errors, failedCodes, stats, progress, sw, ct, "退市股", Granularity.DayRaw,
            emptyRangeProbes: emptyRaw);
        if (floors != null) RecordProbeFloors(floors.Repo, emptyRaw, earliestRawD, Granularity.DayRaw, "退市股不复权", progress);

        return candidates.Select(r => r.Code).ToList();
    }

    /// <summary>
    /// 按年份补历史时，算出某个标的在目标年里真正需要请求的 [start, end]——本地最早日已早于年初就返回
    /// 一个空区间（start &gt; end，<see cref="ProcessOneStockAsync"/> 会直接记 Skip、不发请求）；最早日
    /// 落在目标年内就只补"年初 → 最早日前一天"；最早日在年末之后（或本地没有该标的）就抓一整年。
    /// 依赖"本地历史是连续的"这一前提——增量抓取永远是从水位线往后连续推进的，所以只需要看最早日一个点。
    /// </summary>
    /// <remarks>
    /// 具体规则连同"日历覆盖不到就不敢跳过"那道前提，2026-09-06 一起抽到了
    /// <see cref="YearGapCalculator"/>（纯计算、可单测）。这里只留一层转发，保持既有调用点不变。
    /// </remarks>
    private static (DateTime Start, DateTime End) YearGapFor(
        string code, Dictionary<string, DateTime> earliestByCode, DateTime yearStart, DateTime yearEnd,
        TradingCalendar? calendar = null, IReadOnlyDictionary<string, DateTime>? noDataBefore = null)
        => YearGapCalculator.For(code, earliestByCode, yearStart, yearEnd, calendar, noDataBefore);

    /// <summary>一轮区间回补里共用的"已探明水位"——三个粒度各一份，外加落库用的仓储。
    /// 打包成一个参数纯粹是为了不让 <see cref="FetchDelistedForRangeAsync"/> 的参数表再长四个。</summary>
    private sealed record ProbeFloors(
        SqliteBarProbeFloorRepository Repo,
        IReadOnlyDictionary<string, DateTime> Day,
        IReadOnlyDictionary<string, DateTime> Hfq,
        IReadOnlyDictionary<string, DateTime> Raw);

    /// <summary>
    /// 把本轮"请求成功、但返回 0 行"的结论落成永久水位（<c>BarProbeFloor</c> 表）——下一轮同一区间
    /// 就不用再试这些票了。哪些能落、落到哪一天，判定全在 <see cref="ProbeFloorPlanner"/>（纯计算、
    /// 可单测；那里两道前提写得很清楚，记错一条会让一只票的历史永久跳过）。
    ///
    /// 落库失败**不**让整轮回补算失败：水位只是省请求的优化，丢了下一轮重探一遍而已。
    /// </summary>
    private static void RecordProbeFloors(
        SqliteBarProbeFloorRepository repo, ConcurrentBag<(string Code, DateTime End)> probes,
        Dictionary<string, DateTime> earliestByCode, string granularity, string kind, IProgress<string>? progress)
    {
        if (probes.IsEmpty) return;
        try
        {
            var plan = ProbeFloorPlanner.Plan(probes, earliestByCode);
            if (plan.Count == 0) return;
            repo.Record(plan, granularity);
            progress?.Report($"{kind}：本轮 {probes.Count} 只返回空，其中 {plan.Count} 只可判定为"
                           + "\"数据源没有更早数据\"，已记下水位——下一轮同区间不再请求它们。");
        }
        catch (Exception ex)
        {
            progress?.Report($"（记\"数据源没有更早数据\"水位时出错，不影响本轮补齐：{ex.Message}）");
        }
    }

    /// <summary>本地已知的交易日历——给 <see cref="YearGapFor"/> 判断"这段缺口里到底有没有交易日"用。
    /// 取不到就返回 null，调用方退回到不判交易日的老行为（宁可多发请求，也不静默漏抓）。
    ///
    /// ════ 为什么取全市场 day_raw 的并集，而不是单只指数 ════
    /// 原来取的是上证指数的 day 序列。那只票自己缺哪一段，日历就瞎哪一段——2026-09-06 实测：
    /// 上证指数的 day 也只有 2016-01-04 起，于是【拉取区间数据 1990~2016】把 2,360 只最该补历史的
    /// 老股判成"缺口里没有交易日"，一个请求都没发就跳过了。
    /// 不复权（day_raw）是全库唯一"抓一次永久有效"的序列、且覆盖全市场，用它的日期并集当日历，
    /// 只要有任何一只票在某天有K线，那天就一定被认成交易日，不会再有这种盲区。
    /// 并集查询走 ix_bar_gran_date(granularity, period_start) 索引，且每轮只查一次。</summary>
    private static TradingCalendar? LocalTradingDays(SqliteBarRepository repo)
    {
        try
        {
            var days = repo.GetDistinctPeriodStarts(Granularity.DayRaw);
            if (days.Count == 0)   // 空库或还没抓过不复权 → 退回老口径（上证指数 day）
                days = repo.Query(MarketIndexCatalog.All[0].Symbol, Granularity.Day)
                           .Select(b => b.PeriodStart.Date).ToList();
            return days.Count > 0 ? new TradingCalendar(days) : null;
        }
        catch { return null; }
    }

    /// <summary>按年份补 ETF 日K——ETF列表仍要联网取一次（本地 StockMeta 里的 type=etf 也可以，但列表接口
    /// 便宜且能顺带发现新ETF），窗口计算与个股共用 <see cref="YearGapFor"/>。取不到列表就跳过ETF、不算失败。</summary>
    private async Task<List<string>> FetchEtfBarsForYearAsync(
        NamedBarSource source, DateTime yearStart, DateTime yearEnd, Dictionary<string, DateTime> earliestByCode,
        SqliteBarRepository currentRepo, ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes,
        FetchStats stats, IProgress<string>? progress, Stopwatch sw, CancellationToken ct,
        TradingCalendar? tradingDays = null, ProbeFloors? floors = null)
    {
        if (_etfListProvider == null) return new List<string>();
        List<StockListEntry> etfs;
        try { etfs = await _etfListProvider.GetAllStocksAsync(progress, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"获取ETF列表失败（跳过ETF）：{ex.Message}"); return new List<string>(); }
        if (etfs.Count == 0) { progress?.Report("ETF列表为空（接口可能不可达/被限流），本轮跳过ETF。"); return new List<string>(); }

        progress?.Report($"开始补 {yearStart:yyyy}~{yearEnd:yyyy} 年 ETF 日K（{etfs.Count} 只）...");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, etfs.Select(e => (e.Code, e.Name)), SqliteStockMetaUpsert.TypeEtf);
        // ETF 只有前复权一路（不复权/后复权只对个股抓），所以【回填"无更早数据"水位】那一项
        // 不敢凭一路数据给它们下结论、一条都不填。它们的水位只能靠**真探测**攒：这里照旧发请求，
        // 返回空就记一条，下一轮起就不再请求那一段。漏了这个接线的话，"留给真探测"就是句空话——
        // 1655 只 ETF 每轮都要重抓一遍（约 20 分钟），永远攒不下结论。
        var emptyEtf = new ConcurrentBag<(string Code, DateTime End)>();
        int done = 0;
        await Task.WhenAll(etfs.Select(etf =>
        {
            var (s, e) = YearGapFor(etf.Code, earliestByCode, yearStart, yearEnd, tradingDays, floors?.Day);
            return ProcessOneStockAsync(etf.Code, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                etfs.Count, () => Interlocked.Increment(ref done), sw, ct,
                emptyRangeProbes: emptyEtf);
        }));
        progress?.Report($"ETF 日K补齐完成（{etfs.Count} 只）。");
        if (floors != null)
            RecordProbeFloors(floors.Repo, emptyEtf, earliestByCode, Granularity.Day, "ETF", progress);
        return etfs.Select(e => e.Code).ToList();
    }

    // 【补整天缺失】FillMissingNetInflowDaysAsync 和【补残缺日】FillPartialDaysAsync
    // 双双删于 2026-09-18：
    //   · 前者只服务资金净流入，随 NetInflowTask 一起迁走（见 doc/netinflow-task-design.md）；
    //   · 后者在两融迁走时就只剩"转发给 PartialDayRepair 并报一句只能人工处理"的空壳——
    //     唯一还会走到它的资金净流入这次也自己补了，于是没有任何调用方。
    // 残缺日的编排本体一直在 PartialDayRepair（各任务共用），删掉的只是编排器这侧的转发。

    // PartialDaysOf 删于 2026-09-18：唯一的用户是两融整段回补，随 MarginTask 一起迁走了
    // （任务侧直接用 PartialDayRepair.DaysOf——"整段回补要把已知残缺日从 have 里扣掉，
    // 否则那些天会被当成'已有'永远跳过"这条规矩跟着搬了过去）。

    private async Task FetchNetInflowRangeAsync(
        IReadOnlyList<string> codes, DateTime rangeStart, DateTime rangeEnd, IProgress<string>? progress, CancellationToken ct)
    {
        var failedNetInflowCodes = new ConcurrentBag<string>();
        try
        {
            // 起点抬到数据源自己的起点（2010-03-01）——这一步对资金流不是"少跑几天"而是"少跑一整轮"：
            // 这个源一次返回整只票的全部历史、窗口在客户端裁，起点填 1990 的话每只票都算出
            // [1990, 本地最早日-1] 的缺口、一只都跳不过，全市场白抓一遍约 1 小时 45 分。
            // 见 INetInflowFetcher.EarliestAvailable。
            var floor = _netInflowFetcher.EarliestAvailable.ToDateTime(TimeOnly.MinValue);
            if (rangeStart < floor)
            {
                progress?.Report($"资金净流入：起点 {rangeStart:yyyy-MM-dd} 上提到 {floor:yyyy-MM-dd}" +
                                 "——该日之前这份数据源上根本不存在，不是漏抓。");
                rangeStart = floor;
            }
            if (rangeStart > rangeEnd)
            {
                progress?.Report($"资金净流入：区间 ~{rangeEnd:yyyy-MM-dd} 整段早于数据起点 {floor:yyyy-MM-dd}，无可补，跳过。");
                return;
            }

            var repo = new SqliteNetInflowRepository(_paths.CurrentDb);
            repo.EnsureSchema();
            var earliest = repo.GetEarliestPeriodStartByCode();
            progress?.Report($"开始补资金净流入 {rangeStart:yyyy-MM-dd} ~ {rangeEnd:yyyy-MM-dd}（共 {codes.Count} 只，逐只查询、只补本地缺的那段，会比较慢）...");

            int totalRows = 0, failCount = 0, skipCount = 0, done = 0;
            await Task.WhenAll(codes.Select(async code =>
            {
                var (start, end) = YearGapFor(code, earliest, rangeStart, rangeEnd);
                if (start.Date > end.Date) { Interlocked.Increment(ref skipCount); return; }

                List<NetInflow> rows;
                try { rows = await _netInflowFetcher.FetchAsync(code, start, end, ct); }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    Interlocked.Increment(ref failCount);
                    failedNetInflowCodes.Add(code);
                    return;
                }

                if (rows.Count > 0)
                {
                    lock (_dbLock)
                    {
                        var todaysRows = rows.Where(r => r.PeriodStart.Date == DateTime.Today).ToList();
                        var olderRows = rows.Where(r => r.PeriodStart.Date != DateTime.Today).ToList();
                        if (olderRows.Count > 0) repo.InsertOrIgnore(olderRows);
                        if (todaysRows.Count > 0) repo.Upsert(todaysRows);
                    }
                    Interlocked.Add(ref totalRows, rows.Count);
                }
                if (Interlocked.Increment(ref done) % 200 == 0)
                    progress?.Report($"资金净流入补齐中：已处理 {done}/{codes.Count} 只、写入 {totalRows} 条（跳过本地已有 {skipCount} 只，失败 {failCount} 只）");
            }));

            progress?.Report($"资金净流入补齐完成：写入 {totalRows} 条、跳过本地已有 {skipCount} 只" +
                             (failCount > 0 ? $"、{failCount} 只失败（可用\"重新拉取失败股票\"重试）" : ""));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress?.Report($"补资金净流入整体失败（不影响K线）：{ex.Message}");
            foreach (var code in codes) failedNetInflowCodes.Add(code);
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            SetFailedTodo(manifest, RetryTaskIds.NetInflow, codes, failedNetInflowCodes);
            _manifestStore.Save(manifest);
        }
    }

    /// <summary>
    /// 重新拉取失败股票（2026-07-08新增，2026-07-09扩展到市值/资金净流入）——针对
    /// <see cref="Manifest.FailedCodes"/>/<see cref="Manifest.FailedMarketCapCodes"/>/
    /// <see cref="Manifest.FailedNetInflowCodes"/> 三份名单分别重试，不重新扫描全市场股票列表、
    /// 不重新跑公告（公告本来就是全市场批量扫描，不按股票记录失败，下次跑"拉取全部"/"拉取当天"
    /// 自然会覆盖到）。市值的"重试"本质仍是整轮扫描（见 FetchMarketCapAsync 的类注释），不会比
    /// 正常跑一次更快，但至少能正确清零失败名单；资金净流入跟K线一样是逐只精确重试。可以反复
    /// 点击：只要重试完三份名单里任何一份还有剩，下次再点还是只处理剩下的那些。
    /// </summary>
    public async Task<FetchResult> RunRetryFailedAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        _marketCapFetcher.OnStatus += ForwardStatus;
        _netInflowFetcher.OnStatus += ForwardStatus;
        try
        {
            return await RunRetryFailedInternalAsync(source, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
            _marketCapFetcher.OnStatus -= ForwardStatus;
            _netInflowFetcher.OnStatus -= ForwardStatus;
        }
    }

    /// <summary>重试结束时的一句话总结（2026-08-19新增）。
    ///
    /// 加它的原因：以前跑完只在末尾留下"K线没有失败的股票需要重试"，紧接着就是"结束"，看起来像
    /// 整个操作什么都没干；实际上市值和资金流已经重试完、失败名单也清零了。现在把每一类实际做了
    /// 什么都列出来，并明确说名单已经更新，用户不用再去猜。</summary>
    private void ReportRetrySummary(IReadOnlyList<string> done, IProgress<string>? progress)
    {
        progress?.Report(done.Count == 0
            ? "本轮没有需要重试的项目"
            : "本轮重试完成：" + string.Join("、", done));

        var left = GetRetryBacklog();
        progress?.Report(left.Any
            ? $"仍有待重试：{left.Describe()}——可以再点一次这个按钮"
            : "失败名单已全部清零，没有遗留项目");
    }

    /// <summary>
    /// 【重新拉取失败】——2026-09-13 二期起它**自己不抓任何东西**，只是个调度器：
    /// 读统一待办清单 → 按 <see cref="RetryTodo.TaskId"/> 挨个调对应任务的"补待办"入口。
    ///
    /// 改成这样的原因：原来这里是一长串 <c>if (xxx.Count > 0)</c>，每加一类待办就要在这里
    /// 再写一段。于是 2026-09-02 加的历史空洞、09-06 加的资金流缺失日，执行链加上了、
    /// 界面摘要却没人回头改——显示"09-11日线 1 只"，点下去实际跑 1909 段、几个小时。
    /// 现在"有哪些待办"只有 <see cref="Manifest.MigrateLegacyTodos"/> 一处知道，
    /// 显示、按钮、这里的分派全读同一份，加一类待办漏不掉。
    /// </summary>
    private async Task<FetchResult> RunRetryFailedInternalAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法重新拉取，请先执行一次\"拉取全部\"");

        // ⚠ 判"这轮有没有活"必须走 RetryBacklog，**不能在这里手写一串 .Count == 0**
        //   （2026-09-13 修）：原来那八个 .Count 只数了几份失败名单，漏掉了体检写进来的
        //   历史空洞和资金流缺失日——名单里躺着 1909 段，只要那几份清零就会从这里直接
        //   返回"不需要重试"，**那 1909 段永远补不上而且一声不吭**。
        var backlog = RetryBacklog.From(_manifestStore.Load());
        if (!backlog.Any)
        {
            progress?.Report("目前没有记录到抓取失败或缺当天数据的股票，不需要重试");
            return new FetchResult();
        }

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();
        var sw = Stopwatch.StartNew();
        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var done = new List<string>();

        progress?.Report($"本轮要补：{backlog.Describe()}");

        var taskIds = DispatchOrder(backlog);
        var attempted = new List<string>();
        foreach (var taskId in taskIds)
        {
            ct.ThrowIfCancellationRequested();
            attempted.AddRange(await RunFillBacklogAsync(
                taskId, source, currentRepo, errors, failedCodes, done, progress, sw, ct));
        }

        // 收尾：本轮碰过的代码里这次没失败的一律移出失败名单（"之前失败、这次成功了"），
        // 并顺手再体检一次当天覆盖情况——上面刚补过的话名单得重建。
        var result = FinishFetchRun(errors, "重新拉取失败股票",
            attempted.Distinct(StringComparer.Ordinal).ToList(), failedCodes,
            progress, checkDayCoverage: true);
        ReportRetrySummary(done, progress);
        return result;
    }

    /// <summary>
    /// 【重新拉取失败】这一轮要按什么顺序调哪些任务（2026-09-13 二期）。
    ///
    /// 顺序是固定的，日志才可预期：便宜的整轮扫描排前面，K线那几项最重、排后面。
    /// 不在这张表里的任务排最后、按 id 字典序——新加一个任务忘了登记顺序也跑得起来，
    /// 只是排在末尾。
    ///
    /// 抽成静态方法是为了能单测：**分派对不对是这一版的核心**，而
    /// <see cref="RunRetryFailedInternalAsync"/> 一跑就要发几千个网络请求，测不了。
    /// </summary>
    public static List<string> DispatchOrder(RetryBacklog backlog)
    {
        var order = new[]
        {
            RetryTaskIds.Roster, RetryTaskIds.NetInflow,
            RetryTaskIds.IndexCons, RetryTaskIds.IndexWeight,
            RetryTaskIds.Shareholder, RetryTaskIds.Dividend,
            RetryTaskIds.StockDayBars, RetryTaskIds.StockHfqBars, RetryTaskIds.StockRawBars,
            RetryTaskIds.EtfBars, RetryTaskIds.IndexBars, RetryTaskIds.DelistedTails,
        };
        return backlog.Actionable.Select(i => i.TaskId).Distinct(StringComparer.Ordinal)
            .OrderBy(id => Array.IndexOf(order, id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 【只补待办】单项入口（<c>FetchMode.FillBacklog</c>）——把某一个任务欠的账补上。
    ///
    /// 跟这一项的日常入口（<c>RunStepXxxAsync</c>）的分界是**目标从哪来**：日常是按每只标的的
    /// 水位线往后续抓，这里是照着待办清单补。两者不能互相替代——历史空洞在水位线**之下**，
    /// 跑一整轮增量也补不上一段（见 <see cref="FillGapTodoAsync"/>）。
    ///
    /// 【重新拉取失败】就是拿它把有待办的任务挨个跑一遍；单独给某一项设成这个模式也行。
    /// </summary>
    public async Task<FetchResult> RunFillBacklogAsync(
        string taskId, NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法补待办，请先执行一次\"拉取全部\"");

        var (repo, errors, failed, _, sw) = BeginStep();
        var done = new List<string>();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        List<string> attempted;
        try
        {
            attempted = await RunFillBacklogAsync(taskId, source, repo, errors, failed, done, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }

        if (done.Count == 0)
        {
            progress?.Report($"【{TaskLabel(taskId)}】没有待补的项目。");
            return new FetchResult { NothingToDo = true };
        }
        progress?.Report("本轮补完：" + string.Join("、", done));
        return FinishFetchRun(errors, $"{TaskLabel(taskId)}·补待办",
            attempted.Distinct(StringComparer.Ordinal).ToList(), failed, progress, taskId: taskId);
    }

    /// <summary>
    /// 补一个任务的全部待办（2026-09-13 二期）——**"归谁补"这件事在这里落地**。
    ///
    /// 每一类待办的补法和复查方式都不一样，所以这里只做分派，具体怎么补、怎么复查、
    /// 什么时候从名单里划掉，都在各自的方法里（见 doc/retry-backlog-design.md §3.5）：
    /// 缺行看"行在不在"、值错要按 Reason 重查对应判据、失败名单是"这轮没失败就移出"。
    ///
    /// ⚠ 别把它接到任务的**日常入口**上（RunStepXxxAsync）：那些是按水位线跑的，
    /// 而历史空洞正好在水位线**之下**——那样跑不仅慢，而且一段也补不上
    /// （FillGapTodoAsync 存在的理由就是这个）。
    /// </summary>
    /// <returns>本轮真的去抓过的代码（收尾时用来更新失败名单）。</returns>
    private async Task<List<string>> RunFillBacklogAsync(
        string taskId, NamedBarSource source, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, List<string> done,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        // ── 这一项的待办由任务自己补？转交，立刻返回 ──
        // ⚠ 必须在 Load() **之前**判、并且直接 return：任务会自己写自己的那条待办，
        //   而这里往下走的话，收尾时保存的是转交之前读到的那份 manifest，
        //   会把任务刚写进去的名单覆盖掉。
        if (BacklogRunner?.Handles(taskId) == true)
        {
            var handed = await BacklogRunner.RunAsync(taskId, progress, ct);
            foreach (var e in handed.Errors) errors.Add(e);
            if (!handed.NothingToDo) done.Add($"{TaskLabel(taskId)}（任务自补）");
            return new List<string>();
        }

        var attempted = new List<string>();
        var manifest = _manifestStore.Load();

        List<string> CodesOf(string kind) =>
            manifest.Todo(taskId, kind)?.Targets.Select(t => t.Code).ToList() ?? new List<string>();

        // ── 逐只失败：各类用各自的补法 ──
        var failed = CodesOf(RetryTodoKind.Failed);
        if (failed.Count > 0)
        {
            switch (taskId)
            {
                // 【股东数据】的 case 删于 2026-09-18：它的待办由 ShareholderTask 自己补，
                // 上面 BacklogRunner 那一段已经转交走了，到不了这里。
                // 【分红送配】的 case 删于 2026-09-18：它的待办由 DividendTask 自己补，
                // 上面 BacklogRunner 那一段已经转交走了，到不了这里。
                default:
                    // K线：**按这个任务自己的口径**重抓（2026-09-13 二期修的就是这条）。
                    // 以前所有K线失败挤在一个 FailedCodes 里，重试一律走 Granularity.Day，
                    // 后复权/不复权任务失败的票走这条路补不回自己的口径，得绕一轮体检。
                    attempted.AddRange(await RefetchFailedBarsAsync(
                        taskId, failed, source, currentRepo, errors, failedCodes, done, progress, sw, ct));
                    break;
            }
        }

        // ── 当天日线还缺着（不是失败，是数据源当时还没出这些股票的当天数据）──
        var missingDay = manifest.Todo(taskId, RetryTodoKind.MissingDay);
        if (missingDay is { Targets.Count: > 0 } && missingDay.Day is { } missDate)
        {
            var codes = missingDay.Targets.Select(t => t.Code).ToList();
            var missStats = new FetchStats();
            int missDone = 0;
            var today = DateTime.Today;
            // **按任务决定补哪些口径**（2026-09-16 当日体检扩到 ETF/指数时加，09-17 补上 ETF 不复权）：
            // 名单是体检按标的类型 × 口径分开记的，补的时候也得分开——拿 ETF 去跑后复权
            // 是白发一轮请求，而数据源多半直接给空；反过来，ETF 的 day_raw 待办要是走成前复权，
            // 补了半天那条线还是缺的（而且复查会说"已补齐"）。
            //   · 个股：三个口径并成一份名单，三条线各按自己的水位线补，齐了的直接跳过；
            //   · ETF/指数（前复权）：只有 day 这一路；
            //   · ETF·不复权：只有 day_raw 这一路。
            bool qfq = taskId is not RetryTaskIds.EtfRawBars;
            bool hfq = taskId is RetryTaskIds.StockDayBars or RetryTaskIds.StockHfqBars
                               or RetryTaskIds.StockRawBars;
            bool raw = hfq || taskId is RetryTaskIds.EtfRawBars;
            string missLabel = TaskLabel(taskId);
            progress?.Report($"补 {missDate:yyyy-MM-dd} 还缺的{missLabel}，共 {codes.Count} 只"
                           + $"（上一轮不是失败，是数据源当时还没出这些标的的当天数据），数据源：{source.Name}");
            // 窗口取"缺的那个交易日 → 今天"。前复权走 ProcessOneStockAsync，后复权/不复权走
            // FetchHfqBarsAsync（它自己按各口径的水位线算缺口）——已经补齐的那条线会在水位线
            // 判定里跳过、不发请求。
            if (qfq)
                await Task.WhenAll(codes.Select(code =>
                    ProcessOneStockAsync(code, source, missDate, today, currentRepo,
                        errors, failedCodes, missStats, progress, codes.Count,
                        () => Interlocked.Increment(ref missDone), sw, ct)));
            if (hfq)
                await FetchHfqBarsAsync(source, codes,
                    code => HfqWatermarkWindow(currentRepo, code, today, DefaultLookbackYears),
                    currentRepo, errors, failedCodes, missStats, progress, sw, ct);
            if (raw)
                await FetchHfqBarsAsync(source, codes,
                    code => HfqWatermarkWindow(currentRepo, code, today, DefaultLookbackYears, Granularity.DayRaw),
                    currentRepo, errors, failedCodes, missStats, progress, sw, ct, gran: Granularity.DayRaw);
            progress?.Report($"当天日线重补汇总：{missStats.Summarize()}");
            done.Add($"{missDate:MM-dd}{missLabel} {codes.Count} 只");
            attempted.AddRange(codes);
        }

        // ── 全库体检查出来的：历史空洞 / 值问题（各自的复查方式，见方法注释）──
        await FillGapTodoAsync(taskId, source, currentRepo, errors, failedCodes, done, progress, sw, ct);
        await FillValueTodoAsync(taskId, source, currentRepo, errors, failedCodes, done, progress, sw, ct);

        // 【整天缺失】和【残缺日】两类的分支删于 2026-09-18：它们只属于资金净流入和那几个
        // 日频项，而那些项现在都自己补待办（HandlesBacklog=true），分派在转交那一步就走掉了。
        // 资金净流入把两类合并成一轮全市场抓（见 NetInflowTask 的类注释：一次请求返回整只票
        // 全历史，补几天跟补一天一样贵），复查仍分开——整天缺失看"有没有行"，
        // 残缺日走 PartialDayRepair.RunBatchAsync（体检同判据）。

        return attempted;
    }

    /// <summary>
    /// 重抓某个K线任务失败的那些票，**用它自己的口径**。
    ///
    /// 前复权走 <see cref="ProcessOneStockAsync"/>（并发，水位线没有时用默认回看年数兜底）；
    /// 后复权/不复权走 <see cref="FetchHfqBarsAsync"/>——它按那个口径自己的水位线算缺口，
    /// 已经齐的票一个请求都不发。
    /// </summary>
    private async Task<List<string>> RefetchFailedBarsAsync(
        string taskId, List<string> codes, NamedBarSource source, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, List<string> done,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        var gran = taskId switch
        {
            RetryTaskIds.StockHfqBars => Granularity.DayHfq,
            RetryTaskIds.StockRawBars => Granularity.DayRaw,
            _ => Granularity.Day,
        };
        string label = TaskLabel(taskId);
        var today = DateTime.Today;
        var stats = new FetchStats();

        if (gran != Granularity.Day && !source.Fetcher.SupportsHfq)
        {
            progress?.Report($"（{label} {codes.Count} 只先留着：数据源 {source.Name} 不提供这个口径，"
                           + "要补请把数据源切到 Tencent 再跑一次）");
            done.Add($"{label} {codes.Count} 只跳过（数据源不支持）");
            return new List<string>();
        }

        progress?.Report($"重新拉取上次失败的{label}K线，共 {codes.Count} 只，数据源：{source.Name}");

        if (gran == Granularity.Day)
        {
            int completed = 0;
            await Task.WhenAll(codes.Select(code =>
            {
                // 失败的票水位线可能很旧（一直失败），也可能压根没有（第一次就失败）——
                // 后一种用跟【拉取全部】默认一样的回看年数兜底，这里没有单独的输入框。
                DateTime start;
                lock (_dbLock)
                    start = IncrementalStart(currentRepo.GetLatestBarInfo(code, Granularity.Day), today, 3);
                return ProcessOneStockAsync(code, source, start, today, currentRepo, errors, failedCodes,
                    stats, progress, codes.Count, () => Interlocked.Increment(ref completed), sw, ct);
            }));
        }
        else
        {
            await FetchHfqBarsAsync(source, codes,
                code => HfqWatermarkWindow(currentRepo, code, today, DefaultLookbackYears, gran),
                currentRepo, errors, failedCodes, stats, progress, sw, ct, gran: gran);
        }

        progress?.Report($"{label}K线本轮汇总：{stats.Summarize()}");
        done.Add($"{label}K线 {codes.Count} 只");
        return codes;
    }

    /// <summary>
    /// 回填成交额/换手率（2026-07-13新增）——一次性修复历史数据：2026-07-10 TencentBarFetcher
    /// 改用 newfqkline 接口之前入库的日线，amount/turnover 全是0，而日线是 INSERT OR IGNORE +
    /// 每股水位线只往前抓新日期，正常抓取永远不会回头补这些旧行。这个模式按代码找出还有
    /// amount=0 日线的区间，重新抓那一段，然后只 UPDATE amount/turnover 两列（不动OHLC——重抓
    /// 的前复权价可能因为其间的分红除权跟当年入库的基准不一致，见
    /// SqliteBarRepository.UpdateDayAmountTurnover），最后重算该代码的周/月线聚合。
    ///
    /// 幂等、可中断重跑：已补上的行不再匹配 amount=0，下次运行自然跳过；失败/没抓到的代码留在
    /// 缺失名单里，再点一次就是精确重试（所以不占用 Manifest 的失败名单）。整轮工作量与一次
    /// 全量回补相当（每只股票1~2个分页请求），预计1小时上下。数据源建议用Tencent（链内新浪
    /// 回退拿不到成交额的行会被跳过留给下次）；选"Sina"跑这个没有意义，开头会给出警告。
    /// </summary>
    public async Task<FetchResult> RunBackfillAmountTurnoverAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        try
        {
            return await RunBackfillAmountTurnoverInternalAsync(source, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
        }
    }

    private async Task<FetchResult> RunBackfillAmountTurnoverInternalAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无需回填，请先执行一次\"拉取全部\"");

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();

        progress?.Report("正在统计本地日线里成交额缺失（amount=0）的代码和区间...");
        List<(string Code, DateTime Min, DateTime Max, int Count)> targets;
        lock (_dbLock)
        {
            targets = currentRepo.GetDayCodesWithMissingAmount();
        }
        if (targets.Count == 0)
        {
            progress?.Report("本地日线的成交额都已经有值，不需要回填");
            return new FetchResult();
        }

        var sw = Stopwatch.StartNew();
        progress?.Report($"共 {targets.Count} 只代码、{targets.Sum(t => (long)t.Count)} 行日线缺成交额，开始回填，数据源：{source.Name}");
        if (source.Name == "Sina")
            progress?.Report("警告：新浪的K线接口不返回成交额/换手率，用它回填不会有任何效果——请切换到 Tencent 再运行");

        var errors = new ConcurrentBag<string>();
        long updatedRows = 0;
        int failed = 0, completed = 0;
        var tasks = targets.Select(async t =>
        {
            ct.ThrowIfCancellationRequested();
            List<Bar> bars;
            try
            {
                (_, bars) = await source.Fetcher.FetchAsync(t.Code, Granularity.Day, t.Min, t.Max, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // 用户点了"停止"
            }
            catch (Exception ex)
            {
                errors.Add($"{t.Code}: [{source.Name}] {ex.Message}");
                Interlocked.Increment(ref failed);
                ReportBackfillProgress(Interlocked.Increment(ref completed), targets.Count, progress, sw);
                return;
            }

            lock (_dbLock)
            {
                var updated = currentRepo.UpdateDayAmountTurnover(bars);
                if (updated > 0) Interlocked.Add(ref updatedRows, updated);
                // 2026-09-10 起不再重算周/月线：它们不落库了，读的时候由
                // SqliteBarRepository.Query 从日线现场聚合，所以日线一改，周月线自动就是新的。
            }
            ReportBackfillProgress(Interlocked.Increment(ref completed), targets.Count, progress, sw);
        });
        await Task.WhenAll(tasks);

        progress?.Report($"回填汇总：处理 {targets.Count} 只代码，实际补上 {updatedRows} 行日线的成交额/换手率，失败 {failed} 只" +
                         (failed > 0 ? "（失败的不影响已完成的部分，再点一次\"回填\"只会重试还缺的）" : ""));

        var result = new FetchResult();
        result.Errors.AddRange(errors);
        return result;
    }

    private static void ReportBackfillProgress(int done, int totalCount, IProgress<string>? progress, Stopwatch sw)
    {
        if (done % 20 == 0 || done == totalCount)
            progress?.Report($"回填进度 ({done}/{totalCount})，已用时 {FormatElapsed(sw.Elapsed)}");
    }

    /// <summary>
    /// 大盘指数日K（2026-07-13新增，见 <see cref="MarketIndexCatalog"/>）——"拉取全部"和"拉取
    /// 当天"都会先跑这一步，给分析/回测提供大盘环境数据（指数MA20、两市成交额热度）。指数在
    /// Bar 表里用带前缀的8位符号（"sh000001"）存，水位线/收盘后确认/失败重试逻辑与个股完全一致
    /// （失败进 Manifest.FailedCodes，"重新拉取失败股票"会连指数一起重试——ProcessOneStockAsync
    /// 及各抓取器对带前缀符号原生支持）。总共就几个指数，串行跑完也只多花几秒。
    /// </summary>
    /// <summary>
    /// 个股日K（**前复权**）那一趟：按每只自己的水位线续抓到 <paramref name="end"/>，写库时顺带重算该股
    /// 周/月线（见 <see cref="ProcessOneStockAsync"/>），并把抓取中发现的复权基准漂移记进待重取名单。
    ///
    /// 2026-09-02 从 <see cref="RunFetchAllInternalAsync"/> 里原样抽出来，好让"只跑这一步"的单项入口
    /// （<see cref="RunStepStockDayBarsAsync"/>，计划页拆分用）跟【拉取全部】共用同一段逻辑——
    /// 抽的时候没有改任何判断，只是把内联 lambda 挪进了方法。
    /// </summary>
    private async Task FetchStockDayBarsAsync(
        NamedBarSource source, IReadOnlyList<string> codes, DateTime end, int lookbackYears,
        SqliteBarRepository currentRepo, ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes,
        FetchStats stats, IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        var driftedCodes = new ConcurrentBag<string>();

        // 先把每只的续抓起点算出来，分清"真要抓"和"本地已是最新"——跟后复权/不复权那条路
        // （FetchHfqBarsAsync）口径一致，日志才对得上。
        //
        // Resume point is per-stock, not a single global watermark — an interrupted run or a
        // stock that failed last time just gets its gap re-requested next time, since nothing
        // advanced its latest-date unless the fetch actually succeeded (see class remarks).
        // "今天"这一天是特例：本地已经有记录了，但如果是盘中抓的，还不能算数——要看抓取时间
        // 是不是已经过了收盘（IsConfirmedFinal），过了才跳过，没过就还要再抓一次去覆盖修正。
        var windows = new List<(string Code, DateTime Start)>(codes.Count);
        lock (_dbLock)
        {
            foreach (var code in codes)
            {
                var start = IncrementalStart(currentRepo.GetLatestBarInfo(code, Granularity.Day), end, lookbackYears);
                windows.Add((code, start));
            }
        }

        var toFetch = windows.Where(w => w.Start.Date <= end.Date).ToList();
        int upToDate = windows.Count - toFetch.Count;
        for (int i = 0; i < upToDate; i++) stats.Skip();

        if (toFetch.Count == 0)
        {
            progress?.Report($"个股日K·前复权：{codes.Count} 只本地都已是最新，这一轮无需抓取（一个请求都没发）。");
            return;
        }
        progress?.Report($"个股日K·前复权：{codes.Count} 只里有 {toFetch.Count} 只要抓、"
                       + $"{upToDate} 只本地已是最新（按各自水位线跳过，不发请求）...");

        int completed = 0;
        var tasks = toFetch.Select(w =>
            ProcessOneStockAsync(w.Code, source, w.Start, end, currentRepo, errors, failedCodes, stats, progress,
                toFetch.Count, () => Interlocked.Increment(ref completed), sw, ct,
                Granularity.Day, overwrite: false, driftedCodes: driftedCodes));
        await Task.WhenAll(tasks);

        // 复权基准漂移：抓取时顺带发现的（分红/送转导致数据源基准变了）。这里只**记名单**，
        // 真正的重取交给计划里的【重取前复权】在空闲时慢慢做，见 RunRepairQfqAsync。
        RecordDriftedForRepair(driftedCodes.ToList(), currentRepo, progress);
    }

    /// <summary>
    /// 个股日K（前复权）**只抓指定的那一天**——【补指定历史日】那一路，跟水位线无关。
    /// 2026-09-02 从 <see cref="RunFetchDayInternalAsync"/> 里原样抽出来（判断一行没改），
    /// 好让"个股日K·前复权 + 只抓某一天"这个模式跟老入口共用同一段逻辑。
    /// </summary>
    private async Task FetchStockDayBarsForDayAsync(
        NamedBarSource source, IReadOnlyList<string> codes, DateTime day,
        SqliteBarRepository currentRepo, ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes,
        FetchStats stats, IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        var driftedCodes = new ConcurrentBag<string>();
        int completed = 0;
        var tasks = codes.Select(code =>
        {
            // "补指定历史日"请求的是某个具体日期（往往就是今天）——跟"拉取全部"不一样，这里没有一个
            // "水位线"概念可用（本来就是"不管之前抓到哪天了，就抓这一天"），所以直接查这个具体日期
            // 本地是否已经有记录、以及是不是收盘后确认的。past（非今天）的日期一旦有记录就必然是
            // 最终的（过去的交易日不会再变），IsConfirmedFinal对任何早于今天的date天然成立。
            DateTime start;
            lock (_dbLock)
            {
                var latest = currentRepo.GetLatestBarInfo(code, Granularity.Day);
                if (latest == null)
                {
                    // 本地完全没有这只股票的K线（多半是刚才扫市值顺带发现的新股）——不只抓 day 这
                    // 一天，而是抓一个较长的回看窗口（跟"拉取全部"首次抓一只新股一致）。数据源本来
                    // 也只会返回上市日之后的数据，请求长窗口实际只会拿到"上市→day"的完整历史，不会
                    // 有多余，这样无论隔了几天才发现它，都能一次抓齐它到目前为止的全部K线，不会只
                    // 剩孤零零一天（2026-07-10新增）。
                    start = day.AddYears(-DefaultLookbackYears);
                }
                else
                {
                    var existing = currentRepo.Query(code, Granularity.Day, day, day).FirstOrDefault();
                    start = (existing != null && IsConfirmedFinal(existing.FetchedAt, day)) ? day.AddDays(1) : day;
                }
            }
            return ProcessOneStockAsync(code, source, start, day, currentRepo, errors, failedCodes, stats, progress, codes.Count, () => Interlocked.Increment(ref completed), sw, ct,
                Granularity.Day, overwrite: false, driftedCodes: driftedCodes);
        });
        await Task.WhenAll(tasks);

        // 复权基准漂移：只记名单、不当场重抓，见 RunRepairQfqAsync。
        RecordDriftedForRepair(driftedCodes.ToList(), currentRepo, progress);
    }

    private async Task FetchIndexBarsAsync(
        NamedBarSource source, DateTime end, int lookbackYears, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct, bool fullBackfill = false)
    {
        progress?.Report(fullBackfill
            ? $"正在**整段回补**大盘指数K线（{MarketIndexCatalog.All.Count} 个，从 {AShareMarketOpen:yyyy-MM-dd} 起，不看水位线）..."
            : $"正在抓取大盘指数K线（{MarketIndexCatalog.All.Count} 个：{string.Join("、", MarketIndexCatalog.All.Select(i => i.Name))}）...");
        // 指数名称写进 StockMeta（type=index）——让"查询"页能按名称/代码搜到指数（不影响个股选股，选股扫的是6位纯数字）。
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, MarketIndexCatalog.All.Select(i => (i.Symbol, i.Name)), SqliteStockMetaUpsert.TypeIndex);
        int completed = 0;
        foreach (var (symbol, _) in MarketIndexCatalog.All)
        {
            DateTime start;
            if (fullBackfill)
            {
                // 整段回补：忽略水位线、也忽略回看年数，直接从开市首日要起。
                //
                // 为什么忽略回看年数：这个模式存在的意义就是"把这条指数的历史一次补到底"，
                // 还要人先去把那个格子改成 25、跑完再改回 3，正是它要消灭的麻烦。
                // 数据源只会返回该指数实际存在的日期，早于发布日的部分自然是空
                // （创业板综 2010 才有、北证50 2021 才有），不会写进任何垃圾。
                //
                // 重复抓的代价可以忽略：入库走 InsertOrRefreshUnconfirmed，已确认的行原样跳过，
                // 所以**不会覆盖已有历史、也不会动复权基准**；多花的只是翻页请求
                // （每条指数按 640 行/页算十几页，九条合计一两分钟）。
                start = AShareMarketOpen;
            }
            else
            {
                // 跟"拉取全部"的个股水位线同一套规则：没抓过的从回看窗口起点开始（数据源只会返回
                // 指数实际存在的日期），抓过的从上次的下一天继续，"今天"要看是否已收盘后确认。
                lock (_dbLock)
                {
                    start = IncrementalStart(currentRepo.GetLatestBarInfo(symbol, Granularity.Day), end, lookbackYears);
                }
            }
            await ProcessOneStockAsync(symbol, source, start, end, currentRepo, errors, failedCodes, stats, progress, MarketIndexCatalog.All.Count, () => Interlocked.Increment(ref completed), sw, ct);
        }

        if (fullBackfill)
        {
            // 回补完把每条的实际覆盖报出来——这个模式多半是"加了新指数"之后跑的，
            // 人要的就是"它到底补到哪年了"这个答案，翻日志数行数太费劲。
            foreach (var (symbol, name) in MarketIndexCatalog.All)
            {
                DateTime? earliest;
                lock (_dbLock) earliest = currentRepo.GetEarliestPeriodStart(symbol, Granularity.Day);
                progress?.Report($"　{name}（{symbol}）：本地最早 {earliest:yyyy-MM-dd}");
            }
        }
    }

    /// <summary>
    /// Fetches one stock's bars for the given [start, end] range from the single given source (no
    /// failover — see the class remarks), then aggregates/writes to the local database (writes are
    /// serialized via <see cref="_dbLock"/> — SQLite only allows one writer at a time; network
    /// fetches still run concurrently across stocks, throttled by the source's own rate limiter).
    /// The caller decides what start/end means (per-stock watermark for 拉取全部, a fixed single
    /// day for 拉取当天) — this method doesn't care which.
    ///
    /// <paramref name="granularity"/> 只能是 <see cref="Granularity.Day"/>（前复权，界面展示用）或
    /// <see cref="Granularity.DayHfq"/>（后复权，回测用）。后复权那一路**不重算周/月线**——回测只用
    /// 日线，多存一份周月线纯属浪费空间和时间。两路各自独立记水位线（本方法只按传入的 granularity 读写），
    /// 所以 day_hfq 是空库时会被各入口自然补齐，不需要额外的一次性开关。
    /// </summary>
    /// <param name="overwrite">true=把抓回来的每一根都覆盖写入（"覆盖重抓"修复复权基准漂移用）；
    /// false=只补库里没有的，加上被判定为漂移的那些。</param>
    /// <param name="driftedCodes">非空时启用漂移检测：把请求窗口向前放宽 <see cref="DriftCheckLookbackDays"/>
    /// 天（不增加请求数，见该常量注释），拿回来的历史与库里逐根比对，发现基准漂移就覆盖这一段并把代码记进来，
    /// 由调用方在本轮末尾把它们记进待重取名单（<see cref="RecordDriftedForRepair"/>）。</param>
    private async Task ProcessOneStockAsync(
        string code, NamedBarSource source, DateTime start, DateTime end, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats, IProgress<string>? progress,
        int totalCount, Func<int> reportCompleted, Stopwatch sw, CancellationToken ct,
        string granularity = Granularity.Day, bool overwrite = false, ConcurrentBag<string>? driftedCodes = null,
        ConcurrentBag<(string Code, DateTime End)>? emptyRangeProbes = null)
    {
        ct.ThrowIfCancellationRequested();
        bool isHfq = granularity == Granularity.DayHfq;

        // This is the "is it already up to date locally" check the caller computed start/end
        // from — a stock whose watermark is already >= end never even reaches the network call,
        // which is what makes 拉取全部/拉取当天 safe to re-run without re-downloading everything.
        // See FetchStats.Summarize(), reported once at the end of the run, for visible proof of
        // how many stocks this run actually skipped vs fetched vs failed.
        if (start.Date > end.Date) { stats.Skip(); ReportCompareProgress(reportCompleted(), totalCount, progress, sw, stats); return; }

        // 日志只在真的要发请求时才写这一行（2026-07-10按用户要求改的——之前是按"处理满5只"汇总
        // 报一次，一行里经常混着"跳过的"和"真的发了请求的"，看不出具体是哪只被更新了）。这一行
        // 不节流、每次真发请求都打印一次——网络请求本身受限速器节流（并发+间隔），节奏已经够慢，
        // 不会刷屏。
        // 后复权那一路**不逐只打日志**：它跟前复权是同一批股票、同一时间跑，逐只打会让日志行数翻倍。
        // 而日志走 UI 线程（MainViewModel.Log 往 ObservableCollection 头部插入），几千行会把 UI 线程
        // 灌满，抓取完成后的续跑（写库）全排在这些日志后面，表现为"一直在抓、库里没数据"。
        // 后复权的进度由 ReportCompareProgress 的节流心跳（"正在对比数据 x/y"）体现，够用。
        if (!isHfq) progress?.Report($"正在抓取 {code}");

        // 确实要发请求了，才把窗口向前放宽以取得比对样本——数据源一页固定返回640根，放宽不多花请求
        var fetchStart = driftedCodes != null
            ? new[] { start, end.AddDays(-DriftCheckLookbackDays) }.Min()
            : start;

        List<Bar>? newDayBars = null;
        try
        {
            var (_, bars) = await source.Fetcher.FetchAsync(code, granularity, fetchStart, end, ct);
            newDayBars = bars;
        }
        catch (OperationCanceledException)
        {
            throw; // user clicked "停止" — propagate cleanly, not a per-stock error
        }
        catch (Exception ex)
        {
            errors.Add($"{code}: [{source.Name}] {ex.Message}");
            failedCodes.Add(code);
            stats.Fail();
            ReportCompareProgress(reportCompleted(), totalCount, progress, sw, stats);
            return;
        }

        if (newDayBars.Count > 0)
        {
            stats.FetchedWithNewData();
            lock (_dbLock)
            {
                // "今天"这一天单独走覆盖写入（可能是第二次抓到，用来把盘中抓的旧值换成收盘后的准确
                // 值）；更早的日期永远是第一次见到的新事实，走InsertOrIgnore（跟以前一样）。
                var todaysBars = newDayBars.Where(b => b.PeriodStart.Date == DateTime.Today).ToList();
                var olderBars = newDayBars.Where(b => b.PeriodStart.Date != DateTime.Today).ToList();

                if (overwrite)
                {
                    // "覆盖重抓"模式：整段以数据源当前基准为准，全部覆盖——用来一次性抹平历史上
                    // 分批入库造成的复权基准接缝（见 doc/factorlab-design.md 的复权基准说明）。
                    if (olderBars.Count > 0) SqliteBarUpsert.Upsert(_paths.CurrentDb, olderBars);
                }
                else if (olderBars.Count > 0)
                {
                    // 常规模式：库里没有的才插；已有的逐根比对，值对不上说明数据源那边的复权基准
                    // 变了（该股分红或送转了），把这一段覆盖成新基准，并记下这只股票待做全历史修正。
                    var existing = currentRepo
                        .Query(code, granularity, olderBars[0].PeriodStart, olderBars[^1].PeriodStart)
                        .ToDictionary(b => b.PeriodStart.Date, b => b.Close);

                    var toInsert = new List<Bar>();
                    var toOverwrite = new List<Bar>();
                    foreach (var b in olderBars)
                    {
                        if (!existing.TryGetValue(b.PeriodStart.Date, out var storedClose)) toInsert.Add(b);
                        else if (driftedCodes != null && IsDrifted(storedClose, b.Close)) toOverwrite.Add(b);
                    }
                    if (toInsert.Count > 0) currentRepo.InsertOrRefreshUnconfirmed(toInsert);
                    if (toOverwrite.Count > 0)
                    {
                        SqliteBarUpsert.Upsert(_paths.CurrentDb, toOverwrite);
                        driftedCodes!.Add(code);
                    }
                }

                if (todaysBars.Count > 0) SqliteBarUpsert.Upsert(_paths.CurrentDb, todaysBars);

                // ⚠ 这里原本要重算并写回周/月线（读全历史日线 → 聚合 → 两次 upsert）。
                // **2026-09-10 去掉了：周/月线不再落库**，读的时候由
                // SqliteBarRepository.Query 从日线现场聚合（一次遍历、纯 CPU、零额外 IO）。
                //
                // 它们本来就 100% 是日线算出来的、一个字节不是抓来的，却占了 Bar 表 20%
                // （week 414 万行 + month 100 万行），还带来两个真实代价：同一个数据错误要在
                // 六个口径上分别修（2026-09-10 的成交量单位事故就是这么放大的），以及
                // "日线更新了、周月线还没重算"这个永远存在的不一致窗口。
                // 顺带：每只票省下"读全历史 + 聚合 + 两次 upsert"，全市场 5500 只不是小数目。
            }
        }
        else
        {
            // Fetch succeeded but returned nothing — e.g. the requested range is entirely a
            // weekend/holiday with no trading. Not an error, not a local-DB skip either.
            stats.FetchedButEmpty();

            // 往前补历史时，这个"成功、但没有数据"是个**可以记住**的结论（这只票那些年还没上市），
            // 记下来下一轮就不用再试（见 BarProbeFloor 表与 ProbeFloorPlanner）。袋子只由
            // 【拉取区间数据】传进来——日常增量抓的是"到今天为止"，周末跑同样会走到这里，
            // 那种空绝不能当成"数据源没有"（ProbeFloorPlanner 里第 2 道前提也会再挡一次）。
            emptyRangeProbes?.Add((code, end));
        }

        ReportCompareProgress(reportCompleted(), totalCount, progress, sw, stats);
    }

    /// <summary>"正在对比数据"这一条只是给用户看整体进度用的粗粒度心跳（跳过的/真的发了请求的
    /// 都算在内），跟"正在抓取 {code}"那条不是一回事——那条才是"这只股票确实发了网络请求"的
    /// 精确记录，见 ProcessOneStockAsync。报告间隔沿用之前的"每5只报一次"（不是每50），这样日志
    /// 能持续往前走、看得出运行中还活着——单只股票在限速器的重试/熔断下最长可能要~48秒（见
    /// RateLimiter），中间隔久一点是正常的，不是卡住。</summary>
    private static void ReportCompareProgress(int done, int totalCount, IProgress<string>? progress, Stopwatch sw,
        FetchStats? stats = null)
    {
        if (done % 5 != 0 && done != totalCount) return;

        // 带上累计"抓到新数据/返空"：2026-09-07 用户盯着 (3950/5558) 看了一小时，判断不出这一轮
        // 其实一条都没写进库（当时只能靠数据库文件的 mtime 才看出来）。计数是现成的，报出来就是了。
        var counts = stats == null ? "" : $"，已抓到新数据 {stats.WithNewData} 只、返空 {stats.Empty} 只"
                                        + (stats.Failed > 0 ? $"、失败 {stats.Failed} 只" : "");
        progress?.Report($"正在对比数据 ({done}/{totalCount}{counts})，已用时 {FormatElapsed(sw.Elapsed)}");
    }

    // 【流通市值】FetchMarketCapAsync 和 ResolveMarketCapAsOfDateAsync 删于 2026-09-18：
    // 整项迁去了 StockPlatform.Tasks/RosterMarketCapTask。三条规矩跟着搬了过去——
    // "失败粒度是一轮"（round 待办）、"扫描顺带发现新股"、"as_of_date 记的是值属于哪个交易日"，
    // 最后一条还从"每轮抓一次上证指数"改成了"先问本地交易日历"。见 doc/index-roster-task-design.md。

    /// 中标/订单公告——原来是界面上一个独立的"抓取中标/订单公告"按钮，需要用户自己填起止日期手动
    /// 触发；现在整合进"拉取全部"和"拉取当天"里自动跑，不再单独触发。日期窗口由调用方决定："拉取
    /// 全部"用固定回看窗口（<see cref="AnnouncementLookbackDaysForFetchAll"/>天到今天，公告没有像
    /// K线那样按股票记录的水位线，重复扫描同一窗口靠 OrderWinAnnouncement 的主键去重是安全的，
    /// 所以不需要真正的增量逻辑），"拉取当天"就只查那一天。关键词是用户在界面上填的、跨两种拉取
    /// 方式共用的设置，不是这里决定的。失败按非致命处理，不影响K线抓取。</summary>
    private async Task FetchAnnouncementsAsync(
        IReadOnlyList<string> keywords, DateOnly start, DateOnly end, IProgress<string>? progress, CancellationToken ct)
    {
        if (keywords.Count == 0) return; // 用户清空了关键词框，视为不抓公告

        var slices = CalendarYearSlicer.Split(start, end);
        if (slices.Count > 1)
            progress?.Report($"中标/订单公告：{start:yyyy-MM-dd}~{end:yyyy-MM-dd} 跨 {slices.Count} 个自然年，"
                             + "按年切片分别搜索（见 CalendarYearSlicer：不切会被搜索源的翻页上限静默截断）。");

        foreach (var (sliceStart, sliceEnd) in slices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _announcementOrchestrator.RunAsync(keywords, sliceStart, sliceEnd, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // 用户点了"停止"
            }
            catch (Exception ex)
            {
                // 单片失败不该带倒后面的年份——公告本来就是非致命的旁路数据
                progress?.Report($"获取中标/订单公告失败（{sliceStart:yyyy}年这一片，不影响K线抓取）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// Shared tail for all three fetch modes——只更新 manifest 的 LastFetchAt/LastFetchKind/
    /// FailedCodes，不再产出任何文件（2026-07-09移除master/daily文件生产，见下方"状态变更记录"）。
    /// </summary>
    private FetchResult FinishFetchRun(
        ConcurrentBag<string> errors, string fetchKind,
        IReadOnlyCollection<string> attemptedCodes, ConcurrentBag<string> failedCodesThisRun,
        IProgress<string>? progress = null, bool checkDayCoverage = false,
        string? taskId = null)
    {
        var manifest = _manifestStore.Load();
        manifest.LastFetchAt = DateTime.Now;
        manifest.LastFetchKind = fetchKind;
        // 失败名单记到**是哪个任务失败的**那一格里（2026-09-13 二期）。
        //
        // 以前这里是 manifest.FailedCodes 一个大池子——同一个方法里，一行之隔，
        // LastRunByTask 明明是按任务分域记的，失败名单却不分。后果：后复权/不复权任务
        // 失败的票跟前复权混在一起，【重新拉取失败】只能一律按 Granularity.Day 重抓，
        // 那两个口径的缺口这条路补不回来（得绕一轮全库体检才补得上）。
        //
        // taskId 不传＝按前复权算，跟改之前等价：那些调用点要么本来就是前复权
        //（【拉取全部】【拉取N年】这类复合动作），要么根本不记失败名单（attemptedCodes 为空）。
        SetFailedTodo(manifest, taskId ?? RetryTaskIds.StockDayBars, attemptedCodes, failedCodesThisRun);
        // 按任务分域记一条（2026-09-02）：拆细之后 LastFetchKind 只剩"今天最后收尾的那一项"，
        // 看不出别的项跑没跑。这份字典让"数据状态"能逐项显示"上次什么时候跑的、干不干净"。
        manifest.LastRunByTask[fetchKind] = new TaskRunRecord
        {
            At = DateTime.Now,
            ErrorCount = errors.Count,
        };
        _manifestStore.Save(manifest);

        // 体检要在上面存完 manifest 之后跑——它自己会再读一次 manifest 写入缺失名单。
        if (checkDayCoverage) CheckLatestDayCoverage(progress);

        var result = new FetchResult();
        result.Errors.AddRange(errors);
        return result;
    }

    /// <summary>
    /// 当日完整性体检——判据和编排都在 <see cref="SqliteDayCompletenessAuditor"/>（2026-09-17 抽走），
    /// 这里只剩"跑一轮、写 manifest、把结论报出来"。
    ///
    /// ⚠ 计划里的【当日完整性体检】那一项走的**不是**这个方法，而是新框架的
    /// <c>DayCompletenessTask</c>。留着它是因为还有一个调用方：【重新拉取失败】收尾时
    /// （<see cref="FinishFetchRun"/> 的 <c>checkDayCoverage</c>）——刚补过一轮，名单必须重算。
    /// 两边共用同一个 auditor，所以不会出现"体检说齐了、重试那边还挂着单子"的分叉。
    /// </summary>
    /// <returns>(查的是哪个交易日, 有几处不齐)。第二个数是**处数**不是只数——0 就是当天全齐。</returns>
    public (DateTime? TradingDay, int Problems) CheckLatestDayCoverage(IProgress<string>? progress = null)
    {
        if (!File.Exists(_paths.CurrentDb)) return (null, 0);

        var auditor = new SqliteDayCompletenessAuditor(_paths.CurrentDb);
        var found = new List<DayFinding>();
        DateTime? latest;
        lock (_dbLock)
        {
            foreach (var batch in auditor.RunSegments(out latest)) found.AddRange(batch);
        }
        if (latest == null)
        {
            progress?.Report("（跳过当日完整性体检：本地上证指数日线不足两根，没有交易日锚可用）");
            return (null, 0);
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            SqliteDayCompletenessAuditor.Apply(manifest, found);
            _manifestStore.Save(manifest);
        }

        foreach (var f in found.Where(f => f.Detail is { Length: > 0 }))
            progress?.Report($"⚠ {f.Detail}");

        int problems = found.Count(f => f.IsBad);
        progress?.Report(problems == 0
            ? $"当日完整性体检：{latest:yyyy-MM-dd} 全齐 —— {string.Join("；", found.Select(f => f.Summary))}"
            : $"当日完整性体检：{latest:yyyy-MM-dd} 有 {problems} 处不齐 —— "
              + string.Join("；", found.Select(f => f.Summary)));
        return (latest, problems);
    }

    /// <summary>
    /// Read-only snapshot for the Fetcher UI's "数据状态" line (see doc/data-platform-design.md) —
    /// lets the user see, without opening a database browser, what date range is already covered
    /// locally and when a fetch last actually ran, so they know what's left to pull instead of
    /// guessing/re-pulling something already up to date.
    /// </summary>
    public DataStatus GetDataStatus()
    {
        var manifest = _manifestStore.Load();
        var recent = manifest.LastRunByTask
            .Select(kv => new TaskRunLine(kv.Key, kv.Value.At, kv.Value.ErrorCount))
            .OrderByDescending(r => r.At)
            .ToList();
        if (!File.Exists(_paths.CurrentDb))
            return new DataStatus
            {
                LastFetchAt = manifest.LastFetchAt,
                LastFetchKind = manifest.LastFetchKind,
                RecentTaskRuns = recent,
            };

        // 一次扫描同时拿最早+最晚（2026-08-04）——以前分两次调，等于把 7GB 的主键覆盖索引扫两遍，
        // 实测 5.2 秒 vs 合成后 3.1 秒。这个方法整体仍然慢（冷启动几十秒），所以调用方
        // （Fetcher 的 MainViewModel.RefreshDataStatus）已改成在后台线程跑、不挡窗口显示。
        var repo = new SqliteBarRepository(_paths.CurrentDb);
        var (earliest, latest) = repo.GetOverallPeriodStartRange(Granularity.Day);
        return new DataStatus
        {
            EarliestDay = earliest,
            LatestDay = latest,
            LastFetchAt = manifest.LastFetchAt,
            LastFetchKind = manifest.LastFetchKind,
            RecentTaskRuns = recent,
        };
    }

    /// <summary>【重新拉取失败】的待办清单（2026-09-13 由 <c>GetFailedRetrySummary</c> 改过来）。
    ///
    /// 为什么不能只给一个总数：**流通市值是整轮扫描**，一次请求拿全市场，接口失败时会保守地把
    /// 这批代码全部记进名单（见 <see cref="FetchMarketCapAsync"/> 的 catch）。于是"1次接口失败"
    /// 在总数里表现成"5544 支失败"，按钮上写"重新拉取失败股票（5547）"会被读成丢了5547只票的
    /// 数据，实际上只是一次市值快照没取到、外加3只资金流。所以市值单独按"轮"表达。
    ///
    /// 为什么换成 <see cref="RetryBacklog"/>：原来那份清单在代码里手抄了三遍，
    /// 体检写进来的两类待办漏抄了整整两周，界面上显示"1 只"而实际要跑 1909 段。
    /// 详见 RetryBacklog 的类注释和 doc/retry-backlog-design.md。</summary>
    public RetryBacklog GetRetryBacklog() => RetryBacklog.From(_manifestStore.Load());

    /// <summary>
    /// 本轮尝试过（无论最终成功/失败/跳过）的代码，凡是这次没有失败的一律移出失败名单——覆盖
    /// "之前失败、这次成功了"和"之前没失败、这次失败了"两种情况；本轮真正失败的加回/保留在名单
    /// 里。没被本轮碰到的代码（比如已经不在最新股票列表里的）保持原样不动。K线/市值/资金净流入
    /// 三份名单（Manifest.FailedCodes/FailedMarketCapCodes/FailedNetInflowCodes）共用这同一套
    /// 计算逻辑，各自独立维护自己的名单。
    /// </summary>
    private static List<string> ComputeUpdatedFailedCodes(List<string> currentFailed, IReadOnlyCollection<string> attemptedCodes, IReadOnlyCollection<string> failedCodesThisRun)
    {
        var stillFailed = new HashSet<string>(currentFailed);
        stillFailed.ExceptWith(attemptedCodes);
        stillFailed.UnionWith(failedCodesThisRun);
        return stillFailed.OrderBy(c => c).ToList();
    }

    /// <summary>
    /// 把一份失败名单写进统一待办（2026-09-13 二期）——七类失败名单共用这一套，
    /// 判据仍是上面那个 <see cref="ComputeUpdatedFailedCodes"/>，只是存的地方从
    /// 九个各自为政的字段换成了 <see cref="Manifest.Todos"/> 里按 (TaskId, Kind) 索引的一条。
    ///
    /// **taskId 就是那件事该归谁补**：以前所有K线失败挤在一个 FailedCodes 里，
    /// 重试时只能一律按前复权重抓，后复权/不复权任务失败的票走这条路补不回自己的口径。
    /// </summary>
    private static void SetFailedTodo(Manifest m, string taskId,
        IReadOnlyCollection<string> attemptedCodes, IReadOnlyCollection<string> failedThisRun)
    {
        var current = m.Todo(taskId, RetryTodoKind.Failed)?.Targets.Select(t => t.Code).ToList() ?? new List<string>();
        var updated = ComputeUpdatedFailedCodes(current, attemptedCodes, failedThisRun);
        m.SetTodo(taskId, RetryTodoKind.Failed, updated.Select(c => new RetryTarget { Code = c }).ToList());
    }

    /// <summary>
    /// "拉取指数成分/权重"（2026-07-16新增）——遍历内置指数全集(<see cref="IndexCatalog"/>，732个)：先向
    /// 新浪拉每个指数的成分名单(IndexCons)，再向中证官网拉成分权重(IndexWeight，只有中证系有、非中证系
    /// 404 跳过)，最后按 ETF 名称匹配指数生成 EtfIndexMap（供"股票→指数→ETF"反查）。成分/权重各自逐指数
    /// 记录失败(<see cref="Manifest.FailedIndexConsCodes"/>/<see cref="Manifest.FailedIndexWeightCodes"/>)，
    /// 可用"重新拉取失败股票"重试。独立按钮，不掺进主抓取流程（成分是季度级慢变数据，不必跟每天K线跑）。
    /// </summary>
    public async Task<FetchResult> RunFetchIndexConsAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        void Forward(string s) => progress?.Report(s);
        _indexConsProvider.OnStatus += Forward;
        _indexWeightProvider.OnStatus += Forward;
        try
        {
            return await RunFetchIndexConsInternalAsync(progress, ct);
        }
        finally
        {
            _indexConsProvider.OnStatus -= Forward;
            _indexWeightProvider.OnStatus -= Forward;
        }
    }

    private async Task<FetchResult> RunFetchIndexConsInternalAsync(IProgress<string>? progress, CancellationToken ct)
    {
        _indexRepository.EnsureSchema();
        var indexes = IndexCatalog.All;
        if (indexes.Count == 0)
            return new FetchResult { Errors = new List<string> { "内置指数清单为空（IndexCatalog.csv 未打包？），无法拉取指数成分" } };

        var errors = new List<string>();
        var consFailed = new List<string>();
        var weightFailed = new List<string>();
        var attempted = indexes.Select(i => i.Code).ToList();
        var now = DateTime.Now;
        int consOk = 0, consEmpty = 0, weightOk = 0, weightNone = 0, done = 0;

        var sw = Stopwatch.StartNew();
        progress?.Report($"开始拉取指数成分/权重，共 {indexes.Count} 个指数（成分走新浪、权重走中证，逐个抓，较慢）...");
        foreach (var (code, _) in indexes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var members = await _indexConsProvider.GetConsAsync(code, ct);
                if (members.Count > 0) { lock (_dbLock) _indexRepository.ReplaceCons(code, members, now); consOk++; }
                else consEmpty++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"指数 {code} 成分抓取失败：{ex.Message}"); consFailed.Add(code); }

            try
            {
                var weights = await _indexWeightProvider.GetWeightsAsync(code, ct);
                if (weights.Count > 0) { lock (_dbLock) _indexRepository.ReplaceWeights(code, weights); weightOk++; }
                else weightNone++;   // 非中证系指数没有权重文件（404），不算失败
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"指数 {code} 权重抓取失败：{ex.Message}"); weightFailed.Add(code); }

            if (++done % 20 == 0 || done == indexes.Count)
                progress?.Report($"指数成分/权重 {done}/{indexes.Count}（成分成功 {consOk}、权重成功 {weightOk}，已用时 {FormatElapsed(sw.Elapsed)}）");
        }

        // ETF→指数 名称匹配（尽力）——生成 EtfIndexMap，供"股票→指数→ETF"反查。
        var map = BuildEtfIndexMap(progress);
        lock (_dbLock) _indexRepository.ReplaceEtfIndexMap(map);

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            SetFailedTodo(manifest, RetryTaskIds.IndexCons, attempted, consFailed);
            SetFailedTodo(manifest, RetryTaskIds.IndexWeight, attempted, weightFailed);
            _manifestStore.Save(manifest);
        }

        progress?.Report($"指数成分/权重完成：成分 {consOk} 个指数有数据、{consEmpty} 个无成分；权重 {weightOk} 个指数(中证系)、{weightNone} 个无权重文件；" +
                         $"成分失败 {consFailed.Count}、权重失败 {weightFailed.Count}" +
                         ((consFailed.Count > 0 || weightFailed.Count > 0) ? "（失败的可点\"重新拉取失败股票\"重试）" : ""));
        return new FetchResult { Errors = errors };
    }

    /// <summary>
    /// 把本地已缓存的银行/券商/保险 PDF 全部用**当前**解析规则重跑一遍（不联网，几分钟）。
    /// 2026-09-02 从 <see cref="RunFetchBankRegulatoryAsync"/> 里原样抽出来——逻辑一行没改，
    /// 只是让它能被"只重解析、不下载"的单项入口（<see cref="RunStepReparseBankReportsAsync"/>）复用。
    ///
    /// 这一步是幂等自愈：解析规则改进后（各行版式差异会不断暴露新问题），已经下载过的报告
    /// 不需要重新下载就能用新规则重跑，旧的错值被 INSERT OR REPLACE 覆盖掉。
    /// 顺带处理"下错文件"：早期版本会把问询函回复当年报下下来（353 份里有 9 份），
    /// 这里删掉文件和状态记录，后面的下载流程会按修正后的标题规则重取。
    /// </summary>
    private void ReparseCachedBankReports(
        SqliteBankRegulatoryRepository repo,
        Dictionary<string, FinancialSnapshot> latest,
        IProgress<string>? progress, CancellationToken ct)
        => new BankReportReparser(repo, _paths.ReportsDir, _dbLock)
            .Run(latest, s => progress?.Report(s), ct);

    /// <summary>ETF→指数 名称匹配（尽力）——ETF 名称几乎都含指数名（"沪深300ETF华泰"→沪深300），用它在
    /// 指数清单里找。exact=名称完全等于某指数名，contains=互相包含，都找不到=unmatched（未匹配的绝大多数
    /// 是债券/货币/黄金ETF，本就没有A股成分）。长指数名优先，避免"中证500"被"中证50"抢先命中。</summary>
    private List<(string EtfCode, string? IndexCode, string MatchType)> BuildEtfIndexMap(IProgress<string>? progress)
    {
        var etfs = SqliteStockMetaUpsert.GetAllInstruments(_paths.CurrentDb)
            .Where(x => x.Type == SqliteStockMetaUpsert.TypeEtf).ToList();
        var idx = IndexCatalog.All.OrderByDescending(i => i.Name.Length).ToList();

        var result = new List<(string, string?, string)>();
        int matched = 0;
        foreach (var e in etfs)
        {
            var core = e.Name.Split("ETF")[0].Trim();   // "ETF"之前的部分作为指数名候选
            string? found = null;
            string matchType = "unmatched";
            if (core.Length >= 2)
            {
                var exact = idx.FirstOrDefault(i => i.Name == core);
                if (exact.Code != null) { found = exact.Code; matchType = "exact"; }
                else
                {
                    var contains = idx.FirstOrDefault(i => i.Name.Length >= 2 && (core.Contains(i.Name) || i.Name.Contains(core)));
                    if (contains.Code != null) { found = contains.Code; matchType = "contains"; }
                }
            }
            if (found != null) matched++;
            result.Add((e.Code, found, matchType));
        }
        progress?.Report($"ETF→指数名称匹配：{etfs.Count} 只 ETF，匹配到 {matched}、未匹配 {etfs.Count - matched}" +
                         "（未匹配多为债券/货币/黄金ETF，本就无A股成分）");
        return result;
    }

    // RetryIndexAsync（重试指数成分/权重失败）删于 2026-09-18：两项都迁成了新式任务，
    // 补失败名单跟日常抓取是同一个动作，在各自的任务里（FillBacklog 模式）。


    // 【拉取股东数据】2026-09-18 迁到新任务框架（StockPlatform.Tasks/ShareholderTask）：
    //   判据抽成 ShareholderFetchPlanner（按报告期 + 实际披露日，纯查库零请求），
    //   失败待办也由任务自己补，从 RunFillBacklogAsync 开头的 BacklogRunner 转交过去。
    //   迁的理由跟分红一样：老实现每轮全量重抓、取消时失败名单落不了盘，被限流打断就整轮白跑；
    //   顺带拆了 SinaShareholderProvider 注释里那颗雷（一只票两个请求 + 全市场 WhenAll
    //   ⇒ "所有票的第1个请求 → 所有票的第2个请求"，前半程零写入）。见 doc/shareholder-task-design.md。

    // 【融资余额】的 FetchMarginRecentAsync / RunFetchMarginAsync 删于 2026-09-18：
    // 整项迁去了 StockPlatform.Tasks/MarginTask（增量、只抓某一天、整段回补、只补待办
    // 四条路都在那儿，四道闸走共用的 DailyBackfillGate、空日名单走 DailyNoDataGate）。
    // 后者当时**已经没有任何调用方**，一并清掉——留着就是留个陷阱。
    // 【拉取历史区间】里的两融那半边还在本类（上面 BackfillDailyAsync 那一段），
    // 落库跟任务侧共用 MarginDayWriter。见 doc/margin-task-design.md。

    /// <summary>逐交易日补齐一类每日数据，只对**真正可能有数据**的日子发请求。<paramref name="fetchOne"/>
    /// 负责抓某天并写库、返回写入条数；异常记进 errors（非致命，继续下一天）。
    ///
    /// ════ 四道跳过闸（2026-09-08 从"只跳周末"扩充）════
    ///   ① 周末；
    ///   ② **交易日历里不是交易日**——节假日。日历见 TradingDay 表；日历覆盖不到的区间不敢判，
    ///      照旧逐日试（<see cref="TradingCalendar.CoversFrom"/>，2026-09-06 那次静默漏抓 2360 只
    ///      老股就是把"日历不知道"当成了"没有交易日"）；
    ///   ③ **确认没有数据的日子**——是交易日、但这个源上确实没有（新浪龙虎榜早年那几年）。
    ///      见 <see cref="IDailyFetchNoDataRepository"/>；
    ///   ④ 本地已有的日子——但**最近 5 个交易日除外**。这两类数据都是盘后陆续发布的
    ///      （龙虎榜当晚、融资余额 T+1），早抓到的可能只是一半，"有行就跳过"会把残缺状态永久固化。
    ///      重抓幂等（主键 + INSERT OR IGNORE），代价就是每轮多 5 个请求。
    ///
    /// ════ 什么时候写"确认没有"（三条缺一不可）════
    /// 正常返回的空（异常走 catch，绝不记——一次网络抽风换永久漏一天是这套机制唯一的致命失败模式）
    /// ＋ 日期在 3 天以前（近几天的空可能只是还没发布）＋ provider 自己保证不把空壳反爬页当成空
    /// （见 SinaLhbProvider 的骨架校验）。满足就**一次定案**，不用像K线那样等两轮：同一个源抓第二次
    /// 并不会带来新信息。
    ///
    /// <paramref name="dataset"/>＝空日名单里的数据集键（<see cref="IDailyFetchNoDataRepository.LhbDataset"/>
    /// 那些）；传 null 就是不参与空日记账（老调用点可以逐步接）。
    ///
    /// <paramref name="earliestAvailable"/>＝这份数据**最早存在**的那天（由 provider 声明，见
    /// <see cref="IMarginProvider.EarliestAvailable"/>）。起点会被抬到它——调用方给的起点来自"本地K线
    /// 最早那天"或界面上填的年份，那是**K线**的水位线，跟每日数据自己什么时候开始有毫无关系：融资融券
    /// 2010-03-31 才开市，从 1990-12-19 起跑就是对着 4700 多个必然为空的交易日一天发一次请求。
    /// 抬起点时日志会明说一句，免得日后有人以为是漏抓。</summary>
    private async Task BackfillDailyAsync(string label, string? dataset, DateOnly start, DateOnly end, HashSet<DateOnly> have,
        Func<DateOnly, Task<int>> fetchOne, ConcurrentBag<string> errors, IProgress<string>? progress, Stopwatch sw,
        DateOnly earliestAvailable, CancellationToken ct)
    {
        if (start < earliestAvailable)
        {
            progress?.Report($"{label}：起点 {start:yyyy-MM-dd} 上提到 {earliestAvailable:yyyy-MM-dd}" +
                             $"——该日之前这份数据源上根本不存在，不是漏抓。");
            start = earliestAvailable;
        }
        if (start > end)
        {
            progress?.Report($"{label}：区间 ~{end:yyyy-MM-dd} 整段早于数据起点 {earliestAvailable:yyyy-MM-dd}，无可补，跳过。");
            return;
        }

        var calendar = LoadTradingCalendar(progress);
        var confirmed = dataset != null && _dailyNoDataRepository != null
            ? _dailyNoDataRepository.GetConfirmed(dataset)
            : new HashSet<DateOnly>();
        // 最近 5 个交易日无条件重抓（闸④）。日历还没建时退化成"最近 7 个自然日"，宁可多抓几天
        var today = DateOnly.FromDateTime(DateTime.Today);
        var recent = calendar != null
            ? calendar.LastTradingDays(DateTime.Today, 5).Select(DateOnly.FromDateTime).ToHashSet()
            : Enumerable.Range(0, 7).Select(i => today.AddDays(-i)).ToHashSet();

        progress?.Report($"开始补齐{label}历史：{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}（跳过周末、非交易日、" +
                         $"本地已有和已确认没有的日子；最近 5 个交易日无条件重抓）" +
                         (confirmed.Count > 0 ? $"，已确认没有 {confirmed.Count} 天" : "") + "...");

        int done = 0, wrote = 0, skipped = 0, fail = 0, skipHoliday = 0, skipNoData = 0, newConfirmed = 0, healed = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            switch (DailyBackfillGate.Evaluate(d, calendar, confirmed, have, recent))
            {
                case DailySkipReason.Weekend: continue;
                case DailySkipReason.NotTradingDay: skipHoliday++; continue;
                case DailySkipReason.ConfirmedNoData: skipNoData++; continue;
                case DailySkipReason.AlreadyHave: skipped++; continue;
            }

            try
            {
                int n = await fetchOne(d);
                wrote += n;
                // 名单的增删判据走共用的纯函数（2026-09-18 抽出来）——这套判据原来在本类里
                // 写了两份，两融迁成新式任务时会出现第三份。⚠ cutoff 那条不能省，理由见它的注释。
                if (dataset != null && _dailyNoDataRepository != null)
                {
                    switch (DailyNoDataGate.Evaluate(n, d, today, confirmed.Contains(d)))
                    {
                        case NoDataAction.Revoke:
                            // 源后来补上了，撤销之前的结论（能走到这里说明这天没被闸③挡住，
                            // 也就是手动指定区间或彻底体检清过表）
                            _dailyNoDataRepository.Remove(dataset, d); confirmed.Remove(d); healed++;
                            break;
                        case NoDataAction.Confirm:
                            _dailyNoDataRepository.Confirm(dataset, d); confirmed.Add(d); newConfirmed++;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{label} {d:yyyy-MM-dd}：{ex.Message}"); fail++; }
            if (++done % 20 == 0)
                progress?.Report($"{label} 补齐中：已抓 {done} 天、写入 {wrote} 条、失败 {fail}（跳过已有 {skipped}、" +
                                 $"非交易日 {skipHoliday}、确认没有 {skipNoData}，已用时 {FormatElapsed(sw.Elapsed)}）");
        }
        progress?.Report($"{label}补齐完成：新抓 {done} 个交易日、写入 {wrote} 条、失败 {fail} 天；" +
                         $"跳过：本地已有 {skipped}、非交易日 {skipHoliday}、确认没有 {skipNoData}" +
                         (newConfirmed > 0 ? $"；本轮新确认 {newConfirmed} 天没有数据（往后不再重试）" : "") +
                         (healed > 0 ? $"；{healed} 天数据源后来补上了，已撤销结论" : "") +
                         (fail > 0 ? "（失败的可再点一次、只会补还缺的）" : ""));
    }

    /// <summary>
    /// 读交易日历（TradingDay 表）。没配仓储、表还空着，都返回 null——调用方据此退回"只跳周末"的老行为，
    /// **绝不能**把"没有日历"当成"这些天都不是交易日"。日历由【交易日历】那一项建，见 TradingCalendarTask。
    /// 一次运行内缓存，免得每个回补循环都读一遍。
    /// </summary>
    private TradingCalendar? LoadTradingCalendar(IProgress<string>? progress)
    {
        if (_tradingCalendarCache != null) return _tradingCalendarCache;
        if (_tradingDayRepository == null) return null;
        try
        {
            var days = _tradingDayRepository.GetAll();
            if (days.Count == 0)
            {
                progress?.Report("⚠ 本地交易日历还是空的，这一轮只能按\"跳过周末\"来（节假日会白发请求）——" +
                                 "跑一次【交易日历】就好了。");
                return null;
            }
            _tradingCalendarCache = new TradingCalendar(days);
            return _tradingCalendarCache;
        }
        catch (Exception ex)
        {
            progress?.Report($"⚠ 读交易日历失败（{ex.Message}），这一轮按\"跳过周末\"来。");
            return null;
        }
    }

    private TradingCalendar? _tradingCalendarCache;

    /// <summary>
    /// 【交易日历】那一行在参数格里显示的存量：日历覆盖到哪天、共多少天（2026-09-09）。
    ///
    /// 为什么要它：这一项跟别的"还差多少只"不一样，它没有待办清单，人想知道的是**覆盖到哪天**——
    /// 交易所年底才发布下一年的日历，日历只到今天附近就说明该再拉一次了，光看"✔ 完成"看不出来。
    ///
    /// null = 没配仓储（老配置）。三条 MIN/MAX/COUNT 都走 day 主键，几毫秒，可以跟着计数一起刷。
    /// **不走 <see cref="_tradingCalendarCache"/>**：那份缓存一次运行内不失效，而这里要的是刚抓完的现状。
    /// </summary>
    public (DateTime? Min, DateTime? Max, int Days)? GetTradingCalendarRange()
    {
        if (_tradingDayRepository == null) return null;
        var (min, max) = _tradingDayRepository.GetRange();
        return (min, max, min == null ? 0 : _tradingDayRepository.Count());
    }

    /// <summary>
    /// 拉取全市场证监会行业分类（2026-08-04新增）——见 <see cref="ExchangeSinaIndustryProvider"/>。
    /// 两级：门类（两所官网，覆盖沪深全部）+ 大类（新浪，粒度合适但约58%覆盖），消费端优先用大类、
    /// 缺失退回门类。用途：① 因子法名单显示"板块"；② FactorLab 的行业中性化——原先用板块表只有
    /// 44% 覆盖、其余全挤在一个"未知"组里，中性IC 一直不够准。
    /// 行业极少变动，属定期数据，季度跟财报一起跑一次即可；整体覆盖写入，反复跑无副作用。
    /// </summary>
    /// <summary>
    /// 抓指定股票列表的财务报表（2026-08-29 新增）。跟【拉取财务报表】那条主路径的区别：
    /// 那个（StockPlatform.Tasks.FinancialTask）按增量计划抓全市场、有每轮上限；
/// 这个直接抓给定的一小批，供【银行监管指标】做前置补数。两条路共用同一个 provider 和落库方法。
    ///
    /// 同样**必须顺序处理**，原因见 FinancialTask 类注释里那段关于信号量 FIFO 的坑。
    /// </summary>
    /// ⚠ 2026-09-15 从 private 放开：【金融监管指标】迁到新任务框架后，那个任务要拿它
    ///   做前置补数。**以委托的形式注进去**（见 App 里的注册），不让任务反向依赖编排层。
    public async Task FetchFinancialsForCodesAsync(
        IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        if (_financialProvider == null || codes.Count == 0) return;

        var repo = new SqliteFinancialRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        var sw = Stopwatch.StartNew();
        int done = 0, failed = 0;

        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rows = await _financialProvider.GetAllAsync(code, ct);
                if (rows.Count > 0) lock (_dbLock) { repo.ReplaceByCode(code, rows); }
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }   // 单只失败不影响整批，后面认不出它就是了

            if (++done % 20 == 0 || done == codes.Count)
                progress?.Report($"  补抓财务 {done}/{codes.Count}（失败 {failed}），"
                               + $"用时 {FormatElapsed(sw.Elapsed)}");
        }
    }

    // 【金融监管指标】的 RunFetchBankRegulatoryAsync 2026-09-15 整体迁到
    // StockPlatform.Tasks.BankRegulatoryTask（新任务框架），这里删除。
    // 拆分后的落点：
    //   · 列公告 / 下载 PDF  → SinaReportIndex（通用数据源，【子公司名单】也用）
    //   · 逐候选试到解析出指标 → BankReportFetcher
    //   · 本地已有 PDF 重解析  → BankReportReparser（【重解析已有PDF】跟它共用同一份）
    //   · 认金融机构 / 删文件护栏 → FinancialInstitutionRoster（有用例钉着）
    // FetchFinancialsForCodesAsync 留在这里，以委托形式注给那个任务做前置补数。

    /// <summary>
    /// 导入人工回填的监管指标（2026-08-29 新增）。读 <see cref="ManualFillWorklist.FileName"/>，
    /// 只认「填这里」那列有数字的行；写入后来源标 'manual'，自动重解析不会再覆盖它们。
    /// </summary>
    /// <summary>「待手工回填清单」还剩多少项要人填——摆在计划表【导入手工数据】那一行。</summary>
    public (int Total, int NeedFill) GetManualFillPending()
        => ManualFillWorklist.CountPending(Path.Combine(_paths.ReportsDir, ManualFillWorklist.FileName));

    public Task<FetchResult> RunImportManualMetricsAsync(IProgress<string>? progress, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new FetchResult();
            var csv = Path.Combine(_paths.ReportsDir, ManualFillWorklist.FileName);
            if (!File.Exists(csv))
            {
                var msg = $"找不到清单文件：{csv}。请先跑一次【金融监管指标】生成它。";
                progress?.Report("⚠ " + msg);
                result.Errors.Add(msg);
                return result;
            }
            progress?.Report($"读取 {csv} ...");
            var (imported, confirmed, skipped, errors) = ManualFillWorklist.Import(_paths.CurrentDb, csv);
            foreach (var e in errors) { progress?.Report("⚠ " + e); result.Errors.Add(e); }
            progress?.Report($"导入完成：写入 {imported} 条人工填的值，"
                           + $"确认 {confirmed} 条 OCR 值正确（留空的那些），"
                           + $"跳过 {skipped} 条（既没 OCR 值也没填）。"
                           + "这两类以后都不会被自动解析覆盖。");
            return result;
        }, ct);

    /// <summary>
    /// 优化数据库（2026-08-29 新增）——给几张大表补建二级索引并更新统计信息，见
    /// <see cref="SqliteMaintenance"/> 和 <see cref="SqliteSchema.BigTableIndexes"/>。
    ///
    /// 不联网、不抓任何数据，纯本地维护。一次性动作：建成后是持久对象，之后由 SQLite 自动维护，
    /// 不用再点（重复点会检测到已存在、秒返回）。
    /// </summary>
    public Task<FetchResult> RunOptimizeDatabaseAsync(IProgress<string>? progress, CancellationToken ct = default)
        // 整段是同步的阻塞 IO（CREATE INDEX），扔到线程池跑，别占着 UI 线程。
        => Task.Run(() =>
        {
            var result = new FetchResult();
            try
            {
                new SqliteMaintenance(_paths.CurrentDb).BuildIndexes(
                    s => progress?.Report(s), ct, liveness: s => Liveness?.Invoke(s));
            }
            catch (OperationCanceledException)
            {
                // 中断是安全的：每条索引各自独立事务，已建好的保留，下次点会跳过继续。
                progress?.Report("已停止；已建好的索引保留，下次点【优化数据库】会跳过它们继续建。");
                throw;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"优化数据库失败：{ex.Message}");
            }
            return result;
        }, ct);

    // 【拉取行业分类】2026-09-10 迁成新式任务 StockPlatform.Tasks/IndustryTask.cs
    // （判据见 doc/full-audit-task-migration-design.md §0）。这里不再留一份，
    // 免得配置切换和调度侧准入各走各的路。

    // 【拉取财务报表】2026-09-10 迁成新式任务 StockPlatform.Tasks/FinancialTask.cs，
    // 待抓判据抽成 FinancialFetchPlanner（界面和任务都要用它，留在这儿新任务就得反过来
    // 依赖 orchestrator）。这里只留一个薄封装给界面调，别再往回加抓取逻辑。
    //
    // ⚠ 银行/券商保险监管指标那条内部路径仍在本类里（FetchFinancialsForCodesAsync），
    //   它跟新任务共用同一个 provider 实例和同一个落库方法，不要各写一份。

    /// <summary>
    /// 财务还剩多少只没补——界面用（【空闲时自动补财务】靠它决定要不要继续跑）。
    /// 实现在 <see cref="FinancialFetchPlanner"/>，不发网络请求、只查库。
    /// </summary>
    public FinancialFetchPlan GetFinancialFetchPlan(int? cap = null)
        => new FinancialFetchPlanner(_paths).Plan(cap);

    // 【拉取分红送配】2026-09-18 迁到新任务框架（StockPlatform.Tasks/DividendTask），
    //   走 MainViewModel 的 _taskRegistry 总分支；它的失败待办也由任务自己补，
    //   从这里转交过去（见 BacklogRunner 和 doc/dividend-task-design.md）。
    //   迁的理由：老实现每轮全量重抓、取消时失败名单落不了盘，被限流打断就等于整轮白跑。

}

/// <summary>
/// Per-run counters, thread-safe (many stocks are processed concurrently) — makes the "only
/// fetches what's missing locally" resume behavior (see FetchOrchestrator class remarks)
/// something the user can actually see in the log, not just something they have to trust.
/// </summary>
internal class FetchStats
{
    private int _skipped;
    private int _fetchedWithNewData;
    private int _fetchedButEmpty;
    private int _failed;

    public void Skip() => Interlocked.Increment(ref _skipped);
    public void FetchedWithNewData() => Interlocked.Increment(ref _fetchedWithNewData);
    public void FetchedButEmpty() => Interlocked.Increment(ref _fetchedButEmpty);
    public void Fail() => Interlocked.Increment(ref _failed);

    // 给进度心跳读的（见 ReportCompareProgress）——只有累计数，没有别的用途。
    public int WithNewData => Volatile.Read(ref _fetchedWithNewData);
    public int Empty => Volatile.Read(ref _fetchedButEmpty);
    public int Failed => Volatile.Read(ref _failed);

    public string Summarize() =>
        $"跳过 {_skipped} 只（本地已是最新，未发起请求）、抓到新数据 {_fetchedWithNewData} 只、" +
        $"请求成功但无新数据 {_fetchedButEmpty} 只（比如请求的日期不是交易日）、失败 {_failed} 只";
}
