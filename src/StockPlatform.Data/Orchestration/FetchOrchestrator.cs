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
/// and manifest-updating tail (<see cref="FinishFetchRun"/>):
/// - <see cref="RunFetchAsync"/> ("拉取全部"): refreshes the market-wide stock list, then for each
///   stock resumes from wherever it last left off (its own latest day-bar date in the local
///   database) up to today. This makes it safe to stop and re-run at any time — an interrupted
///   run or a handful of per-stock failures just get retried/caught up on the next run, since
///   nothing advances a stock's watermark unless that stock's fetch actually succeeded.
///   末尾还会刷新退市名单、补新退市股缺失的最后几天K线（2026-09-21 起在 StockPlatform.Tasks/DelistedTailTask，
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
/// - 【拉取区间数据】(往回补更早的历史) 2026-09-22 改成了**分派器**并迁到新框架
///   [起始年, 结束年] 的历史——把区间里"能取到历史的"各类数据一次取齐（K线/**退市股名单与历史**/资金净
///   流入/融资余额/龙虎榜/公告），逐标的、逐交易日只补本地还缺的部分。起止相同即单年。快照型数据（流通
///   市值、板块行情与成分、指数成分与权重）天生只有"当下"、没有历史可取，会明确跳过并在日志里说明原因。
///   注意它只往**后**补（补到各标的本地最早那天为止），不会抓今天的新数据——日常增量仍靠"拉取全部"。
/// </summary>
public partial class FetchOrchestrator
{
    // 板块名单那一整块（FetchBoardListCoreAsync / FetchBoardListNamesAsync /
    // CommitBoardListFromMenu / ImportBoardHierarchy / Push2PausedReason）删于 2026-09-21：
    // 整项迁去了 StockPlatform.Tasks/BoardListTask，那 280 行是**原样搬过去**的。

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
    /// 【已删 2026-09-22】待办转交口 BacklogRunner。【重新拉取失败】改成任务之后
    /// （StockPlatform.Tasks/RetryFailedTask），编排层不再参与待办分派，这个端口也就没人用了。
    /// null＝没注入，所有待办都按老路在这里编排（行为跟 2026-09-18 之前一致）。
    /// </summary>

    /// <summary>
    /// 触发新框架任务的口子（2026-09-21）——眼下只有【拉取区间数据】末尾要重合成板块指数。
    /// 端口的理由见 <see cref="ITaskRunner"/>：任务在 Tasks 层，编排层引用不到它。
    /// </summary>
    public ITaskRunner? TaskRunner { get; set; }

    private readonly FetchPaths _paths;
    private readonly IManifestStore _manifestStore;
    /// <summary>
    /// ⚠ 不是 readonly：<see cref="ReplaceBoardFetcher"/> 会在运行期换掉它（【重新读取配置】按钮）。
    /// 这一条是 <c>BoardMemberChannel</c>/<c>Push2NetworkInterface</c> 两个设置的落点——
    /// 它们决定的是**造哪个类、用什么限流参数**，光重读文件不换对象是不生效的。
    /// </summary>
    private IBoardFetcher _boardFetcher;
    private readonly IBoardRepository _boardRepository;


    private readonly IIndexConsProvider _indexConsProvider;
    private readonly IIndexWeightProvider _indexWeightProvider;
    private readonly IIndexConsRepository _indexRepository;
    /// <summary>交易日历（2026-09-08，TradingDay 表）——逐日回补靠它跳过节假日。
    /// 可空：没配就退回"只跳周末"的老行为，见 <see cref="LoadTradingCalendar"/>。</summary>
    private readonly ITradingDayRepository? _tradingDayRepository;
    private readonly IFinancialProvider? _financialProvider;
    /// <summary>分档资金流（2026-09-03，东财 push2his）。跟 NetInflow 是同一件事的不同精度。</summary>
    // 类型是接口而不是那个具体的 provider（2026-09-14）：逐股补历史现在有两条通道
    // （HttpClient／真浏览器），这里只用它报熔断状态，谁在跑都一样。
    private readonly Logic.Abstractions.IMoneyFlowDetailFetcher? _moneyFlowProvider;
    /// <summary>分档资金流的全市场当日快照（2026-09-06，push2delay）。跟上面那个是同一份数据的两种切法。</summary>
    private readonly Remote.EastMoneyMoneyFlowSnapshotProvider? _moneyFlowSnapshotProvider;
    private readonly INetInflowDetailRepository? _moneyFlowRepository;

    /// <summary>
    /// 市场事件表增量时额外往前回看的天数（见 <see cref="RunFetchMarketEventsAsync"/> 里
    /// RunOne 的注释）。
    ///
    /// 这个 30 天原本是为**大宗交易的滞后字段**定的（"事件后 N 日涨跌幅"最长 20 个交易日
    /// ≈ 28 自然日）。大宗 2026-09-17 拆去 <c>BlockTradeTask</c> 之后，留给剩下三张表的作用
    /// 变成"公告补发/修订的兜底"——那三张都是按年切片的公告类数据，多抓一小段成本极低。</summary>
    /// </summary>
    private const int LaggingFieldLookbackDays = 30;


    /// <summary>
    /// 本地库的写闸。2026-09-21 从"本类私有的一把 new object()"改成指向
    /// <see cref="SqliteWriteGate.Local"/>——**闸本身归 Sqlite 层**（分层职责原则，
    /// 见 doc/solution-class-map.md §0.1），这样新框架里的任务也能拿到同一把，
    /// 而不是像迁走的那些任务一样各写各的。本类里 46 处 <c>lock (_dbLock)</c> 一行不用改。
    /// </summary>
    private readonly object _dbLock = SqliteWriteGate.Local;

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
    /// 2026-09-21 常量本体挪到了 <see cref="IncrementalWindowCalculator.AShareMarketOpen"/>
    /// （指数日K迁进新框架之后两边都要用），这里只转发。
    private static readonly DateTime AShareMarketOpen = IncrementalWindowCalculator.AShareMarketOpen;

    /// <summary>后复权阶段的熔断门槛：完成这么多只之后才开始判断失败率（样本太少容易被偶发失败误伤）。</summary>
    private const int HfqAbortCheckAfter = 30;

    /// <summary>复权基准漂移的比对回看天数。本体在 <see cref="BarWritePlanner.DriftCheckLookbackDays"/>
    /// （2026-09-21 抽走，理由见那个类）——这里只转发，别在这儿再写一个数。</summary>
    private const int DriftCheckLookbackDays = BarWritePlanner.DriftCheckLookbackDays;

    /// <summary>财务报表每轮最多抓多少只（2026-08-27）。新浪的 vDOWN 报表接口配额很严（见
    /// Fetcher/App.xaml.cs 里那段限速注释），降速后约 10 请求/分钟、每只 3 个请求，所以 300 只
    /// 差不多要 1.5 小时。没抓完的下轮自动继续——靠 FinancialFetchState 记录的报告期和科目集
    /// 版本断点续传，抓过的不会重抓。全市场 5780 只分几天补齐，而不是一次跑 24 小时。</summary>

    /// <summary>判据本体在 <see cref="BarWritePlanner.IsDrifted"/>（Logic 层纯函数，有单测覆盖）
    /// ——这里只是转发，别在这儿再写一份。2026-09-21 抽走的理由见那个类的注释。</summary>
    private static bool IsDrifted(double stored, double fresh) =>
        BarWritePlanner.IsDrifted(stored, fresh);

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
        IBoardFetcher boardFetcher,
        IBoardRepository boardRepository,
        IIndexConsProvider indexConsProvider,
        IIndexWeightProvider indexWeightProvider,
        IIndexConsRepository indexRepository,
        IFinancialProvider? financialProvider = null,
        Logic.Abstractions.IMoneyFlowDetailFetcher? moneyFlowProvider = null,
        INetInflowDetailRepository? moneyFlowRepository = null,
        Remote.EastMoneyMoneyFlowSnapshotProvider? moneyFlowSnapshotProvider = null,
        ITradingDayRepository? tradingDayRepository = null)
    {
        _tradingDayRepository = tradingDayRepository;
        _moneyFlowProvider = moneyFlowProvider;
        _moneyFlowSnapshotProvider = moneyFlowSnapshotProvider;
        _moneyFlowRepository = moneyFlowRepository;
        _paths = paths;
        _manifestStore = manifestStore;
        _boardFetcher = boardFetcher;
        _boardRepository = boardRepository;
        _indexConsProvider = indexConsProvider;
        _indexWeightProvider = indexWeightProvider;
        _indexRepository = indexRepository;
        _financialProvider = financialProvider;
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
    /// 判据本体 2026-09-21 挪到了 <see cref="BoardMemberPlanner.StatsSince"/>（Logic 层纯函数）——
    /// 这里只转发，别在这儿再写一份。
    public static DateTime BoardMemberStatsSince(TimeSpan memberFreshFor, DateTime today)
        => BoardMemberPlanner.StatsSince(memberFreshFor, today);

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
    /// 只抓**板块名单和行情快照**（2026-09-04 从原来的板块任务里拆出来）。约 10 个请求，很快。
    ///
    /// 为什么要跟成分股分开：限流器现在是"每 15 个请求主动歇 2 分钟"（东财实测连发 16~35 个
    /// 就被切），而列表开头就要 9~10 个请求——合在一起时每轮三分之二的配额花在列表上，
    /// 只剩 5 个才轮到那 2500 个成分股请求。而且列表一挂整项就退出，成分股一个都跑不成，
    /// 可库里明明有上一次的名单、照样能接着抓成分。
    /// </summary>
    /// <returns>没开工时返回原因；正常跑完返回 null。</returns>





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
    // FetchBoardMembersCoreAsync 和 FetchBoardsCoreAsync 删于 2026-09-21：整项迁去了
    // StockPlatform.Tasks/BoardMemberTask（排序和熔断两条判据进了 Logic 的
    // BoardMemberPlanner / ConsecutiveFailureGate）。唯一还调 FetchBoardsCoreAsync 的
    // 是退役项【板块行情与成分】，它的壳也一并删了。

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
    // FetchEtfBarsAsync 删于 2026-09-21：整项迁去了 StockPlatform.Tasks/EtfBarTask。
    // 名单半截那道闸搬进了 Logic 层的 EtfListGuard，写入判据走 BarWritePlanner。
    // 【拉取区间数据】2026-09-22 改成分派器（StockPlatform.Tasks/FetchYearTask），它调的就是上面那个任务。


    // SynthesizeBoardIndexCore 删于 2026-09-21：整项迁去了 StockPlatform.Tasks/BoardIndexTask。
    // 那句"单事务写 468 万行"的旧判断 09-12 已更正——循环一直是一个板块一个事务，
    // WAL 涨到 162 GB 的真实成因是残留进程握着库、被动 checkpoint 被跳过。


    private void RecordDriftedForRepair(
        IReadOnlyList<string> driftedCodes, SqliteBarRepository currentRepo, IProgress<string>? progress)
    {
        if (driftedCodes.Count == 0) return;

        // 判据（"只有历史比手上那一页更长的才需要重取"）2026-09-21 抽去了
        // Logic 层的 QfqRepairPlanner——这里只负责查库和写名单。
        var earliestByCode = currentRepo.GetEarliestPeriodStartByCode(Granularity.Day);
        var targets = QfqRepairPlanner.SelectForRepair(driftedCodes, earliestByCode, DateTime.Today);
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

    // RunFetchEarningsScheduleAsync 删于 2026-09-21：整项迁去了
    // StockPlatform.Tasks/EarningsScheduleTask（"改期对账"那一段跟着搬走了）。


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
    // RunFetchEarningsForecastAsync 删于 2026-09-21：整项迁去了
    // StockPlatform.Tasks/EarningsForecastTask（两段各自 try 那条规矩跟着搬走了）。

    // RunFetchMarketEventsAsync 删于 2026-09-21：整项迁去了 StockPlatform.Tasks/MarketEventTask
    // （起点那三支判据搬进了 Logic 的 MarketEventWindowRule）。

    // RunFetchStockBoardMapAsync 删于 2026-09-21：整项迁去了
    // StockPlatform.Tasks/StockBoardMapTask（"拿到第一批才清表"那条快照语义跟着搬走了）。


    /// <summary>
    /// 哪些标的需要不复权日线：**只有个股和退市股**。
    /// 指数不除权（三种复权返回同一序列）、ETF 暂不进回测、板块指数是本地合成的——
    /// 给它们抓不复权纯属浪费（实测全库 7658 个标的里有 2000 个是这类）。
    /// </summary>
    /// <summary>
    /// 要补不复权的标的：只有**个股和退市股**（指数不除权、ETF 走自己那一项、板块指数是本地合成的）。
    /// 老库 type=NULL 的行算个股——这条口径由 <see cref="SqliteStockMetaUpsert.GetByTypes"/> 带着。
    /// 2026-09-21 从手写 SQL 改成调它，跟 <c>StockAdjustedBarTask</c> 用的是同一个读法。
    /// </summary>
    private HashSet<string> RawBarTargetCodes()
    {
        try
        {
            return SqliteStockMetaUpsert
                .GetByTypes(_paths.CurrentDb, SqliteStockMetaUpsert.TypeStock, SqliteStockMetaUpsert.TypeDelisted)
                .Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        }
        catch { return new HashSet<string>(StringComparer.Ordinal); }   // 读不到就当没有，调用方自然什么都不做
    }

    /// <summary>判据本体在 <see cref="RawBarCompletenessRule.IsComplete"/>（Logic 层纯函数，有单测覆盖）
    /// ——2026-09-21 抽走，这里只转发。**两头都要比**那条教训写在那个类上。</summary>
    private static bool RawBarsComplete(DateTime dayEarliest, DateTime dayLatest,
                                        DateTime? rawEarliest, DateTime? rawLatest)
        => RawBarCompletenessRule.IsComplete(dayEarliest, dayLatest, rawEarliest, rawLatest);

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

    // RunRepairQfqAsync（【重取前复权】）删于 2026-09-22：整项迁去了
    // StockPlatform.Tasks/QfqRepairTask（继承 BarFetchTaskBase，一批 10 只、每批存完就从
    // PendingQfqRepairCodes 划账）。迁的理由就是这个老实现的毛病：Task.WhenAll 把整批丢给
    // 限流器、划账在 WhenAll 之后，而取消是直接冒泡的——被停止时名单一个都不更新，
    // 已经按新基准重写完的票下一轮全部重抓一遍（每只十年多页、约 4 秒）。
    // 覆盖写入的判据（BarWritePlanner 的 overwrite 那一路）一行没动，搬的只是外面那圈循环。
    // 上面的 GetPendingQfqRepairCount 留着——界面刷新"待重取 N 只"走的是 orchestrator。

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

    // CatchUpDelistedTailsAsync 删于 2026-09-21：整项迁去了 StockPlatform.Tasks/DelistedTailTask
    // （筛选判据搬进 Logic 的 DelistedTailPlanner）。原来那个 FetchDelistedForRangeAsync 是
    // 【拉取区间数据】用的另一条路，跟它不是一回事，还在。

    // 【补整天缺失】FillMissingNetInflowDaysAsync 和【补残缺日】FillPartialDaysAsync
    // 双双删于 2026-09-18：
    //   · 前者只服务资金净流入，随 NetInflowTask 一起迁走（见 doc/netinflow-task-design.md）；
    //   · 后者在两融迁走时就只剩"转发给 PartialDayRepair 并报一句只能人工处理"的空壳——
    //     唯一还会走到它的资金净流入这次也自己补了，于是没有任何调用方。
    // 残缺日的编排本体一直在 PartialDayRepair（各任务共用），删掉的只是编排器这侧的转发。

    // PartialDaysOf 删于 2026-09-18：唯一的用户是两融整段回补，随 MarginTask 一起迁走了
    // （任务侧直接用 PartialDayRepair.DaysOf——"整段回补要把已知残缺日从 have 里扣掉，
    // 否则那些天会被当成'已有'永远跳过"这条规矩跟着搬了过去）。

    // RefetchFailedBarsAsync 删于 2026-09-21：K线六项 + ETF不复权都自己补待办了
    // （StockPlatform.Tasks/BarFetchTaskBase.Backlog.cs）。顺带修掉一个错：
    // 那里按 taskId 猜口径，StepEtfRawBars 落进 `_ => day`，于是它的失败票被按前复权重抓。

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

    // FetchIndexBarsAsync 删于 2026-09-21：整项迁去了 StockPlatform.Tasks/IndexBarTask
    // （含"整段回补从开市首日起"和跑完逐条报本地最早日期那两段）。

    private static readonly Dictionary<DateTime, double> EmptyCloses = new();

    /// <summary>
    /// 取"抓回来这一段里、除今天以外的那几天"在库里已有的收盘价，喂给
    /// <see cref="BarWritePlanner.Plan"/> 当比对样本。
    ///
    /// ⚠ 只查**这一段**、不查全历史：漂移比对的窗口本来就只有手上这一页
    /// （见 <see cref="BarWritePlanner.DriftCheckLookbackDays"/>），查多了纯属拖慢。
    /// </summary>
    private static IReadOnlyDictionary<DateTime, double> StoredClosesFor(
        SqliteBarRepository repo, string code, string granularity, IReadOnlyList<Bar> fetched)
    {
        var today = DateTime.Today;
        DateTime? from = null, to = null;
        foreach (var b in fetched)
        {
            if (b.PeriodStart.Date == today) continue;
            if (from is null || b.PeriodStart < from) from = b.PeriodStart;
            if (to is null || b.PeriodStart > to) to = b.PeriodStart;
        }
        if (from is null) return EmptyCloses;      // 抓回来的全是今天那几根

        var stored = new Dictionary<DateTime, double>();
        foreach (var b in repo.Query(code, granularity, from.Value, to!.Value))
            stored[b.PeriodStart.Date] = b.Close;
        return stored;
    }

    /// <summary>"正在对比数据"这一条只是给用户看整体进度用的粗粒度心跳（跳过的/真的发了请求的
    /// 都算在内），跟"正在抓取 {code}"那条不是一回事——那条才是"这只股票确实发了网络请求"的
    /// 精确记录（那套逻辑现在在 StockPlatform.Tasks/BarFetchTaskBase）。报告间隔沿用之前的"每5只报一次"（不是每50），这样日志
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

    /// <summary>
    /// Shared tail for all three fetch modes——只更新 manifest 的 LastFetchAt/LastFetchKind/
    /// FailedCodes，不再产出任何文件（2026-07-09移除master/daily文件生产，见下方"状态变更记录"）。
    /// </summary>
    private FetchResult FinishFetchRun(
        ConcurrentBag<string> errors, string fetchKind,
        IReadOnlyCollection<string> attemptedCodes, ConcurrentBag<string> failedCodesThisRun,
        IProgress<string>? progress = null,
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

        var result = new FetchResult();
        result.Errors.AddRange(errors);
        return result;
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
    /// 判据本体在 <see cref="FailedTodoRule.Update"/>（Logic 层纯函数，有单测覆盖）——
    /// 2026-09-21 抽走，这里只转发。在那之前同一条规则还在 <c>NetInflowTask</c> 里写了第二份。
    private static List<string> ComputeUpdatedFailedCodes(List<string> currentFailed, IReadOnlyCollection<string> attemptedCodes, IReadOnlyCollection<string> failedCodesThisRun)
        => FailedTodoRule.Update(currentFailed, attemptedCodes, failedCodesThisRun);

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
        => FailedTodoRule.SetFailed(m, taskId, attemptedCodes, failedThisRun);

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

    /// <summary>
    /// ETF→指数 名称匹配。**判据在 <see cref="EtfIndexMatcher"/>**（Logic 层纯函数，2026-09-22 抽走）——
    /// 【ETF指数映射】那一项迁成任务之后有两个调用方了，各写一份迟早分叉。这里只剩取数和报一句。
    /// </summary>
    private List<(string EtfCode, string? IndexCode, string MatchType)> BuildEtfIndexMap(IProgress<string>? progress)
    {
        var etfs = SqliteStockMetaUpsert.GetAllInstruments(_paths.CurrentDb)
            .Where(x => x.Type == SqliteStockMetaUpsert.TypeEtf)
            .Select(x => (x.Code, x.Name)).ToList();

        var matches = EtfIndexMatcher.Match(etfs, IndexCatalog.All.Select(i => (i.Code, i.Name)));
        int matched = matches.Count(m => m.IndexCode != null);
        progress?.Report($"ETF→指数名称匹配：{etfs.Count} 只 ETF，匹配到 {matched}、未匹配 {etfs.Count - matched}" +
                         "（未匹配多为债券/货币/黄金ETF，本就无A股成分）");
        return matches.Select(m => (m.EtfCode, m.IndexCode, m.MatchType)).ToList();
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
