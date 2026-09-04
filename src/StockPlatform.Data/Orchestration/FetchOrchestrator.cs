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
    private readonly FetchPaths _paths;
    private readonly IManifestStore _manifestStore;
    private readonly IFundamentalMetricRepository _fundamentalRepository;
    private readonly IMarketCapFetcher _marketCapFetcher;
    private readonly INetInflowFetcher _netInflowFetcher;
    private readonly AnnouncementFetchOrchestrator _announcementOrchestrator;
    private readonly IBoardFetcher _boardFetcher;
    private readonly IBoardRepository _boardRepository;
    private readonly IStockListProvider? _etfListProvider;
    private readonly IIndexConsProvider _indexConsProvider;
    private readonly IIndexWeightProvider _indexWeightProvider;
    private readonly ILhbProvider _lhbProvider;
    private readonly IIndexConsRepository _indexRepository;
    private readonly ILhbRepository _lhbRepository;
    private readonly IShareholderProvider _shareholderProvider;
    private readonly IShareholderRepository _shareholderRepository;
    private readonly IMarginProvider _marginProvider;
    private readonly IMarginRepository _marginRepository;
    private readonly IDelistedListProvider? _delistedListProvider;
    private readonly IFinancialProvider? _financialProvider;
    private readonly IDividendProvider? _dividendProvider;
    private readonly IDividendRepository? _dividendRepository;
    private readonly IIndustryProvider? _industryProvider;
    private readonly Remote.CninfoPrebookProvider? _prebookProvider;
    /// <summary>业绩预告/快报（2026-09-03，东财）。没有回退源——新浪/腾讯/交易所都不提供结构化预告，
    /// 巨潮只有公告原文。所以东财不可用时这一项整体跳过，不像板块那样有备胎。</summary>
    private readonly Remote.EastMoneyEarningsForecastProvider? _forecastProvider;
    private readonly IEarningsForecastRepository? _forecastRepository;
    /// <summary>龙虎榜营业部席位明细（2026-09-03，东财）。跟现有的 Lhb 表是不同粒度、不是替换。</summary>
    private readonly Remote.EastMoneyLhbSeatProvider? _lhbSeatProvider;
    private readonly ILhbSeatRepository? _lhbSeatRepository;
    /// <summary>分档资金流（2026-09-03，东财 push2his）。跟 NetInflow 是同一件事的不同精度。</summary>
    private readonly Remote.EastMoneyMoneyFlowProvider? _moneyFlowProvider;
    private readonly INetInflowDetailRepository? _moneyFlowRepository;
    /// <summary>大宗交易/机构调研/限售解禁/股东增减持（2026-09-03，东财）。本地此前全都没有。</summary>
    private readonly Remote.EastMoneyMarketEventProvider? _marketEventProvider;
    private readonly IMarketEventRepository? _marketEventRepository;
    /// <summary>个股行业/题材归属（2026-09-03，东财 datacenter）。补证监会分类的粒度不足。</summary>
    private readonly Remote.EastMoneyStockBoardMapProvider? _boardMapProvider;
    private readonly IStockBoardMapRepository? _boardMapRepository;
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
    private const int MaxFinancialFetchPerRun = 300;

    /// <summary>
    /// 判定某根K线是否发生了复权基准漂移：库里存的值与数据源当前给出的值不一致。
    /// 阈值取"相对 0.2% 与绝对 0.005 元的较大者"——足够小以捕捉几分钱的现金分红调整，
    /// 又不会被浮点噪声和低价股的分位误差误判。
    /// </summary>
    private static bool IsDrifted(double stored, double fresh) =>
        Math.Abs(stored - fresh) > Math.Max(0.005, Math.Abs(fresh) * 0.002);

    // 15:00只是常规连续竞价的收盘时间，15:00~15:30还有盘后定价交易（大宗/固定价格成交），这段
    // 时间抓到的数据不算真正确定——用16:00才能确保盘后定价交易也结束了，判断"某一天的数据是不是
    // 已经收盘后抓到、以后不会再变了"更安全（2026-07-09新增，2026-07-09从15点改成16点，见
    // IsConfirmedFinal）。故意不处理早收盘的极少数节假日前半天交易——用这个固定较晚的时间点判断
    // 只会让那些日子多等一会儿才被认定为"最终"，不会出现"提前认定成最终、结果数据其实还会变"的
    // 反向错误，属于保守但安全的简化。
    private const int MarketCloseHour = 16;

    /// <summary>某一天(<paramref name="tradingDay"/>)的数据，如果实际抓到的时间
    /// (<paramref name="fetchedAt"/>) 已经在那天16点之后（或者压根是更晚的日子才抓到的），就
    /// 认为是收盘后确认的最终数据，以后不用再为这一天重新发请求——不管是当天多次重复运行，还是
    /// 隔了几天才想起来要补，只要抓取时间点晚于当天16点就成立，不需要额外判断具体是哪一天。</summary>
    private static bool IsConfirmedFinal(DateTime fetchedAt, DateTime tradingDay) =>
        fetchedAt >= tradingDay.Date.AddHours(MarketCloseHour);

    /// <summary>"mm\:ss"格式的TimeSpan在超过1小时后会把小时部分直接丢掉（比如1小时5分12秒会被
    /// 打印成"05:12"，看起来像是时间变短了/重置了，而不是继续在涨）——全市场扫描现在经常跑到
    /// 一小时以上，这个格式化统一换成超过1小时时带上小时数。</summary>
    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

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
        IIndustryProvider? industryProvider = null,
        Remote.CninfoPrebookProvider? prebookProvider = null,
        Remote.EastMoneyEarningsForecastProvider? forecastProvider = null,
        IEarningsForecastRepository? forecastRepository = null,
        Remote.EastMoneyLhbSeatProvider? lhbSeatProvider = null,
        ILhbSeatRepository? lhbSeatRepository = null,
        Remote.EastMoneyMoneyFlowProvider? moneyFlowProvider = null,
        INetInflowDetailRepository? moneyFlowRepository = null,
        Remote.EastMoneyMarketEventProvider? marketEventProvider = null,
        IMarketEventRepository? marketEventRepository = null,
        Remote.EastMoneyStockBoardMapProvider? boardMapProvider = null,
        IStockBoardMapRepository? boardMapRepository = null)
    {
        _boardMapProvider = boardMapProvider;
        _boardMapRepository = boardMapRepository;
        _moneyFlowProvider = moneyFlowProvider;
        _moneyFlowRepository = moneyFlowRepository;
        _marketEventProvider = marketEventProvider;
        _marketEventRepository = marketEventRepository;
        _prebookProvider = prebookProvider;
        _forecastProvider = forecastProvider;
        _forecastRepository = forecastRepository;
        _lhbSeatProvider = lhbSeatProvider;
        _lhbSeatRepository = lhbSeatRepository;
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
        _industryProvider = industryProvider;
    }

    /// <summary>
    /// 抓取板块数据（概念/题材 + 行业）及各板块成分股，整体覆盖写入本地库（见 IBoardRepository）。
    /// 独立于 K线/市值/资金流的抓取——是一个单独的按钮触发（"拉取板块"），因为板块热点是"当下快照"、
    /// 跟历史K线的增量抓取不是一回事，也不想让它拖慢主抓取。
    /// </summary>
    public async Task<FetchResult> RunFetchBoardsAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var errors = new ConcurrentBag<string>();
        var skipped = await FetchBoardsCoreAsync(errors, progress, ct);
        if (skipped != null)
            return new FetchResult { Errors = errors.ToList(), SkippedReason = skipped };

        // 拉完板块紧接着合成板块指数（不联网，用本地已有个股K线按新成分重算）——2026-07-16 合并为
        // 一步。2026-09-02 拆分之后合成也有了自己的计划项（StepBoardIndex / RunStepSynthesizeBoardIndexAsync），
        // 这里仍旧带着跑，让老按钮的行为保持不变；排了独立项的人多跑一次也只是几分钟本地计算。
        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();
        SynthesizeBoardIndexCore(currentRepo, errors, progress, ct);

        return new FetchResult { Errors = errors.ToList() };
    }

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
        if (_boardFetcher is Remote.EastMoneyBoardFetcher emb && emb.PausedUntil is { } a)
            list.Add(("东财 push2", a));
        if (_moneyFlowProvider?.PausedUntil is { } b)
            list.Add(("东财 push2his", b));
        return list;
    }

    /// <summary>板块成分股这一轮之后的存量进度，给界面显示用（见 FetchResult.Progress）。</summary>
    private string? _memberProgressText;

    /// <summary>
    /// 还在限流熔断里吗——是的话返回该说的话，调用方直接收工。
    /// 进去也只是在限流器里干等到超时报失败（实测干等过 8 分钟），不如把话说清楚。
    /// </summary>
    private string? Push2PausedReason()
    {
        if (_boardFetcher is not Remote.EastMoneyBoardFetcher emb) return null;
        if (emb.PausedUntil is not { } until) return null;
        var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
        return $"东财 push2 限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
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
    private async Task<string?> FetchBoardListCoreAsync(
        ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        if (Push2PausedReason() is { } paused)
        {
            progress?.Report($"{paused}，本轮不开工。库里保留上一次的板块名单。");
            return paused;
        }

        void Forward(string s) => progress?.Report(s);
        _boardFetcher.OnStatus += Forward;
        try
        {
            if (_boardFetcher is Remote.EastMoneyBoardFetcher em2)
            {
                // 浏览器通道初始化要几秒（建 WebView2 + 打开东财页面拿 Cookie），
                // 放在这儿而不是第一个请求里，免得把那几秒算进限流节奏
                await em2.PrepareBrowserAsync(ct);
                progress?.Report(em2.DescribeChannel() + "；" + em2.DescribeBinding());
            }

            _boardRepository.EnsureSchema();

            // 按页续传 + 凑齐才提交——循环本体和它藏过的两个 bug 见 BoardListFetchLoop
            if (_boardFetcher is not Remote.EastMoneyBoardFetcher pager)
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
        if (Push2PausedReason() is { } paused)
        {
            progress?.Report($"{paused}，本轮不开工。已抓到的板块都在库里，恢复后接着抓没抓过的。");
            return paused;
        }

        void Forward(string s) => progress?.Report(s);
        _boardFetcher.OnStatus += Forward;
        try
        {
            if (_boardFetcher is Remote.EastMoneyBoardFetcher em3)
            {
                await em3.PrepareBrowserAsync(ct);
                progress?.Report(em3.DescribeChannel() + "；" + em3.DescribeBinding());
            }

            _boardRepository.EnsureSchema();

            // 名单从库里读——列表那一项没跑也能干活，只是漏掉当天新增的板块（软依赖）
            var all = _boardRepository.QueryBoards();
            if (all.Count == 0)
            {
                progress?.Report("库里还没有板块名单，先跑一次【板块列表】再来抓成分股。");
                return "库里还没有板块名单（先跑【板块列表】）";
            }

            var since = DateTime.Today.AddDays(-7);   // 一周内抓过的算新鲜，不重复抓
            var fresh = _boardRepository.GetBoardsWithFreshMembers(since);
            var todo = all.Where(b => !fresh.Contains(b.BoardCode)).ToList();
            progress?.Report($"成分股：{fresh.Count} 个板块已是最近抓的，本轮需抓 {todo.Count} 个。");
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

            var (pOk, pFailed, pNever) = _boardRepository.GetMemberFetchProgress(since);
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
    /// （给调用方并入 attempted）。</summary>
    private async Task<List<string>> FetchEtfBarsAsync(
        NamedBarSource source, DateTime end, int lookbackYears, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        if (_etfListProvider == null) return new List<string>();
        progress?.Report("正在获取全市场ETF列表...");
        List<StockListEntry> etfs;
        try { etfs = await _etfListProvider.GetAllStocksAsync(progress, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"获取ETF列表失败（跳过ETF）：{ex.Message}"); return new List<string>(); }
        if (etfs.Count == 0) { progress?.Report("ETF列表为空（接口可能不可达/被限流），本轮跳过ETF。"); return new List<string>(); }
        progress?.Report($"共 {etfs.Count} 只 ETF，开始抓取日K（已用时 {FormatElapsed(sw.Elapsed)}）...");
        // ETF 名称写进 StockMeta（type=etf）——让"查询"页能搜到 ETF（不影响个股选股）。
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, etfs.Select(e => (e.Code, e.Name)), SqliteStockMetaUpsert.TypeEtf);

        int completed = 0;
        var tasks = etfs.Select(etf =>
        {
            // 跟个股/大盘指数一样的逐标的水位线：没抓过从回看窗口起点(首次补历史)，抓过从上次+1，"今天"看是否收盘确认。
            DateTime start;
            lock (_dbLock)
            {
                var info = currentRepo.GetLatestBarInfo(etf.Code, Granularity.Day);
                if (info == null)
                    start = end.AddYears(-lookbackYears);
                else if (info.Value.PeriodStart.Date < end.Date)
                    start = info.Value.PeriodStart.AddDays(1);
                else
                    start = IsConfirmedFinal(info.Value.FetchedAt, end) ? end.AddDays(1) : end;
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
                    if (bars.Count > 0) currentRepo.InsertOrIgnore(bars);
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
            if (++done % 40 == 0 || done == boards.Count)
                progress?.Report($"合成板块指数：{done}/{boards.Count}（已生成 {withBars} 个板块、{totalBars} 根日K）");
        }
        // 有指数K的板块名称写进 StockMeta（type=board）——让"查询"页能搜到板块、看行情（不影响个股选股）。
        if (quotes.Count > 0) _boardRepository.UpdateQuotes(quotes);
        if (synthesizedMeta.Count > 0)
            SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, synthesizedMeta, SqliteStockMetaUpsert.TypeBoard);
        progress?.Report($"板块指数合成完成：{boards.Count} 个板块，其中 {withBars} 个成分股数据足够、已写入 {totalBars} 根日K（code=板块代码，不进个股选股）。");
    }

    /// <summary>
    /// Fetches every A-share stock automatically — the user does not type in codes, they
    /// just pick a source and click "拉取全部"; the program looks up the full market list itself.
    /// </summary>
    /// <param name="lookbackYears">How far back to backfill a stock that has NO local history yet
    /// (never fetched before, or newly IPO'd since the last run) — does not affect stocks already
    /// tracked locally, their resume point is always their own last local date + 1 regardless of
    /// this value (see remarks). User-adjustable, default 3.</param>
    public async Task<FetchResult> RunFetchAsync(
        NamedBarSource source, int lookbackYears, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        // Forward the rate limiter's out-of-band status (e.g. "intentionally pausing, not
        // hung" — see RateLimiter/IBarDataFetcher.OnStatus) into this run's progress log. The
        // fetcher/its RateLimiter live for the whole app session, so this must be unsubscribed
        // when the run ends — otherwise a later run would get duplicate deliveries.
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        _marketCapFetcher.OnStatus += ForwardStatus;
        _netInflowFetcher.OnStatus += ForwardStatus;
        _lhbProvider.OnStatus += ForwardStatus;      // 龙虎榜每日数据已并入主流程
        _marginProvider.OnStatus += ForwardStatus;   // 融资余额每日数据已并入主流程
        try
        {
            return await RunFetchAllInternalAsync(source, lookbackYears, announcementKeywords, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
            _marketCapFetcher.OnStatus -= ForwardStatus;
            _netInflowFetcher.OnStatus -= ForwardStatus;
            _lhbProvider.OnStatus -= ForwardStatus;
            _marginProvider.OnStatus -= ForwardStatus;
        }
    }

    /// <summary>See the class remarks — "拉取当天", independent of each stock's watermark.</summary>
    public async Task<FetchResult> RunFetchDayAsync(
        NamedBarSource source, DateOnly date, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        _marketCapFetcher.OnStatus += ForwardStatus;
        _netInflowFetcher.OnStatus += ForwardStatus;
        _lhbProvider.OnStatus += ForwardStatus;
        _marginProvider.OnStatus += ForwardStatus;
        try
        {
            return await RunFetchDayInternalAsync(source, date, announcementKeywords, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
            _marketCapFetcher.OnStatus -= ForwardStatus;
            _netInflowFetcher.OnStatus -= ForwardStatus;
            _lhbProvider.OnStatus -= ForwardStatus;
            _marginProvider.OnStatus -= ForwardStatus;
        }
    }

    private async Task<FetchResult> RunFetchAllInternalAsync(
        NamedBarSource source, int lookbackYears, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct)
    {
        var today = DateTime.Today;
        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();

        var sw = Stopwatch.StartNew();
        progress?.Report("正在获取全市场股票列表...");
        var stocks = await source.StockListProvider.GetAllStocksAsync(progress, ct);
        progress?.Report($"共 {stocks.Count} 只股票，数据源：{source.Name}，开始抓取（已用时 {FormatElapsed(sw.Elapsed)}）");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, stocks.Select(s => (s.Code, s.Name)));

        await FetchMarketCapAsync(source, stocks.Select(s => s.Code).ToList(), progress, ct);
        await FetchNetInflowAsync(stocks.Select(s => s.Code).ToList(), today, exactDayOnly: false, progress, ct);
        await FetchAnnouncementsAsync(
            announcementKeywords, DateOnly.FromDateTime(today.AddDays(-AnnouncementLookbackDaysForFetchAll)),
            DateOnly.FromDateTime(today), progress, ct);

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();
        await FetchIndexBarsAsync(source, today, lookbackYears, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        await FetchStockDayBarsAsync(source, stocks.Select(s => s.Code).ToList(), today, lookbackYears,
            currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 后复权日K（回测专用，见 FetchHfqBarsAsync）——只对个股，指数/ETF不需要。
        await FetchHfqBarsAsync(source, stocks.Select(s => s.Code).ToList(),
            code => HfqWatermarkWindow(currentRepo, code, today, lookbackYears),
            currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 不复权日K（原始成交价）——跟后复权并列，日常增量在这里顺带抓一根，
        // 这样【补不复权历史】就只剩"首次回补十年"这一件事，跑完一次就基本不用再管了。
        await FetchHfqBarsAsync(source, stocks.Select(s => s.Code).ToList(),
            code => HfqWatermarkWindow(currentRepo, code, today, lookbackYears, Granularity.DayRaw),
            currentRepo, errors, failedCodes, stats, progress, sw, ct, gran: Granularity.DayRaw);

        // 个股抓完后，末尾顺带跑 ETF 和板块指数合成（合成放最后，要读当天个股K线）。
        var etfCodes = await FetchEtfBarsAsync(source, today, lookbackYears, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 退市股收尾：只补"本地已跟踪过、但最后一根K线还早于终止日"的那几只（见方法注释）。
        var delistedCodes = await CatchUpDelistedTailsAsync(source, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        SynthesizeBoardIndexCore(currentRepo, errors, progress, ct);

        // 每日数据（融资余额/龙虎榜）并入主流程——非致命，失败只记 error 不影响 K线；更早的历史用
        // "一键补齐每日历史"补。融资余额要回看最近几个交易日、不能只抓当天（两所T+1发布，
        // 见 MarginLookbackTradingDays 的说明）；龙虎榜当晚就发布，抓当天即可。
        await FetchMarginRecentAsync(today, errors, progress, ct);
        await FetchLhbOneDayAsync(today, errors, progress, ct);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        var attempted = stocks.Select(s => s.Code)
            .Concat(MarketIndexCatalog.All.Select(i => i.Symbol))
            .Concat(etfCodes)
            .Concat(delistedCodes).ToList();
        return FinishFetchRun(errors, "拉取全部", attempted, failedCodes, progress, checkDayCoverage: true);
    }

    private async Task<FetchResult> RunFetchDayInternalAsync(
        NamedBarSource source, DateOnly date, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法按天抓取，请先执行一次\"拉取全部\"");

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();
        var stocks = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb);
        if (stocks.Count == 0)
            throw new InvalidOperationException("本地股票列表为空，无法按天抓取，请先执行一次\"拉取全部\"");

        var day = date.ToDateTime(TimeOnly.MinValue);
        var sw = Stopwatch.StartNew();
        progress?.Report($"按天抓取 {date:yyyy-MM-dd}，共 {stocks.Count} 只股票（K线用本地已有列表、不为K线重新扫全市场；流通市值步骤仍会扫一遍全市场、顺带发现新股），数据源：{source.Name}");

        // 流通市值本来就要扫一遍全市场列表，顺带发现的新股（本地列表里还没有的代码）在这里并入
        // 本轮的 stocks——这样"拉取当天"也能当天就把新股纳入K线/资金净流入抓取，不用非得先专门跑
        // 一次"拉取全部"才会发现它（2026-07-10新增，见 FetchMarketCapAsync 的类注释）。
        var (newCodes, _) = await FetchMarketCapAsync(source, stocks.Select(s => s.Code).ToList(), progress, ct);
        if (newCodes.Count > 0)
            stocks = stocks.Concat(newCodes).ToList();

        await FetchNetInflowAsync(stocks.Select(s => s.Code).ToList(), day, exactDayOnly: true, progress, ct);
        await FetchAnnouncementsAsync(announcementKeywords, date, date, progress, ct);

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();
        var driftedCodes = new ConcurrentBag<string>();

        // 指数走的是水位线增量（不是"只抓这一天"）——指数总共就几个，增量补齐的代价可以忽略，
        // 而且这样第一次升级到带指数的版本时，跑一次"拉取当天"就能自动把指数近几年的历史一次
        // 补齐（跟扫市值时顺带发现的新股用长回看窗口是同一个道理）。
        await FetchIndexBarsAsync(source, day, DefaultLookbackYears, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        await FetchStockDayBarsForDayAsync(source, stocks.Select(s => s.Code).ToList(), day,
            currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 后复权日K（回测专用）——跟 ETF/指数一样走自己的水位线增量而不是"只抓这一天"，所以升级后
        // 第一次跑"拉取当天"会自动把最近 DefaultLookbackYears 年的后复权补上；要一次补齐十年历史
        // 仍需跑一次"拉取区间数据"（见 FetchHfqBarsAsync 注释）。
        await FetchHfqBarsAsync(source, stocks.Select(s => s.Code).ToList(),
            code => HfqWatermarkWindow(currentRepo, code, day, DefaultLookbackYears),
            currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 不复权日K：同上，日常增量并在这里，见"拉取全部"里的同一段注释。
        await FetchHfqBarsAsync(source, stocks.Select(s => s.Code).ToList(),
            code => HfqWatermarkWindow(currentRepo, code, day, DefaultLookbackYears, Granularity.DayRaw),
            currentRepo, errors, failedCodes, stats, progress, sw, ct, gran: Granularity.DayRaw);

        // 个股抓完后，末尾顺带跑 ETF 和板块指数合成（合成放最后，要读当天个股K线）。ETF 跟大盘指数一样
        // 走水位线增量而不是"只抓这一天"，所以升级后第一次跑"拉取当天"就会自动把 ETF 历史一次补齐。
        var etfCodes = await FetchEtfBarsAsync(source, day, DefaultLookbackYears, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 退市股收尾两种模式末尾都跑（这里 + RunFetchAllInternalAsync）：原因是2026-07-29时用户的日常
        // 入口是"拉取当天"（当时确认"拉取全部"只有第一次会点），只挂在"拉取全部"上等于永远不执行。
        // 2026-07-31 复核后用户改为日常点"拉取全部"（两者耗时相同、只有它补断档，见类注释），该按钮也
        // 已改名为"补指定历史日"——但这一步仍保留在两处：两个入口都跑才与"哪个都不漏"的初衷一致。
        // 放末尾也保证本轮的抓取清单（开头已取好）不受影响，见方法注释。
        var delistedCodes = await CatchUpDelistedTailsAsync(source, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        SynthesizeBoardIndexCore(currentRepo, errors, progress, ct);

        // 每日数据（融资余额/龙虎榜）并入"补指定历史日"。融资余额同样按"以该日为终点回看几天、
        // 跳过本地已有"处理（见 MarginLookbackTradingDays）——补历史某天时，它前面几天多半也缺，
        // 顺手一起补掉；已有的日子不会重复请求。龙虎榜仍只抓指定那一天。
        await FetchMarginRecentAsync(day, errors, progress, ct);
        await FetchLhbOneDayAsync(day, errors, progress, ct);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        var attempted = stocks.Select(s => s.Code)
            .Concat(MarketIndexCatalog.All.Select(i => i.Symbol))
            .Concat(etfCodes)
            .Concat(delistedCodes).ToList();
        return FinishFetchRun(errors, "补指定历史日", attempted, failedCodes, progress, checkDayCoverage: true);
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

        // ── 大盘指数日K ──
        progress?.Report($"开始补 {rangeLabel}大盘指数日K...");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, MarketIndexCatalog.All.Select(i => (i.Symbol, i.Name)), SqliteStockMetaUpsert.TypeIndex);
        int idxDone = 0;
        foreach (var (symbol, _) in MarketIndexCatalog.All)
        {
            var (s, e) = YearGapFor(symbol, earliestByCode, yearStart, yearEnd, tradingDays);
            await ProcessOneStockAsync(symbol, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                MarketIndexCatalog.All.Count, () => Interlocked.Increment(ref idxDone), sw, ct);
        }

        // ── 个股日K（并发受数据源限速器节流，跟"拉取全部"同一套）──
        progress?.Report(overwriteQfq
            ? $"开始【覆盖重抓】{rangeLabel}个股前复权日K（不看本地已有什么，整段按数据源当前基准重写，用来抹平复权基准接缝）..."
            : $"开始补 {rangeLabel}个股日K（本地最早日已早于 {startYear} 年年初的标的会整只跳过、不发请求）...");
        int completed = 0;
        await Task.WhenAll(stockCodes.Select(code =>
        {
            var (s, e) = overwriteQfq ? (yearStart, yearEnd) : YearGapFor(code, earliestByCode, yearStart, yearEnd, tradingDays);
            return ProcessOneStockAsync(code, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                stockCodes.Count, () => Interlocked.Increment(ref completed), sw, ct,
                Granularity.Day, overwrite: overwriteQfq);
        }));
        progress?.Report($"{rangeLabel}K线部分汇总：{stats.Summarize()}");

        // ── 个股后复权日K（回测专用）：水位线独立，用 day_hfq 自己的最早日算缺口 ──
        var earliestHfq = currentRepo.GetEarliestPeriodStartByCode(Granularity.DayHfq);
        await FetchHfqBarsAsync(source, stockCodes, code => YearGapFor(code, earliestHfq, yearStart, yearEnd, tradingDays),
            currentRepo, errors, failedCodes, stats, progress, sw, ct, $"{rangeLabel}个股");

        // ── 个股不复权日K：跟后复权并列，各按各的水位线 ──
        // 往前补历史年份时这条线也得跟上，否则 day/day_hfq 有 2012 年而 day_raw 没有，
        // 回测序列（day_adj）就只能算到 day_raw 的起点为止。
        var earliestRaw = currentRepo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        await FetchHfqBarsAsync(source, stockCodes, code => YearGapFor(code, earliestRaw, yearStart, yearEnd, tradingDays),
            currentRepo, errors, failedCodes, stats, progress, sw, ct, $"{rangeLabel}个股", Granularity.DayRaw);

        // ── ETF 日K ──
        var etfCodes = await FetchEtfBarsForYearAsync(source, yearStart, yearEnd, earliestByCode, currentRepo, errors, failedCodes, stats, progress, sw, ct, tradingDays);

        // ── 退市股：名单 + 区间内的历史日K（前复权+后复权，消除回测幸存者偏差，见 FetchDelistedForRangeAsync）──
        var delistedCodes = await FetchDelistedForRangeAsync(source, yearStart, yearEnd, rangeLabel,
            currentRepo, earliestByCode, earliestHfq, errors, failedCodes, stats, progress, sw, ct);

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
        await BackfillDailyAsync($"{rangeLabel}融资余额", DateOnly.FromDateTime(yearStart), DateOnly.FromDateTime(yearEnd), marginHave, async d =>
        {
            var rows = await _marginProvider.GetDetailAsync(d, ct);
            if (rows.Count > 0) { lock (_dbLock) { _marginRepository.InsertOrIgnore(rows); } }
            return rows.Count;
        }, errors, progress, sw, ct);

        var lhbHave = _lhbRepository.GetTradeDates();
        await BackfillDailyAsync($"{rangeLabel}龙虎榜", DateOnly.FromDateTime(yearStart), DateOnly.FromDateTime(yearEnd), lhbHave, async d =>
        {
            var rows = await _lhbProvider.GetDailyAsync(d, ct);
            if (rows.Count > 0) { lock (_dbLock) { _lhbRepository.InsertOrIgnore(rows); } }
            return rows.Count;
        }, errors, progress, sw, ct);

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
    /// 抓龙虎榜**营业部席位明细**（2026-09-03，东财 datacenter）。
    ///
    /// 跟现有的【龙虎榜】任务是**两回事，不是替换**：那一项（新浪）抓的是"某天某股上榜了、
    /// 原因是涨跌幅偏离、成交额多少"，**没有买卖前五营业部名单**——而龙虎榜的全部价值恰恰
    /// 在于看**是谁在买**：机构专用席位、知名游资、还是深股通。17 万行数据缺了这块等于只留了个壳。
    ///
    /// 数据量是本次接入里最大的（买方 131 万 + 卖方 133 万），所以是唯一走**流式回调**的：
    /// provider 每攒 2000 行就交给仓储落库，不把 264 万行全装内存。首次全量约 5200 页、
    /// 按月切片跑（东财深分页到上千页会拒绝），之后增量每月只有 40 页出头。
    /// </summary>
    public async Task<FetchResult> RunFetchLhbSeatAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = new FetchResult();
        if (_lhbSeatProvider == null || _lhbSeatRepository == null)
        {
            progress?.Report("没有配置龙虎榜席位数据源（东财），跳过。");
            result.NothingToDo = true;
            return result;
        }

        var sw = Stopwatch.StartNew();
        _lhbSeatRepository.EnsureSchema();

        void Forward(string s) => progress?.Report(s);
        _lhbSeatProvider.OnStatus += Forward;
        try
        {
            var today = DateTime.Today;
            // 水位线**回退到所在月的 1 号**，整月重抓。
            //
            // 不能直接拿 MAX(trade_date) 当起点——这张表的落库粒度是"月"，而且**一个月内买卖是
            // 分两次落库的**（先买方后卖方）。如果在某月的买方已落库、卖方还没落时被打断，
            // MAX(trade_date) 就停在了那个月的月中/月末，下一轮从那天起切片，该月月初到那天的
            // **卖方数据就永久漏掉了**。
            // 实测踩过：中断时买方最大日 2019-05-31、卖方 2019-04-30，2019-05 整月只有买方 7635 行、
            // 卖方 0 行；不回退的话 05-01~05-30 的卖方就再也补不回来。
            //
            // 回退到月初的代价只是每次重抓一个月（约 1.6 万行、几十页），主键 UPSERT 保证不重复。
            // 顺带也覆盖了"当天盘后陆续发布"那个场景（本来就要从最新那天本身重抓）。
            var watermark = _lhbSeatRepository.GetLatestTradeDate();
            var start = watermark.HasValue
                ? new DateTime(watermark.Value.Year, watermark.Value.Month, 1)
                : new DateTime(2016, 1, 1);
            bool firstRun = _lhbSeatRepository.Count() == 0;
            progress?.Report($"龙虎榜席位：从 {start:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}" +
                             (firstRun ? "（首次全量约 264 万行、5200 页，按月切片，预计数小时）" : "（增量）"));

            int total = await _lhbSeatProvider.FetchAsync(
                start, today,
                batch => _lhbSeatRepository.Upsert(batch),   // 每 2000 行落一次库
                progress, ct);

            progress?.Report($"龙虎榜席位写入 {total} 行，本地共 {_lhbSeatRepository.Count()} 行，"
                           + $"用时 {FormatElapsed(sw.Elapsed)}。");
        }
        catch (OperationCanceledException)
        {
            // 中断时已落库的部分是有效的，重跑会从本地水位线接着走——这正是流式落库的意义
            progress?.Report($"龙虎榜席位抓取中断，已落库 {_lhbSeatRepository.Count()} 行，下次从断点续。");
            throw;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"龙虎榜席位抓取失败：{ex.Message}");
            progress?.Report($"⚠ 龙虎榜席位抓取失败：{ex.Message}（已落库的部分保留，重跑会续）");
        }
        finally
        {
            _lhbSeatProvider.OnStatus -= Forward;
        }
        return result;
    }

    /// <summary>
    /// 抓分档资金流（2026-09-03，东财 push2his）。
    ///
    /// 跟现有的【资金净流入】是**同一件事的不同精度**、不是替换：那张 NetInflow 表 1077 万行，
    /// 但每行只有一个"主力净额合计"。这里拆成超大单/大单/中单/小单各自的净额与净占比。
    /// 判断资金性质要看结构不看合计——同样"主力净流入1亿"，超大单进、小单出（机构建仓）跟
    /// 大单进、超大单出（游资接力）含义完全相反，合计数把这个信息抹平了。
    ///
    /// 两个限制决定了它的抓法：
    ///   1. 接口<b>只给最近约 120 个交易日</b>，没有增量入口——每次拿回来的都是同样那批日期，
    ///      所以历史深度只能靠定期抓取慢慢养，一次抓不出长历史。
    ///   2. <b>只能按股票查</b>，全市场一轮 5500+ 个请求。
    /// 因此断点续传只能按"这只票今天抓过没有"来判断（<see cref="INetInflowDetailRepository.HasFreshData"/>），
    /// 不能像别的任务那样按数据日期做水位线。
    /// </summary>
    /// <param name="maxCount">本轮最多抓几只（null=不限）。配合计划页的时间窗，跑不完下轮接着来。</param>
    public async Task<FetchResult> RunFetchMoneyFlowDetailAsync(
        IProgress<string>? progress, CancellationToken ct = default, int? maxCount = null)
    {
        var result = new FetchResult();
        if (_moneyFlowProvider == null || _moneyFlowRepository == null)
        {
            progress?.Report("没有配置分档资金流数据源（东财），跳过。");
            result.NothingToDo = true;
            return result;
        }

        // 跟板块那边同一条：还在熔断暂停里就别开工，免得干等到超时才报失败（2026-09-04）
        if (_moneyFlowProvider.PausedUntil is { } until)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((until - DateTime.Now).TotalMinutes));
            var reason = $"东财 push2his 限流熔断中，预计 {until:HH:mm} 恢复（还有约 {mins} 分钟）";
            progress?.Report($"{reason}，本轮不开工。已抓到的都在库里，恢复后从没抓的接着来。");
            // 不能标 NothingToDo——那是"活干完了"，会让计划引擎以为这一期做完了、拉长下次间隔。
            result.SkippedReason = reason;
            return result;
        }

        var sw = Stopwatch.StartNew();
        _moneyFlowRepository.EnsureSchema();

        void Forward(string s) => progress?.Report(s);
        _moneyFlowProvider.OnStatus += Forward;
        try
        {
            var codes = LocalStockCodes();
            // 今天已经抓过的跳过——接口是滚动窗口，同一天重抓拿到的是同一批数据，纯浪费请求
            var since = DateTime.Today;
            var lastFetched = _moneyFlowRepository.GetLastFetchedAt();

            // ⚠ 排队必须是「最久没抓的先抓」，不能按代码顺序（2026-09-04 修）。
            // 原来是"今天没抓过的按代码顺序抓"，可每天零点一到，昨天抓过的又全变成
            // "今天没抓过"——于是每天都从 000001 重新开始，代码靠后的票永远轮不到。
            // 实测跑了两天，库里只有 000001~000509 这 89 只，1.5%，而且再跑多久都不会变多。
            //
            // 接口是 120 天滚动窗口，所以目标不是"每天抓全市场"（那要 17 小时，做不到），
            // 而是**保证每只票 120 天内被轮到一次**——那样历史就一天都不缺。
            // 全市场 5900 只、每天抓百来只的话 60 天转一圈，正好在窗口内。
            var todo = codes.Where(c => !_moneyFlowRepository.HasFreshData(c, since))
                            .OrderBy(c => lastFetched.TryGetValue(c, out var t) ? t : DateTime.MinValue)
                            .ToList();
            if (maxCount is > 0 && todo.Count > maxCount) todo = todo.Take(maxCount.Value).ToList();

            var never = codes.Count(c => !lastFetched.ContainsKey(c));
            var oldest = todo.Count > 0 && lastFetched.TryGetValue(todo[0], out var ot) ? (DateTime?)ot : null;
            progress?.Report(
                $"分档资金流：全市场 {codes.Count} 只，今天已抓 {codes.Count - todo.Count} 只，" +
                $"本轮抓 {todo.Count} 只（接口只给最近约120个交易日）。" +
                $"从没抓过的还有 {never} 只，先抓它们；" +
                (oldest.HasValue ? $"其余按最久没抓的排（队首上次抓于 {oldest:M-d}）。" : ""));

            int ok = 0, failed = 0, rows = 0, consecutiveFail = 0;
            for (int i = 0; i < todo.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var code = todo[i];
                try
                {
                    var list = await _moneyFlowProvider.FetchAsync(code, ct);
                    if (list.Count > 0) { rows += _moneyFlowRepository.Upsert(list); ok++; }
                    consecutiveFail = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++; consecutiveFail++;
                    if (failed <= 5) result.Errors.Add($"{code} 分档资金流失败：{ex.Message}");
                    // 连续失败=已被限流，继续打只会让封禁更久；抓到的都落库了，下轮接着来
                    if (consecutiveFail >= 15)
                    {
                        progress?.Report($"⚠ 连续 {consecutiveFail} 只失败，判定被限流，本轮提前收尾。" +
                                         $"已成功 {ok} 只，剩余 {todo.Count - i - 1} 只下轮继续。");
                        result.Errors.Add($"push2his 限流，本轮只抓到 {ok}/{todo.Count} 只。");
                        break;
                    }
                }
                if ((i + 1) % 100 == 0 || i + 1 == todo.Count)
                    progress?.Report($"分档资金流：{i + 1}/{todo.Count}（成功 {ok}、失败 {failed}、{rows} 行）");
            }

            progress?.Report(
                $"分档资金流完成：本轮成功 {ok} 只、失败 {failed} 只、写入 {rows} 行；" +
                $"本地共 {_moneyFlowRepository.CountCodes()} 只 / {_moneyFlowRepository.Count()} 行，" +
                $"用时 {FormatElapsed(sw.Elapsed)}。");
        }
        catch (OperationCanceledException)
        {
            progress?.Report($"分档资金流中断，已落库 {_moneyFlowRepository.Count()} 行，下次从没抓的接着来。");
            throw;
        }
        finally
        {
            _moneyFlowProvider.OnStatus -= Forward;
        }
        return result;
    }

    /// <summary>
    /// 抓大宗交易 / 机构调研 / 限售解禁 / 股东增减持（2026-09-03，东财 datacenter）。
    /// 本地此前这四份数据全都没有，也都没有回退源。
    ///
    /// 放一个任务里跑是因为它们同构、共用同一个 datacenter 配额，分成四项只会让人在计划页上
    /// 排四行、还得自己记住顺序。四项各自 try：一项失败不影响其余（覆盖面和重要性都不一样，
    /// 机构调研挂了不该让已经抓好的大宗交易也算失败）。
    ///
    /// 增量水位线各表自己的日期列；<b>限售解禁例外，每次全量重取</b>——它含未来的解禁计划
    /// （实测有 2035 年的），按"抓到今天为止"做增量会永远漏掉未来那部分，而未来正是它的价值。
    /// </summary>
    public async Task<FetchResult> RunFetchMarketEventsAsync(
        IProgress<string>? progress, CancellationToken ct = default)
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
            async Task RunOne(string label, string table, string dateCol, Func<DateTime, Task<int>> fetch)
            {
                try
                {
                    var start = _marketEventRepository.GetLatestDate(table, dateCol) ?? floor;
                    bool first = _marketEventRepository.Count(table) == 0;
                    progress?.Report($"{label}：从 {start:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}"
                                   + (first ? "（首次全量）" : "（增量）"));
                    int n = await fetch(start);
                    progress?.Report($"{label} 写入 {n} 行，本地共 {_marketEventRepository.Count(table)} 行。");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Errors.Add($"{label} 抓取失败：{ex.Message}");
                    progress?.Report($"⚠ {label} 抓取失败：{ex.Message}");
                }
            }

            await RunOne("大宗交易", "BlockTrade", "trade_date", start =>
                _marketEventProvider.FetchBlockTradesAsync(start, today,
                    b => _marketEventRepository.UpsertBlockTrades(b), progress, ct));

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

    /// <summary>
    /// 除权事件源（<c>Dividend</c> / <c>RightsIssue</c>）比 <c>day_adj</c> 新的那些票——它们的复权因子
    /// 是拿**旧的**除权记录算的，得重算。
    ///
    /// ⚠ 这条判据不能少（2026-09-02 补）：原来只比 day_raw 和 day_adj 的**日期范围**，
    /// 于是"价格没变、但除权记录变了"这种情况完全检测不到——
    /// 补录了一条漏掉的除权、配股方案入库、分红从"预案"变成"实施"填上了 ex_date，
    /// 全都会改变复权因子，而界面上的待办量还是 0。又是个不报错的静默错误：
    /// day_adj 静静地保持旧值，回测拿着错的收益率跑，没有任何地方会提示。
    ///
    /// **代价**：【拉取分红】是按 code 删旧写新的，跑完一轮全市场的 fetched_at 都会变新，
    /// 于是每月触发一次全量重算（5781 只，十几二十分钟）。这个代价是认的——
    /// 它纯本地、不占数据源配额、挂在「空闲时」跑，用一个月一次的机器时间换"因子永远跟事件一致"。
    /// 要更精准就得存"上次算用了哪些事件"的签名（多一张状态表），眼下不值得。
    /// </summary>
    private HashSet<string> CodesWithStaleAdjEvents()
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.CurrentDb}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // ⚠ 这里比的是「事件抓取时刻 vs day_adj 的重算时刻」。后者从 2026-09-04 起才是真的
            // "重算时刻"——在那之前 day_adj 的 fetched_at 是从 day_raw 原样抄来的（源K线抓取
            // 时刻），跟重算没关系，于是判据只在"事件抓得比K线还晚"时碰巧成立：实测配股 09-02
            // 抓入、K线 09-03 抓取，642 只有配股的票一只都没被检出。见 AdjustFactorCalculator
            // 的 computedAt 参数。
            // 老数据的时间戳仍是旧的（偏早），只会让判据更倾向于"要重算"——偏保守，不会漏。
            //
            // RightsIssue 是后加的表，老库可能没有——用 sqlite_master 兜一下，缺表不该让整个判据失效
            bool hasRights;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RightsIssue';";
                hasRights = Convert.ToInt32(probe.ExecuteScalar() ?? 0) > 0;
            }
            string events = hasRights
                ? "SELECT code, MAX(fetched_at) f FROM Dividend GROUP BY code " +
                  "UNION ALL SELECT code, MAX(fetched_at) f FROM RightsIssue GROUP BY code"
                : "SELECT code, MAX(fetched_at) f FROM Dividend GROUP BY code";
            cmd.CommandText = $"""
                WITH adj AS (SELECT code, MAX(fetched_at) f FROM Bar WHERE granularity='day_adj' GROUP BY code),
                     ev  AS ({events})
                SELECT DISTINCT ev.code FROM ev JOIN adj ON adj.code = ev.code
                WHERE ev.f IS NOT NULL AND adj.f IS NOT NULL AND ev.f > adj.f;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) stale.Add(r.GetString(0));
        }
        catch { /* 判据取不到就当没有：宁可少算一轮，也不该让界面上的计数抛异常 */ }
        return stale;
    }

    /// <summary>
    /// 分档资金流还剩多少只没轮到（2026-09-04 新增，界面上要在任务行里显示）——
    /// 返回 (今天还没抓的只数, 其中从没抓过的只数)，取不到返回 null。
    ///
    /// 为什么这两个数都要：这一项跟别的任务不同，**它不可能"today 抓完全市场"**——接口给的是
    /// 120 个交易日滚动窗口，全市场 5900 只按配额每天只能抓百来只，跑满一圈要两个月。所以
    /// "今天还剩 5800 只"是常态、不代表落后；真正说明历史有缺口的是**从没抓过**那个数，
    /// 它归零之后才算铺满了一轮，之后就只是按"最久没抓的先抓"轮换维护。
    ///
    /// 便宜：只查一次 <c>GetLastFetchedAt</c>（一条 GROUP BY），不像 GetPendingAdjRebuildCount
    /// 那样要对 1300 万行的 Bar 表扫四遍——放在 RefreshFailedCodeCount 里不会拖慢它。
    /// </summary>
    public (int Todo, int Never)? GetPendingMoneyFlowCount()
    {
        if (_moneyFlowRepository == null) return null;
        try
        {
            var codes = LocalStockCodes();
            if (codes.Count == 0) return null;
            var lastFetched = _moneyFlowRepository.GetLastFetchedAt();
            var today = DateTime.Today;
            int todo = codes.Count(c => !lastFetched.TryGetValue(c, out var t) || t < today);
            int never = codes.Count(c => !lastFetched.ContainsKey(c));
            return (todo, never);
        }
        catch { return null; }
    }

    /// <summary>本地有多少只个股的回测序列需要重算（不复权比它新，或者压根还没算过）。</summary>
    public int GetPendingAdjRebuildCount()
    {
        try
        {
            var repo = new SqliteBarRepository(_paths.CurrentDb);
            var rawLatest = repo.GetLatestPeriodStartByCode(Granularity.DayRaw);
            var adjLatest = repo.GetLatestPeriodStartByCode(Granularity.DayAdj);
            var rawEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
            var adjEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayAdj);
            // 两头都要比：不复权在**前面**补长了（补历史），回测序列也得整段重算——
            // 复权因子是从最早那天累乘上来的，起点一变整条线都变。
            // 再加一条：除权事件本身变了也要重算，见 CodesWithStaleAdjEvents。
            var staleEvents = CodesWithStaleAdjEvents();
            return rawLatest.Count(kv => !adjLatest.TryGetValue(kv.Key, out var a) || a.Date < kv.Value.Date
                || !adjEarliest.TryGetValue(kv.Key, out var ae)
                || (rawEarliest.TryGetValue(kv.Key, out var re) && ae.Date > re.Date)
                || staleEvents.Contains(kv.Key));
        }
        catch { return 0; }
    }

    /// <summary>
    /// 重算回测序列（day_adj）＝ 不复权 × 本地算的乘法式复权因子。**纯本地计算，不联网。**
    ///
    /// **能增量就增量**：复权因子只在除权日变，没除权的日子把新增那几根乘上现有因子追加即可。
    /// 只有"还没算过"或"新增的日子里有除权"才整段重算（因子变了，全历史都得跟着变）。
    /// 这个区分很要紧——每个交易日都全量重算的话，5781 只 × 2400 根 = 1400 万行每天读写一遍。
    /// </summary>
    public Task<FetchResult> RunRebuildAdjSeriesAsync(
        IProgress<string>? progress, CancellationToken ct = default, int? maxCount = null)
        // 整段是 CPU 密集的同步活（读几百万行、算、写回），扔线程池别占着 UI 线程
        => Task.Run(() =>
    {
        var result = new FetchResult();
        var repo = new SqliteBarRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        var divRepo = new SqliteDividendRepository(_paths.CurrentDb);

        progress?.Report("正在统计哪些股票的回测序列要重算（要扫一遍全库的日线索引，通常几十秒，请稍等）…");
        var rawLatest = repo.GetLatestPeriodStartByCode(Granularity.DayRaw);
        var adjLatest = repo.GetLatestPeriodStartByCode(Granularity.DayAdj);
        var rawEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        var adjEarliest = repo.GetEarliestPeriodStartByCode(Granularity.DayAdj);
        // 两头都要比 + 除权事件变没变，理由见 GetPendingAdjRebuildCount
        var staleEvents = CodesWithStaleAdjEvents();
        var todo = rawLatest
            .Where(kv => !adjLatest.TryGetValue(kv.Key, out var a) || a.Date < kv.Value.Date
                      || !adjEarliest.TryGetValue(kv.Key, out var ae)
                      || (rawEarliest.TryGetValue(kv.Key, out var re) && ae.Date > re.Date)
                      || staleEvents.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (staleEvents.Count > 0)
            progress?.Report($"其中 {staleEvents.Count} 只是因为分红/配股记录有更新——"
                           + "复权因子是拿这些事件算的，事件一变整条序列都得重算。");

        if (todo.Count == 0)
        {
            progress?.Report("回测序列已经跟不复权一样新了，这一轮没什么可做。");
            result.NothingToDo = true;
            return result;
        }

        var batch = maxCount is > 0 ? todo.Take(maxCount.Value).ToList() : todo;
        progress?.Report($"重算回测序列：{todo.Count} 只待算，本轮算 {batch.Count} 只（本地计算，不联网）...");

        // 全库配股一次读进内存（2026-09-01）：全市场配股记录总共几千条，比在循环里逐只查
        // 5500 次便宜得多。没有 RightsIssue 表（老库还没抓过分红）时拿到空字典，行为跟以前一致。
        Dictionary<string, List<Logic.Models.RightsIssueRow>> rightsByCode;
        lock (_dbLock)
        {
            using var rc = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.CurrentDb}");
            rc.Open();
            SqliteSchema.EnsureSchema(rc);
            rightsByCode = SqliteRightsIssueUpsert.LoadAll(rc);
        }
        if (rightsByCode.Count > 0)
            progress?.Report($"已载入 {rightsByCode.Count} 只股票的配股记录（配股是第四类除权，不还原会多出假阴线）。");

        var sw = Stopwatch.StartNew();
        // 本轮重算的时间戳，整批共用——写进 day_adj 的 fetched_at，代表"这条序列是什么时候算的"。
        // CodesWithStaleAdjEvents 拿它跟除权事件的 fetched_at 比，来决定要不要重算（2026-09-04 修）。
        var rebuildStamp = DateTime.Now;
        int done = 0, applied = 0, skipped = 0, badReturns = 0, incremental = 0, rebuilt = 0;
        var skipNotes = new List<string>();

        foreach (var code in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                List<Bar> raw;
                List<Logic.Models.DividendRow> divs;
                lock (_dbLock)
                {
                    raw = repo.Query(code, Granularity.DayRaw);
                    divs = divRepo.GetByCode(code);
                }
                if (raw.Count < 2) continue;

                // 分红送转 + 配股，一起构成这只票的除权事件序列。
                // 配股是第四类除权（2026-09-01 补上）：漏了它，除权日的真实跳空会被当成真实下跌，
                // 复权序列上凭空多一根阴线——中信证券 2022-01 那次 −5.72%，10配3 的量级到 −15%，
                // 且集中在银行/券商。同一天既送转又配股的，下面按 ExDate 分组后自然合并处理。
                var events = divs
                    .Where(d => d.ExDate.HasValue)
                    .Select(d => new AdjustFactorCalculator.ExDividend(
                        d.ExDate!.Value,
                        (d.BonusShares + d.TransferShares) / 10.0,
                        d.DividendYuan / 10.0))
                    .Concat((rightsByCode.TryGetValue(code, out var rl) ? rl : [])
                        .Where(r => r.ExDate.HasValue)
                        .Select(r => new AdjustFactorCalculator.ExDividend(
                            r.ExDate!.Value, 0, 0,
                            RightsRatio: r.SharesPer10 / 10.0,
                            RightsPrice: r.Price)))
                    .Where(e => !e.IsEmpty)
                    .OrderBy(e => e.ExDate)
                    .ToList();

                // ── 能只补增量就别整段重算 ────────────────────────────────────────
                // 复权因子只在除权日变。没除权的日子，新增那几根乘上现有因子追加就行——
                // 每个交易日都全量重算的话，5781 只 × 2400 根 = 1400 万行每天读写一遍，纯浪费。
                DateTime? adjLast = adjLatest.TryGetValue(code, out var al) ? al.Date : null;
                var newDays = adjLast is { } last ? raw.Where(b => b.PeriodStart.Date > last).ToList() : raw;
                bool exInNewDays = adjLast is { } lastEx
                    && events.Any(e => e.ExDate.Date > lastEx && e.ExDate.Date <= raw[^1].PeriodStart.Date);

                // 开头也对得上才敢走增量：raw 要是在**前面**补长了（补历史），因子基准就变了，
                // 只追加尾巴会让新旧两段落在不同基准上，接缝处凭空多出一个假跳空。
                bool headMatches = adjEarliest.TryGetValue(code, out var ae0)
                                && ae0.Date <= raw[0].PeriodStart.Date;
                // 除权事件变过的，一律整段重算：因子是从最早那天累乘上来的，
                // 中间插进一条新的除权记录，它之后的每一根都得跟着变，只追加尾巴是错的。
                if (adjLast is not null && headMatches && !exInNewDays && !staleEvents.Contains(code)
                    && newDays.Count > 0 && newDays.Count < raw.Count)
                {
                    // 增量：从"最后一根已算好的"反推当前因子，直接乘上去
                    List<Bar> tail;
                    lock (_dbLock) tail = repo.Query(code, Granularity.DayAdj, adjLast, adjLast);
                    var rawAtLast = raw.LastOrDefault(b => b.PeriodStart.Date == adjLast);
                    if (tail.Count > 0 && rawAtLast is { Close: > 0 })
                    {
                        double factor = tail[^1].Close / rawAtLast.Close;
                        var appended = newDays.Select(b => new Bar
                        {
                            Code = b.Code, Granularity = Granularity.DayAdj, PeriodStart = b.PeriodStart,
                            Open = b.Open * factor, Close = b.Close * factor,
                            High = b.High * factor, Low = b.Low * factor,
                            Volume = b.Volume, Amount = b.Amount, Turnover = b.Turnover,
                            // 跟整段重算一致：盖"算出来的时刻"，不是源K线的抓取时刻。
                            // 两条路径必须用同一个语义，否则走过增量的票时间戳偏旧，
                            // CodesWithStaleAdjEvents 会把它们误判成"事件比序列新"、反复重算。
                            FetchedAt = rebuildStamp,
                        }).ToList();
                        lock (_dbLock) repo.InsertOrIgnore(appended);
                        incremental++;
                        if (++done % 500 == 0)
                            progress?.Report($"  处理中 {done}/{batch.Count}（增量 {incremental} 只、整段重算 {rebuilt} 只），"
                                           + $"用时 {FormatElapsed(sw.Elapsed)}");
                        continue;
                    }
                }

                // 整段重算：没算过、或者新增的日子里有除权（因子变了，全历史都要跟着变）
                var adj = AdjustFactorCalculator.BuildAdjusted(code, raw, events, out var report, rebuildStamp);
                badReturns += AdjustFactorCalculator.VerifyReturns(raw, adj);
                applied += report.Applied;
                skipped += report.Skipped;
                rebuilt++;
                if (skipNotes.Count < 30) skipNotes.AddRange(report.Notes.Take(30 - skipNotes.Count));

                lock (_dbLock)
                {
                    repo.DeleteByCode(code, Granularity.DayAdj);   // 因子一变全历史都变，整段重写
                    repo.InsertOrIgnore(adj);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Errors.Add($"{code} 重算回测序列失败：{ex.Message}"); }

            if (++done % 500 == 0)
                progress?.Report($"  处理中 {done}/{batch.Count}（增量 {incremental} 只、整段重算 {rebuilt} 只），"
                               + $"用时 {FormatElapsed(sw.Elapsed)}");
        }

        foreach (var n in skipNotes) progress?.Report("  ⚠ " + n);
        int left = GetPendingAdjRebuildCount();
        progress?.Report($"回测序列更新完成：{done} 只（其中 {incremental} 只只追加了新K线、{rebuilt} 只整段重算），"
                       + $"应用除权 {applied} 次、按价格校验剔除可疑记录 {skipped} 条"
                       + (badReturns > 0 ? $"，⚠ 有 {badReturns} 天的收益率对不上真实值（算法可能被改坏了）" : "，收益率自检全部通过")
                       + $"，还剩 {left} 只，用时 {FormatElapsed(sw.Elapsed)}。");
        return result;
    }, ct);

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
        string gran = Granularity.DayHfq)
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
                    toFetch.Count, () => Interlocked.Increment(ref done), sw, abortCts.Token, gran);

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
            var info = currentRepo.GetLatestBarInfo(code, gran);
            if (info == null) return (end.AddYears(-lookbackYears), end);
            if (info.Value.PeriodStart.Date < end.Date) return (info.Value.PeriodStart.AddDays(1), end);
            return (IsConfirmedFinal(info.Value.FetchedAt, end) ? end.AddDays(1) : end, end);
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
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        if (_delistedListProvider == null)
        {
            progress?.Report("（未配置退市名单数据源，跳过退市股）");
            return new List<string>();
        }

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
            var (s, e) = YearGapFor(r.Code, earliestByCode, rangeStart, stockEnd);
            return ProcessOneStockAsync(r.Code, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                candidates.Count, () => Interlocked.Increment(ref done), sw, ct);
        }));
        progress?.Report($"退市股日K补齐完成（{candidates.Count} 只）。");

        // 退市股的后复权同样要补——回测股票池里少了它们就等于幸存者偏差没修干净
        await FetchHfqBarsAsync(source, candidates.Select(r => r.Code).ToList(),
            code =>
            {
                var row = candidates.First(x => x.Code == code);
                var stockEnd = row.DelistDate is { } dd && dd < rangeEnd ? dd : rangeEnd;
                return YearGapFor(code, earliestHfq, rangeStart, stockEnd);
            },
            currentRepo, errors, failedCodes, stats, progress, sw, ct, "退市股");

        // 不复权同理——见上面那处的注释
        var earliestRawD = currentRepo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        await FetchHfqBarsAsync(source, candidates.Select(r => r.Code).ToList(),
            code =>
            {
                var row = candidates.First(x => x.Code == code);
                var stockEnd = row.DelistDate is { } dd && dd < rangeEnd ? dd : rangeEnd;
                return YearGapFor(code, earliestRawD, rangeStart, stockEnd);
            },
            currentRepo, errors, failedCodes, stats, progress, sw, ct, "退市股", Granularity.DayRaw);

        return candidates.Select(r => r.Code).ToList();
    }

    /// <summary>
    /// 按年份补历史时，算出某个标的在目标年里真正需要请求的 [start, end]——本地最早日已早于年初就返回
    /// 一个空区间（start &gt; end，<see cref="ProcessOneStockAsync"/> 会直接记 Skip、不发请求）；最早日
    /// 落在目标年内就只补"年初 → 最早日前一天"；最早日在年末之后（或本地没有该标的）就抓一整年。
    /// 依赖"本地历史是连续的"这一前提——增量抓取永远是从水位线往后连续推进的，所以只需要看最早日一个点。
    /// </summary>
    private static (DateTime Start, DateTime End) YearGapFor(
        string code, Dictionary<string, DateTime> earliestByCode, DateTime yearStart, DateTime yearEnd,
        IReadOnlyCollection<DateTime>? tradingDays = null)
    {
        if (!earliestByCode.TryGetValue(code, out var earliest)) return (yearStart, yearEnd);
        if (earliest.Date <= yearStart.Date) return (yearEnd.AddDays(1), yearEnd);   // 空区间=跳过
        if (earliest.Date <= yearEnd.Date)
        {
            var gapEnd = earliest.AddDays(-1);
            // 缺口里一个交易日都没有 → 再请求也只会拿回空数据，直接跳过。典型情形：区间起点写的是
            // 2016-01-01（自然年首日），而 A 股 2016 年第一个交易日是 01-04，中间只有元旦假期——
            // 不判这一下的话，几千只"其实已经补齐"的股票每只都会白发一次请求（2026-07-30 实测：
            // 一次区间重跑光在这上面就烧掉 19 分钟、1150 个请求，还没轮到后面的阶段）。
            if (tradingDays != null && !tradingDays.Any(d => d.Date >= yearStart.Date && d.Date <= gapEnd.Date))
                return (yearEnd.AddDays(1), yearEnd);
            return (yearStart, gapEnd); // 只补前面的缺口
        }
        return (yearStart, yearEnd);
    }

    /// <summary>本地已知的交易日集合（取大盘指数的日K日期）——给 <see cref="YearGapFor"/> 判断
    /// "这段缺口里到底有没有交易日"用。取不到就返回 null，调用方退回到不判交易日的老行为。</summary>
    private static List<DateTime>? LocalTradingDays(SqliteBarRepository repo)
    {
        try
        {
            var bars = repo.Query(MarketIndexCatalog.All[0].Symbol, Granularity.Day);
            return bars.Count > 0 ? bars.Select(b => b.PeriodStart.Date).ToList() : null;
        }
        catch { return null; }
    }

    /// <summary>按年份补 ETF 日K——ETF列表仍要联网取一次（本地 StockMeta 里的 type=etf 也可以，但列表接口
    /// 便宜且能顺带发现新ETF），窗口计算与个股共用 <see cref="YearGapFor"/>。取不到列表就跳过ETF、不算失败。</summary>
    private async Task<List<string>> FetchEtfBarsForYearAsync(
        NamedBarSource source, DateTime yearStart, DateTime yearEnd, Dictionary<string, DateTime> earliestByCode,
        SqliteBarRepository currentRepo, ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes,
        FetchStats stats, IProgress<string>? progress, Stopwatch sw, CancellationToken ct,
        IReadOnlyCollection<DateTime>? tradingDays = null)
    {
        if (_etfListProvider == null) return new List<string>();
        List<StockListEntry> etfs;
        try { etfs = await _etfListProvider.GetAllStocksAsync(progress, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"获取ETF列表失败（跳过ETF）：{ex.Message}"); return new List<string>(); }
        if (etfs.Count == 0) { progress?.Report("ETF列表为空（接口可能不可达/被限流），本轮跳过ETF。"); return new List<string>(); }

        progress?.Report($"开始补 {yearStart:yyyy}~{yearEnd:yyyy} 年 ETF 日K（{etfs.Count} 只）...");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, etfs.Select(e => (e.Code, e.Name)), SqliteStockMetaUpsert.TypeEtf);
        int done = 0;
        await Task.WhenAll(etfs.Select(etf =>
        {
            var (s, e) = YearGapFor(etf.Code, earliestByCode, yearStart, yearEnd, tradingDays);
            return ProcessOneStockAsync(etf.Code, source, s, e, currentRepo, errors, failedCodes, stats, progress,
                etfs.Count, () => Interlocked.Increment(ref done), sw, ct);
        }));
        progress?.Report($"ETF 日K补齐完成（{etfs.Count} 只）。");
        return etfs.Select(e => e.Code).ToList();
    }

    /// <summary>
    /// 按指定区间补资金净流入（2026-07-29新增，给"拉取指定年份"用）——跟K线同样的"只补缺口"语义：
    /// 本地最早一行已早于区间起点就跳过，落在区间内就只补前面那段，没有就抓整个区间。写入全部走
    /// InsertOrIgnore（历史行是既成事实；区间含今天时今天那行走 Upsert，跟主流程一致）。失败逐只记入
    /// <see cref="Manifest.FailedNetInflowCodes"/>，可用"重新拉取失败股票"重试。
    /// </summary>
    private async Task FetchNetInflowRangeAsync(
        IReadOnlyList<string> codes, DateTime rangeStart, DateTime rangeEnd, IProgress<string>? progress, CancellationToken ct)
    {
        var failedNetInflowCodes = new ConcurrentBag<string>();
        try
        {
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
            manifest.FailedNetInflowCodes = ComputeUpdatedFailedCodes(manifest.FailedNetInflowCodes, codes, failedNetInflowCodes);
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

        var left = GetFailedRetrySummary();
        progress?.Report(left.Any
            ? $"仍有待重试：{left.Describe()}——可以再点一次这个按钮"
            : "失败名单已全部清零，没有遗留项目");
    }

    private async Task<FetchResult> RunRetryFailedInternalAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法重新拉取，请先执行一次\"拉取全部\"");

        var manifest = _manifestStore.Load();
        var failedCodesList = manifest.FailedCodes;
        var failedMarketCapCodes = manifest.FailedMarketCapCodes;
        var failedNetInflowCodes = manifest.FailedNetInflowCodes;
        var failedIndexConsCodes = manifest.FailedIndexConsCodes;
        var failedIndexWeightCodes = manifest.FailedIndexWeightCodes;
        var failedShareholderCodes = manifest.FailedShareholderCodes;
        var failedDividendCodes = manifest.FailedDividendCodes;
        // 这一份不是"失败"名单，是体检出来的"当天日线还缺着"名单（见 CheckLatestDayCoverage）——
        // 用户角度它跟失败一样都是"数据没到位、要再抓一次"，所以并进同一个按钮里重试。
        var missingDayCodes = manifest.MissingDayCodes;
        var missingDayDate = manifest.MissingDayDate;

        if (failedCodesList.Count == 0 && failedMarketCapCodes.Count == 0 && failedNetInflowCodes.Count == 0
            && failedIndexConsCodes.Count == 0 && failedIndexWeightCodes.Count == 0 && failedShareholderCodes.Count == 0
            && failedDividendCodes.Count == 0 && missingDayCodes.Count == 0)
        {
            progress?.Report("目前没有记录到抓取失败或缺当天数据的股票，不需要重试");
            return new FetchResult();
        }

        // 逐类重试，每类做完记一条"干了什么"——最后统一汇总。原来没有这个汇总，跑完只在末尾留下
        // 一句"K线没有失败的股票需要重试"，看起来像整个操作什么都没做（用户 2026-08-19 反馈），
        // 实际上市值和资金流已经重试完并清零了。
        var done = new List<string>();

        if (failedMarketCapCodes.Count > 0)
        {
            // 市值是整轮扫描，不是逐只重试——名单里那一大批代码只代表"有一轮要重来"，日志要讲清楚，
            // 否则"重试 5544 只"和后面"写入 5544 条"看起来像两件事。
            progress?.Report($"流通市值：整轮重新扫描（上次整轮失败，名单里那 {failedMarketCapCodes.Count} 个代码是当时那批的全体，"
                           + "不是逐只失败——市值一次请求拿回全市场）");
            await FetchMarketCapAsync(source, failedMarketCapCodes, progress, ct);
            done.Add("流通市值 1 轮");
        }

        if (failedNetInflowCodes.Count > 0)
        {
            await FetchNetInflowAsync(failedNetInflowCodes, DateTime.Today, exactDayOnly: false, progress, ct);
            done.Add($"主力净流入 {failedNetInflowCodes.Count} 只");
        }

        // 指数成分/权重的失败重试（2026-07-16新增）——跟市值/资金流一样，在K线重试之前处理，
        // 各自用自己的失败名单精确重试，可反复点击直到清零（见 RetryIndexAsync）。
        if (failedIndexConsCodes.Count > 0 || failedIndexWeightCodes.Count > 0)
        {
            await RetryIndexAsync(failedIndexConsCodes, failedIndexWeightCodes, progress, ct);
            if (failedIndexConsCodes.Count > 0) done.Add($"指数成分 {failedIndexConsCodes.Count} 个");
            if (failedIndexWeightCodes.Count > 0) done.Add($"指数权重 {failedIndexWeightCodes.Count} 个");
        }

        // 股东数据的失败重试（2026-07-16新增）——逐只精确重试，见 RetryShareholderAsync。
        if (failedShareholderCodes.Count > 0)
        {
            await RetryShareholderAsync(failedShareholderCodes, progress, ct);
            done.Add($"股东数据 {failedShareholderCodes.Count} 只");
        }

        // 分红送配的失败重试（2026-07-31新增）——逐只精确重试，见 RetryDividendAsync。
        if (failedDividendCodes.Count > 0)
        {
            await RetryDividendAsync(failedDividendCodes, progress, ct);
            done.Add($"分红送配 {failedDividendCodes.Count} 只");
        }

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();
        var today = DateTime.Today;
        var sw = Stopwatch.StartNew();

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();

        // ── 当天日线缺失的重补（2026-08-21新增）──
        // 这批股票的请求上一轮**根本没失败**，是数据源盘后还没更新到它们（见 Manifest.MissingDayCodes）。
        // 窗口取"缺的那个交易日 → 今天"；前复权走 ProcessOneStockAsync，后复权走 FetchHfqBarsAsync
        // （它自己按 day_hfq 的水位线算缺口）——已经补齐的那条线会在水位线判定里跳过、不发请求。
        if (missingDayCodes.Count > 0 && missingDayDate.HasValue)
        {
            var missStats = new FetchStats();
            int missDone = 0;
            progress?.Report($"补 {missingDayDate.Value:yyyy-MM-dd} 还缺的个股日线，共 {missingDayCodes.Count} 只"
                           + $"（上一轮不是失败，是数据源当时还没出这些股票的当天数据），数据源：{source.Name}");
            await Task.WhenAll(missingDayCodes.Select(code =>
                ProcessOneStockAsync(code, source, missingDayDate.Value, today, currentRepo,
                    errors, failedCodes, missStats, progress, missingDayCodes.Count,
                    () => Interlocked.Increment(ref missDone), sw, ct)));
            await FetchHfqBarsAsync(source, missingDayCodes,
                code => HfqWatermarkWindow(currentRepo, code, today, DefaultLookbackYears),
                currentRepo, errors, failedCodes, missStats, progress, sw, ct);
            await FetchHfqBarsAsync(source, missingDayCodes,
                code => HfqWatermarkWindow(currentRepo, code, today, DefaultLookbackYears, Granularity.DayRaw),
                currentRepo, errors, failedCodes, missStats, progress, sw, ct, gran: Granularity.DayRaw);
            progress?.Report($"当天日线重补汇总：{missStats.Summarize()}");
            done.Add($"{missingDayDate.Value:MM-dd}日线 {missingDayCodes.Count} 只");
        }

        // ── 全库体检查出来的历史空洞（2026-09-02 新增）──
        await FillAuditedGapsAsync(source, currentRepo, errors, failedCodes, done, progress, sw, ct);

        if (failedCodesList.Count == 0)
        {
            // K线名单是空的，但上面几类可能已经重试完了——只报"K线没有失败"会让人以为整轮什么都没干。
            done.Add("K线 0 只（名单本来就是空的）");
            // 体检要跑：上面刚补过的话名单得重建，没补过也顺手确认一次当天覆盖情况。
            var emptyBarResult = FinishFetchRun(errors, "重新拉取失败股票", missingDayCodes, failedCodes,
                progress, checkDayCoverage: true);
            ReportRetrySummary(done, progress);
            return emptyBarResult;
        }

        progress?.Report($"重新拉取上次失败的K线，共 {failedCodesList.Count} 只，数据源：{source.Name}");
        int completed = 0;
        var tasks = failedCodesList.Select(code =>
        {
            // 失败的股票水位线可能是很久以前的（如果一直失败），也可能压根没有（第一次就失败）——
            // 后一种情况用跟"拉取全部"默认回看年数一样的3年兜底，这里没有单独的"回看年数"输入框。
            // "今天"这一天同样要看是不是收盘后确认的（跟RunFetchAllInternalAsync同样的逻辑）。
            DateTime start;
            lock (_dbLock)
            {
                var info = currentRepo.GetLatestBarInfo(code, Granularity.Day);
                if (info == null)
                    start = today.AddYears(-3);
                else if (info.Value.PeriodStart.Date < today.Date)
                    start = info.Value.PeriodStart.AddDays(1);
                else
                    start = IsConfirmedFinal(info.Value.FetchedAt, today) ? today.AddDays(1) : today;
            }
            return ProcessOneStockAsync(code, source, start, today, currentRepo, errors, failedCodes, stats, progress, failedCodesList.Count, () => Interlocked.Increment(ref completed), sw, ct);
        });
        await Task.WhenAll(tasks);

        progress?.Report($"K线本轮汇总：{stats.Summarize()}");
        done.Add($"K线 {failedCodesList.Count} 只（其中 {failedCodes.Count} 只仍失败）");
        // attempted 要把"当天缺失"那批也算上——它们这轮也真的抓过了，成功的就该从失败名单里移出。
        var attemptedThisRetry = failedCodesList.Concat(missingDayCodes).Distinct(StringComparer.Ordinal).ToList();
        var retryResult = FinishFetchRun(errors, "重新拉取失败股票", attemptedThisRetry, failedCodes,
            progress, checkDayCoverage: true);
        ReportRetrySummary(done, progress);
        return retryResult;
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
                if (updated > 0)
                {
                    Interlocked.Add(ref updatedRows, updated);
                    // 周/月线的 amount/turnover 是日线的求和（见 BarAggregator），日线补上后
                    // 要整体重算覆盖，跟正常抓取后的聚合是同一段逻辑。
                    var allDayBars = currentRepo.Query(t.Code, Granularity.Day);
                    SqliteBarUpsert.Upsert(_paths.CurrentDb, BarAggregator.ToWeekly(allDayBars));
                    SqliteBarUpsert.Upsert(_paths.CurrentDb, BarAggregator.ToMonthly(allDayBars));
                }
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
                var info = currentRepo.GetLatestBarInfo(code, Granularity.Day);
                DateTime start;
                if (info == null)
                    start = end.AddYears(-lookbackYears);
                else if (info.Value.PeriodStart.Date < end.Date)
                    start = info.Value.PeriodStart.AddDays(1);
                else
                    start = IsConfirmedFinal(info.Value.FetchedAt, end) ? end.AddDays(1) : end;
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
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        progress?.Report($"正在抓取大盘指数K线（{MarketIndexCatalog.All.Count} 个：{string.Join("、", MarketIndexCatalog.All.Select(i => i.Name))}）...");
        // 指数名称写进 StockMeta（type=index）——让"查询"页能按名称/代码搜到指数（不影响个股选股，选股扫的是6位纯数字）。
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, MarketIndexCatalog.All.Select(i => (i.Symbol, i.Name)), SqliteStockMetaUpsert.TypeIndex);
        int completed = 0;
        foreach (var (symbol, _) in MarketIndexCatalog.All)
        {
            // 跟"拉取全部"的个股水位线同一套规则：没抓过的从回看窗口起点开始（数据源只会返回
            // 指数实际存在的日期），抓过的从上次的下一天继续，"今天"要看是否已收盘后确认。
            DateTime start;
            lock (_dbLock)
            {
                var info = currentRepo.GetLatestBarInfo(symbol, Granularity.Day);
                if (info == null)
                    start = end.AddYears(-lookbackYears);
                else if (info.Value.PeriodStart.Date < end.Date)
                    start = info.Value.PeriodStart.AddDays(1);
                else
                    start = IsConfirmedFinal(info.Value.FetchedAt, end) ? end.AddDays(1) : end;
            }
            await ProcessOneStockAsync(symbol, source, start, end, currentRepo, errors, failedCodes, stats, progress, MarketIndexCatalog.All.Count, () => Interlocked.Increment(ref completed), sw, ct);
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
        string granularity = Granularity.Day, bool overwrite = false, ConcurrentBag<string>? driftedCodes = null)
    {
        ct.ThrowIfCancellationRequested();
        bool isHfq = granularity == Granularity.DayHfq;

        // This is the "is it already up to date locally" check the caller computed start/end
        // from — a stock whose watermark is already >= end never even reaches the network call,
        // which is what makes 拉取全部/拉取当天 safe to re-run without re-downloading everything.
        // See FetchStats.Summarize(), reported once at the end of the run, for visible proof of
        // how many stocks this run actually skipped vs fetched vs failed.
        if (start.Date > end.Date) { stats.Skip(); ReportCompareProgress(reportCompleted(), totalCount, progress, sw); return; }

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
            ReportCompareProgress(reportCompleted(), totalCount, progress, sw);
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
                    if (toInsert.Count > 0) currentRepo.InsertOrIgnore(toInsert);
                    if (toOverwrite.Count > 0)
                    {
                        SqliteBarUpsert.Upsert(_paths.CurrentDb, toOverwrite);
                        driftedCodes!.Add(code);
                    }
                }

                if (todaysBars.Count > 0) SqliteBarUpsert.Upsert(_paths.CurrentDb, todaysBars);

                // Week/month are derived, not raw facts — recompute over the code's FULL day
                // history (not just the increment) so the still-open current week/month stays
                // correct, then upsert (overwrite) rather than insert-or-ignore.
                // 后复权那一路跳过：周/月线只服务于界面看盘（用前复权），回测只吃日线。
                if (!isHfq)
                {
                    var allDayBars = currentRepo.Query(code, Granularity.Day);
                    var weekBars = BarAggregator.ToWeekly(allDayBars);
                    var monthBars = BarAggregator.ToMonthly(allDayBars);
                    SqliteBarUpsert.Upsert(_paths.CurrentDb, weekBars);
                    SqliteBarUpsert.Upsert(_paths.CurrentDb, monthBars);
                }
            }
        }
        else
        {
            // Fetch succeeded but returned nothing — e.g. the requested range is entirely a
            // weekend/holiday with no trading. Not an error, not a local-DB skip either.
            stats.FetchedButEmpty();
        }

        ReportCompareProgress(reportCompleted(), totalCount, progress, sw);
    }

    /// <summary>"正在对比数据"这一条只是给用户看整体进度用的粗粒度心跳（跳过的/真的发了请求的
    /// 都算在内），跟"正在抓取 {code}"那条不是一回事——那条才是"这只股票确实发了网络请求"的
    /// 精确记录，见 ProcessOneStockAsync。报告间隔沿用之前的"每5只报一次"（不是每50），这样日志
    /// 能持续往前走、看得出运行中还活着——单只股票在限速器的重试/熔断下最长可能要~48秒（见
    /// RateLimiter），中间隔久一点是正常的，不是卡住。</summary>
    private static void ReportCompareProgress(int done, int totalCount, IProgress<string>? progress, Stopwatch sw)
    {
        if (done % 5 == 0 || done == totalCount)
            progress?.Report($"正在对比数据 ({done}/{totalCount})，已用时 {FormatElapsed(sw.Elapsed)}");
    }

    /// <summary>
    /// 流通市值——默认走 <see cref="SinaListMarketCapFetcher"/>（2026-07-08起），本质是让新浪的
    /// 全市场股票列表扫描"顺便"带出每只股票的流通市值字段，不再逐只单独发请求（早期版本是逐只查
    /// 东方财富/腾讯的单股行情接口，见 EastMoneyMarketCapFetcher/TencentMarketCapFetcher 的类
    /// 注释，两者都还在代码里，只是不再是默认实现）。不受用户选的K线数据源（source参数）影响，
    /// "拉取全部"和"拉取当天"都会跑一遍——"拉取当天"因此也会完整扫一遍全市场列表（只为了刷新
    /// 市值，不影响它K线只抓本地已知股票的行为），用户已确认接受这个额外耗时（2026-07-08）。
    /// 失败按非致命处理——查不到就跳过，不影响本轮K线抓取的其余部分（市值数据缺失只会让
    /// MidCapPullbackAnalysisEngine 的条件4被当作"缺数据"跳过，不会导致程序崩溃或影响其他方法）。写入用
    /// Upsert（主键是 code+metric_key+as_of_date）——同一天内如果跑了不止一次，后面这次会覆盖
    /// 前面那次，只认最后一次抓到的值，不是"当天已经有了就跳过"（比如某天先在盘中跑过一次、收盘
    /// 后又跑了一次，库里最终留下的是收盘后那次更准的值，不会被盘中那次锁住）。
    ///
    /// **as_of_date 记的是"这个值属于哪个交易日"，不是"哪天跑的抓取"（2026-08-04改）**：市值接口只给
    /// "当下"的快照、不带日期，而快照的基准价在盘前/周末/节假日是**上一个交易日的收盘**——以前这里一律
    /// 写 <c>DateTime.Today</c>，于是周六跑一次就会凭空多出一行"周六的市值"（值其实是周五收盘），
    /// 盘前跑则把上一交易日的值记到今天名下。实测过的错位样本：2026-07-11(周六)/2026-08-01(周六) 的行
    /// 是 07-10/07-31 的收盘值，2026-07-23 08:02 那行是 07-22 的收盘值。现在改成
    /// <see cref="ResolveMarketCapAsOfDateAsync"/> 解析出的交易日。（当时这么改还顺带修好了按
    /// <c>as_of_date LIKE 'D%'</c> 导出每日增量时周末市值行漏出增量包的问题；那套 GitHub 分发的
    /// 增量导出已于 2026-08-21 删除，交易日对齐本身仍然是对的。）
    /// 注意这跟"补指定历史日"的 <c>date</c> 参数无关——接口给不出往年的市值，无论补哪一天，
    /// 市值刷的都是"当下"那个交易日的快照（补往年的 RunFetchYearAsync 干脆整段跳过市值）。
    ///
    /// 失败追踪（2026-07-09新增）：这是整轮扫描性质的操作，不是逐只单独查，所以"失败"粒度是
    /// "这一轮扫描失败了"——扫描失败时把本轮请求的 <paramref name="codes"/> 全部记进
    /// <see cref="Manifest.FailedMarketCapCodes"/>；扫描成功时把这些代码全部移出（某只股票本来
    /// 就没有市值数据不算失败）。见 Manifest.FailedMarketCapCodes 的类注释。
    ///
    /// 新股发现（2026-07-10新增）：<see cref="SinaListMarketCapFetcher"/>本来就要扫一遍全市场
    /// 列表，天然会看到<paramref name="codes"/>里没有的代码（本地股票表还不知道的新股/新上市）——
    /// 顺手把这些也写进本地股票表（见 <see cref="MarketCapFetchResult.NewlyDiscoveredCodes"/>），
    /// 不需要额外的网络请求。返回值供调用方（尤其是"拉取当天"）把这些新股也纳入本轮的K线/资金
    /// 净流入抓取——不过"拉取当天"性质上只抓一天，新股不会像"拉取全部"那样自动回补历史，仍然需要
    /// 跑一次"拉取全部"才能补齐历史（这个方法只保证新股"从现在起不再被漏掉"）。
    /// </summary>
    /// <returns>
    /// <c>NewCodes</c>＝这次扫描顺带发现的、本地还没有的股票；
    /// <c>RosterRefreshed</c>＝这次扫描是否已经把**全市场名册**整体刷新过了
    /// （2026-09-02 新增：新浪的列表接口一次同时给名册和市值，调用方据此不必再单独扫一遍列表；
    /// 逐只查询的市值实现给不出全市场名单，这里就是 false，调用方照旧自己去取名册）。
    /// </returns>
    private async Task<(List<(string Code, string Name)> NewCodes, bool RosterRefreshed)> FetchMarketCapAsync(
        NamedBarSource source, IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        List<string> failedThisRun;
        var newlyDiscovered = new List<(string Code, string Name)>();
        bool rosterRefreshed = false;
        try
        {
            progress?.Report("正在扫描全市场股票列表以刷新流通市值（顺带发现新股），会比较慢...");
            var result = await _marketCapFetcher.GetMarketCapsAsync(codes, progress, ct);
            var fetchedAt = DateTime.Now;
            var asOfDate = await ResolveMarketCapAsOfDateAsync(source, result.QuotesAreLive, progress, ct);
            var metrics = result.Entries.Select(e => new FundamentalMetric
            {
                Code = e.Code,
                MetricKey = MetricKeys.CirculatingMarketCap,
                AsOfDate = asOfDate,
                Value = e.CirculatingMarketCap,
                Source = _marketCapFetcher.GetType().Name,
                FetchedAt = fetchedAt,
            });
            _fundamentalRepository.Upsert(metrics);
            progress?.Report($"流通市值写入完成，共 {result.Entries.Count} 条，归到交易日 {asOfDate:yyyy-MM-dd}");

            // 扫描把全市场的代码+名称都带回来了（新浪列表实现）——直接整体刷新名册：
            // 新股顺带入库、改过名的也跟着更新，**省掉单独再扫一遍列表接口的 ~55 个请求**
            // （2026-09-02，见 MarketCapFetchResult.AllStocks）。
            if (result.AllStocks is { Count: > 0 } allStocks)
            {
                SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, allStocks);
                rosterRefreshed = true;
            }

            if (result.NewlyDiscoveredCodes.Count > 0)
            {
                // 名册没被整体刷新时（逐只查询的市值实现）才需要单独把新股写进去
                if (!rosterRefreshed) SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, result.NewlyDiscoveredCodes);
                newlyDiscovered = result.NewlyDiscoveredCodes;
                progress?.Report($"发现本地股票表里没有的新股票 {newlyDiscovered.Count} 只，已加入本地列表");
            }

            failedThisRun = new List<string>(); // 整轮扫描成功——不管每只股票是否真的有市值数据，都不算失败
        }
        catch (OperationCanceledException)
        {
            throw; // 用户点了"停止"
        }
        catch (Exception ex)
        {
            progress?.Report($"获取流通市值失败（不影响K线抓取）：{ex.Message}");
            failedThisRun = codes.ToList(); // 整轮扫描失败——保守地把这次请求的代码全部记为失败
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedMarketCapCodes = ComputeUpdatedFailedCodes(manifest.FailedMarketCapCodes, codes, failedThisRun);
            _manifestStore.Save(manifest);
        }

        return (newlyDiscovered, rosterRefreshed);
    }

    /// <summary>
    /// 解析"这批流通市值快照属于哪个交易日"（2026-08-04新增，取代原先一律写 <c>DateTime.Today</c>）。
    ///
    /// 市值接口只给"当下"的快照且不带日期，所以日期得自己定。两步：
    /// 1. <paramref name="quotesAreLive"/>==true（扫描时全市场大多数股票都有最新价 → 今天已开盘，见
    ///    <see cref="MarketCapFetchResult.QuotesAreLive"/>）→ 值就是当日行情，记今天。**盘中跑属于这一档**：
    ///    日期是对的，只是值还不是收盘价，收盘后再跑一次就会被更准的值覆盖（Upsert 同 as_of_date 覆盖）。
    /// 2. 否则（盘前、周末、节假日，或该实现给不出这个信号）→ 快照的基准价是**上一个交易日的收盘**，
    ///    于是去问数据源要上证指数最近几根日线，最新那根的日期就是那个交易日。用指数是因为它不停牌、
    ///    不退市，永远有最新一根；这样就**不需要在本地维护A股节假日日历**，国庆/春节这种连休也天然处理对
    ///    （实测：2026-08-04 09:07 盘前请求返回的最新日线是 2026-08-03，正是上一个交易日）。
    ///
    /// 刻意用当前选定的 <paramref name="source"/> 而不是固定某一家：本轮K线马上就要用它，能用才跑到这里，
    /// 不会因为"锚用了另一家、而那家在用户环境里连不上"凭空多一个故障点。
    ///
    /// 锚请求失败/返回空时回退到今天并在日志里说明——市值本身已经抓到了，不值得为了日期把整步判失败；
    /// 回退的后果就是退回改动前的老行为（可能多出一行非交易日的市值），不会丢数据。
    /// </summary>
    private async Task<DateTime> ResolveMarketCapAsOfDateAsync(
        NamedBarSource source, bool? quotesAreLive, IProgress<string>? progress, CancellationToken ct)
    {
        var today = DateTime.Today;
        if (quotesAreLive == true) return today;

        try
        {
            // datelen 由 start/end 跨度推出来，给 15 天足够覆盖春节这种最长连休。
            var (_, bars) = await source.Fetcher.FetchAsync(
                MarketIndexCatalog.ShanghaiCompositeSymbol, Granularity.Day, today.AddDays(-15), today, ct);
            var latest = bars.Count > 0 ? bars[^1].PeriodStart.Date : default;
            if (latest != default && latest <= today)
            {
                if (latest != today)
                    progress?.Report($"当前不在交易时段，流通市值快照归到上一个交易日 {latest:yyyy-MM-dd}");
                return latest;
            }
            progress?.Report($"未能从上证指数日线判断最新交易日（返回 {bars.Count} 根），流通市值按今天记");
        }
        catch (OperationCanceledException)
        {
            throw; // 用户点了"停止"
        }
        catch (Exception ex)
        {
            progress?.Report($"判断最新交易日失败（不影响市值抓取，按今天记）：{ex.Message}");
        }
        return today;
    }

    /// <summary>
    /// 主力净流入——逐只股票查东方财富的个股资金流向历史接口（fflow/daykline/get，跟K线用的
    /// kline/get是完全不同的数据集/接口），按自己独立的水位线（NetInflow表里该股票已有的最新
    /// 日期）增量补齐到本轮截止日；表里还没有记录的股票（新股票/第一次跑）从截止日往前回溯
    /// NetInflowInitialLookbackDays 天开始。跟流通市值一样不受用户选的K线数据源影响，"拉取全部"
    /// 和"拉取当天"都会跑一遍。失败按非致命处理——单只股票查不到就跳过，不影响K线抓取（数据缺失
    /// 只会让"耀哥法"里"最近三天资金净流入"这条新规则判不满足，不会导致程序崩溃）。
    ///
    /// <paramref name="exactDayOnly"/>（2026-07-09新增）区分两种调用场景，跟K线的"拉取全部" vs
    /// "拉取当天"是同一个道理：false（拉取全部）走水位线增量逻辑，只在水位线正好停在
    /// <paramref name="rangeEnd"/>当天时才需要额外检查是否收盘后确认；true（拉取当天）直接检查
    /// <paramref name="rangeEnd"/>这个具体日期本地是否已经收盘后确认，不看整体水位线在哪。
    ///
    /// 失败追踪（2026-07-09新增）：这个是逐只单独查的，跟K线一样能精确到具体哪只失败——失败的
    /// 代码记进 <see cref="Manifest.FailedNetInflowCodes"/>，成功/跳过的移出，见该字段的类注释。
    /// </summary>
    private async Task FetchNetInflowAsync(IReadOnlyList<string> codes, DateTime rangeEnd, bool exactDayOnly, IProgress<string>? progress, CancellationToken ct)
    {
        var failedNetInflowCodes = new ConcurrentBag<string>();
        try
        {
            progress?.Report($"正在获取主力净流入历史（共 {codes.Count} 只股票，逐只查询，会比较慢）...");
            var repo = new SqliteNetInflowRepository(_paths.CurrentDb);
            repo.EnsureSchema();

            int totalRows = 0, failCount = 0;
            var tasks = codes.Select(async code =>
            {
                DateTime start;
                lock (_dbLock)
                {
                    if (exactDayOnly)
                    {
                        var existing = repo.Query(code, rangeEnd, rangeEnd).FirstOrDefault();
                        start = (existing != null && IsConfirmedFinal(existing.FetchedAt, rangeEnd)) ? rangeEnd.AddDays(1) : rangeEnd;
                    }
                    else
                    {
                        var info = repo.GetLatestRowInfo(code);
                        if (info == null)
                            start = rangeEnd.AddDays(-NetInflowInitialLookbackDays);
                        else if (info.Value.PeriodStart.Date < rangeEnd.Date)
                            start = info.Value.PeriodStart.AddDays(1);
                        else
                            start = IsConfirmedFinal(info.Value.FetchedAt, rangeEnd) ? rangeEnd.AddDays(1) : rangeEnd;
                    }
                }
                if (start.Date > rangeEnd.Date) return;

                List<NetInflow> rows;
                try
                {
                    rows = await _netInflowFetcher.FetchAsync(code, start, rangeEnd, ct);
                }
                catch (OperationCanceledException)
                {
                    throw; // 用户点了"停止"
                }
                catch
                {
                    Interlocked.Increment(ref failCount);
                    failedNetInflowCodes.Add(code);
                    return;
                }

                if (rows.Count == 0) return;
                lock (_dbLock)
                {
                    // 跟Bar同样的道理："今天"这一行可能是第二次抓到（盘中一次、收盘后一次），要
                    // 覆盖写入；更早的日期永远是第一次见到的新事实，InsertOrIgnore即可。
                    var todaysRows = rows.Where(r => r.PeriodStart.Date == DateTime.Today).ToList();
                    var olderRows = rows.Where(r => r.PeriodStart.Date != DateTime.Today).ToList();
                    if (olderRows.Count > 0) repo.InsertOrIgnore(olderRows);
                    if (todaysRows.Count > 0) repo.Upsert(todaysRows);
                }
                Interlocked.Add(ref totalRows, rows.Count);
            });
            await Task.WhenAll(tasks);

            progress?.Report($"主力净流入写入完成，共 {totalRows} 条" + (failCount > 0 ? $"（{failCount} 只股票查询失败，已跳过）" : ""));
        }
        catch (OperationCanceledException)
        {
            throw; // 用户点了"停止"
        }
        catch (Exception ex)
        {
            progress?.Report($"获取主力净流入失败（不影响K线抓取）：{ex.Message}");
            // 这层异常是整体性的（比如数据库层面出错，不是某只股票单独的问题）——保守地把这批
            // 请求的代码全部记为失败，而不是只用目前收集到的那一部分。
            foreach (var code in codes) failedNetInflowCodes.Add(code);
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedNetInflowCodes = ComputeUpdatedFailedCodes(manifest.FailedNetInflowCodes, codes, failedNetInflowCodes);
            _manifestStore.Save(manifest);
        }
    }

    /// <summary>
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

        try
        {
            await _announcementOrchestrator.RunAsync(keywords, start, end, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户点了"停止"
        }
        catch (Exception ex)
        {
            progress?.Report($"获取中标/订单公告失败（不影响K线抓取）：{ex.Message}");
        }
    }

    /// <summary>
    /// Shared tail for all three fetch modes——只更新 manifest 的 LastFetchAt/LastFetchKind/
    /// FailedCodes，不再产出任何文件（2026-07-09移除master/daily文件生产，见下方"状态变更记录"）。
    /// </summary>
    private FetchResult FinishFetchRun(
        ConcurrentBag<string> errors, string fetchKind,
        IReadOnlyCollection<string> attemptedCodes, ConcurrentBag<string> failedCodesThisRun,
        IProgress<string>? progress = null, bool checkDayCoverage = false)
    {
        var manifest = _manifestStore.Load();
        manifest.LastFetchAt = DateTime.Now;
        manifest.LastFetchKind = fetchKind;
        manifest.FailedCodes = ComputeUpdatedFailedCodes(manifest.FailedCodes, attemptedCodes, failedCodesThisRun);
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
    /// 一轮抓取跑完之后的"当天覆盖率体检"（2026-08-21新增）：把本该有最新交易日日线、库里却还是
    /// 没有的个股记进 <see cref="Manifest.MissingDayCodes"/>，交给"重新拉取失败股票"一并重试。
    ///
    /// 起因：数据源盘后是**逐步**更新的，请求发过去时那只股票的当天K线可能还没出来——接口正常返回、
    /// 只是里面没有那一天，代码算作"请求成功但无新数据"（<see cref="FetchStats.FetchedButEmpty"/>），
    /// 既不报错也不进任何失败名单、更不会重试。2026-08-20 那轮 19:00 开跑的"拉取全部"，个股前复权
    /// 只拿到 1773/5539 只（21:00 之后才抓的后复权和 ETF 一个不缺），而界面上一切正常、失败名单是
    /// 空的，用户第二天看盘才发现一半股票的"最新收盘"还停在前一天。
    ///
    /// 判定不看抓取过程中的统计，而是**直接查库**：以上证指数最新一根日线当"最近一个已收盘交易日"
    /// 的锚（<see cref="MarketIndexCatalog.ShanghaiCompositeSymbol"/> 就是为此存在的，指数不停牌、
    /// 不退市），取"上一个交易日有、这一天没有"的个股当作漏抓（见
    /// <see cref="SqliteBarRepository.GetCodesMissingDay"/>，用"上一个交易日有"过滤掉长期停牌和已
    /// 退市的票）。这样不管漏抓的原因是数据源没出、请求失败还是被跳过，都能一网打尽。
    ///
    /// 前复权(day)、后复权(day_hfq)、不复权(day_raw) 各查一次、并成一份名单（不复权是 2026-09-04
    /// 补上的——它是 day_adj 的输入，缺一天回测就错一天，而在此之前它当天缺了没人发现）：三条线
    /// 水位线独立，缺一个不代表另外两个也缺，而重补时已经齐了的那条会在水位线判定里直接跳过、
    /// 不发请求（【重新拉取失败】对这份名单本来就是三个口径都跑一遍）。所以名单本身不带口径，
    /// 只记"这只票那天有东西缺着"。名单每次体检都**重建**，不累加。
    /// </summary>
    public (DateTime? TradingDay, int MissingCount) CheckLatestDayCoverage(IProgress<string>? progress = null)
    {
        if (!File.Exists(_paths.CurrentDb)) return (null, 0);
        var repo = new SqliteBarRepository(_paths.CurrentDb);

        List<Bar> anchorBars;
        lock (_dbLock)
        {
            // 近两个月足够拿到最后两根交易日（含长假）。
            anchorBars = repo.Query(MarketIndexCatalog.ShanghaiCompositeSymbol, Granularity.Day,
                DateTime.Today.AddDays(-60), null);
        }
        if (anchorBars.Count < 2)
        {
            progress?.Report("（跳过当天覆盖率体检：本地上证指数日线不足两根，没有交易日锚可用）");
            return (null, 0);
        }

        var latest = anchorBars[^1].PeriodStart.Date;
        var previous = anchorBars[^2].PeriodStart.Date;

        progress?.Report($"正在体检 {latest:yyyy-MM-dd} 的个股日线覆盖率（前复权/后复权/不复权三套）...");
        List<string> missing;
        lock (_dbLock)
        {
            missing = repo.GetCodesMissingDay(Granularity.Day, latest, previous)
                .Union(repo.GetCodesMissingDay(Granularity.DayHfq, latest, previous))
                .Union(repo.GetCodesMissingDay(Granularity.DayRaw, latest, previous))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
        }

        var manifest = _manifestStore.Load();
        manifest.MissingDayCodes = missing;
        manifest.MissingDayDate = missing.Count > 0 ? latest : null;
        _manifestStore.Save(manifest);

        progress?.Report(missing.Count == 0
            ? $"当天覆盖率体检：{latest:yyyy-MM-dd} 的个股日线是齐的"
            : $"当天覆盖率体检：{latest:yyyy-MM-dd} 还有 {missing.Count} 只个股没有日线——多半是数据源盘后"
              + "还没更新到它们（不是抓取失败），已记入待重试名单，可点\"重新拉取失败股票\"补上");
        return (latest, missing.Count);
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

    /// <summary>各份失败名单的待重试量（2026-08-19 由原来的"一个总数"改成分类汇总）。
    ///
    /// 为什么不能只给一个总数：**流通市值是整轮扫描**，一次请求拿全市场，接口失败时会保守地把
    /// 这批代码全部记进名单（见 <see cref="FetchMarketCapAsync"/> 的 catch）。于是"1次接口失败"
    /// 在总数里表现成"5544 支失败"，按钮上写"重新拉取失败股票（5547）"会被读成丢了5547只票的
    /// 数据，实际上只是一次市值快照没取到、外加3只资金流。所以市值单独按"轮"表达。</summary>
    public FailedRetrySummary GetFailedRetrySummary()
    {
        var manifest = _manifestStore.Load();
        return new FailedRetrySummary
        {
            BarCodes = manifest.FailedCodes.Count,
            MissingDayCodes = manifest.MissingDayCodes.Count,
            MissingDayDate = manifest.MissingDayDate,
            MarketCapCodes = manifest.FailedMarketCapCodes.Count,
            NetInflowCodes = manifest.FailedNetInflowCodes.Count,
            IndexConsCodes = manifest.FailedIndexConsCodes.Count,
            IndexWeightCodes = manifest.FailedIndexWeightCodes.Count,
            ShareholderCodes = manifest.FailedShareholderCodes.Count,
            DividendCodes = manifest.FailedDividendCodes.Count,
        };
    }

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
            manifest.FailedIndexConsCodes = ComputeUpdatedFailedCodes(manifest.FailedIndexConsCodes, attempted, consFailed);
            manifest.FailedIndexWeightCodes = ComputeUpdatedFailedCodes(manifest.FailedIndexWeightCodes, attempted, weightFailed);
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
    {
        if (!Directory.Exists(_paths.ReportsDir)) return;

        int reparsed = 0, reparseFixed = 0;
        progress?.Report("正在用当前解析规则重跑本地已缓存的 PDF（不联网）...");
        foreach (var dir in Directory.GetDirectories(_paths.ReportsDir))
        {
            ct.ThrowIfCancellationRequested();
            var code = Path.GetFileName(dir);
            // 这家已经存下的指标，用来判断哪几期不用再 OCR（见下面 fullyApproved）。
            List<Logic.Models.BankRegulatoryMetric> existing;
            lock (_dbLock) existing = repo.GetByCode(code);
            foreach (var pdf in Directory.GetFiles(dir, "*.pdf"))
            {
                if (!DateTime.TryParse(Path.GetFileNameWithoutExtension(pdf), out var d)) continue;

                if (!BankReportParser.LooksLikeReport(pdf))
                {
                    progress?.Report($"  {code} {d:yyyy-MM-dd} 下载到的不是报告正文"
                                   + "（多半是问询函/专项报告），已删除，稍后重新下载");
                    try { File.Delete(pdf); } catch { }
                    lock (_dbLock)
                    {
                        repo.UpsertState(new Logic.Models.BankReportFetchState
                        {
                            Code = code, ReportDate = d, Status = "wrong_file",
                            MetricCount = 0, Message = "下载到的不是报告正文，已删除待重下",
                        });
                    }
                    continue;
                }

                try
                {
                    // 重解析也要按机构类型选标签集，否则会拿银行的标签去解析券商的报表。
                    var kind = latest.TryGetValue(code, out var snap)
                        ? Logic.Services.BankHealthCheckBuilder.ClassifyInstitution(snap)
                        : Logic.Models.FinancialInstitutionKind.Bank;
                    // 这一期的核心指标要是全都被人核对/回填过了，就别再跑 OCR 了——
                    // 一份要一分钟，而跑出来的值按 Upsert 的规则本来也覆盖不了人拍板的。
                    var expected = Logic.Models.RegulatoryMetricCatalog.ExpectedFor(kind);
                    bool fullyApproved = expected.Length > 0 && expected.All(k =>
                        existing.Any(m => m.ReportDate == d && m.MetricKey == k
                                          && Logic.Models.MetricSources.HumanApproved.Contains(m.Source)));
                    var ms = BankReportParser.Parse(pdf, code, d, kind,
                        s => progress?.Report(s), ct, allowOcr: !fullyApproved);
                    if (ms.Count == 0) continue;
                    lock (_dbLock)
                    {
                        repo.Upsert(ms);
                        repo.UpsertState(new Logic.Models.BankReportFetchState
                        {
                            Code = code, ReportDate = d, Status = "ok",
                            MetricCount = ms.Count, PdfPath = pdf,
                        });
                    }
                    reparsed++; reparseFixed += ms.Count;
                }
                catch { /* 解析不了的交给下载流程当成没抓过重新处理 */ }
            }
        }
        if (reparsed > 0)
            progress?.Report($"  本地重解析完成：{reparsed} 份报告、{reparseFixed} 个指标已按新规则刷新。");
    }

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

    /// <summary>"重新拉取失败股票"里针对指数成分/权重失败名单的重试（2026-07-16新增）——逐指数精确重试，
    /// 更新各自失败名单，可反复点击直到清零。错误通过 progress 报告（跟市值/资金流的重试一致）。</summary>
    private async Task RetryIndexAsync(IReadOnlyList<string> consCodes, IReadOnlyList<string> weightCodes,
        IProgress<string>? progress, CancellationToken ct)
    {
        _indexRepository.EnsureSchema();
        void Forward(string s) => progress?.Report(s);
        _indexConsProvider.OnStatus += Forward;
        _indexWeightProvider.OnStatus += Forward;

        var consFailed = new List<string>();
        var weightFailed = new List<string>();
        var now = DateTime.Now;
        try
        {
            if (consCodes.Count > 0)
            {
                progress?.Report($"重试指数成分失败 {consCodes.Count} 个...");
                foreach (var code in consCodes)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var members = await _indexConsProvider.GetConsAsync(code, ct);
                        if (members.Count > 0) { lock (_dbLock) _indexRepository.ReplaceCons(code, members, now); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { progress?.Report($"指数 {code} 成分重试仍失败：{ex.Message}"); consFailed.Add(code); }
                }
            }

            if (weightCodes.Count > 0)
            {
                progress?.Report($"重试指数权重失败 {weightCodes.Count} 个...");
                foreach (var code in weightCodes)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var weights = await _indexWeightProvider.GetWeightsAsync(code, ct);
                        if (weights.Count > 0) { lock (_dbLock) _indexRepository.ReplaceWeights(code, weights); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { progress?.Report($"指数 {code} 权重重试仍失败：{ex.Message}"); weightFailed.Add(code); }
                }
            }
        }
        finally
        {
            _indexConsProvider.OnStatus -= Forward;
            _indexWeightProvider.OnStatus -= Forward;
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedIndexConsCodes = ComputeUpdatedFailedCodes(manifest.FailedIndexConsCodes, consCodes, consFailed);
            manifest.FailedIndexWeightCodes = ComputeUpdatedFailedCodes(manifest.FailedIndexWeightCodes, weightCodes, weightFailed);
            _manifestStore.Save(manifest);
        }
    }

    /// <summary>
    /// "拉取龙虎榜"（2026-07-16新增）——新浪龙虎榜按交易日抓取(Lhb 表, INSERT OR IGNORE 累积)。
    /// <paramref name="from"/>/<paramref name="to"/> 都为 null=只抓今天；给区间则逐交易日回补（跳过
    /// 周末，节假日靠返回空自然跳过）。龙虎榜失败不进 Manifest 名单，失败的日期在日志报出，重新指定
    /// 日期区间再点即可。
    /// </summary>
    public async Task<FetchResult> RunFetchLhbAsync(DateOnly? from, DateOnly? to, IProgress<string>? progress, CancellationToken ct = default)
    {
        void Forward(string s) => progress?.Report(s);
        _lhbProvider.OnStatus += Forward;
        try
        {
            return await RunFetchLhbInternalAsync(from, to, progress, ct);
        }
        finally
        {
            _lhbProvider.OnStatus -= Forward;
        }
    }

    private async Task<FetchResult> RunFetchLhbInternalAsync(DateOnly? from, DateOnly? to, IProgress<string>? progress, CancellationToken ct)
    {
        _lhbRepository.EnsureSchema();
        var start = from ?? DateOnly.FromDateTime(DateTime.Today);
        var end = to ?? start;
        if (end < start) (start, end) = (end, start);

        var errors = new List<string>();
        var sw = Stopwatch.StartNew();
        int tradingDays = 0, rowsTotal = 0, failDays = 0;
        progress?.Report($"开始拉取龙虎榜 {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}（新浪，按交易日）...");
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;   // 周末必非交易日，跳过
            try
            {
                var rows = await _lhbProvider.GetDailyAsync(d, ct);
                if (rows.Count > 0) { lock (_dbLock) _lhbRepository.InsertOrIgnore(rows); rowsTotal += rows.Count; }
                tradingDays++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"龙虎榜 {d:yyyy-MM-dd} 抓取失败：{ex.Message}"); failDays++; }
            progress?.Report($"龙虎榜 {d:yyyy-MM-dd}：累计写入 {rowsTotal} 条（已用时 {FormatElapsed(sw.Elapsed)}）" + (failDays > 0 ? $"，失败 {failDays} 天" : ""));
        }
        progress?.Report($"龙虎榜完成：处理 {tradingDays} 天、写入 {rowsTotal} 条、失败 {failDays} 天" +
                         (failDays > 0 ? "（失败的日期重新指定区间再点一次即可）" : ""));
        return new FetchResult { Errors = errors };
    }

    /// <summary>
    /// "拉取股东数据"（2026-07-16新增）——逐只个股从新浪股本股东页抓取：股东户数(ShareholderCount) +
    /// 十大股东/十大流通股东(TopShareholder)。用本地已有个股列表(需先"拉取全部"一次)，全市场逐只、量大
    /// 较慢。逐只记录失败(<see cref="Manifest.FailedShareholderCodes"/>)，可用"重新拉取失败股票"重试。
    /// 不掺进"拉取全部"主流程（股东数据季度级慢变，不必每天跑）。
    ///
    /// ⚠ 2026-08-15 修正：这个方法从一开始就是 public、注释也写着"独立按钮"，但**界面上的按钮直到
    /// 今天才真正加上**——在那之前它只能通过"一键拉取定期数据"触发，而且排在第3位（行业分类 →
    /// 指数成分/权重 → 股东数据 → …），前两步耗时很长。后果：修完持股数解析bug后想重抓验证，
    /// 点了定期数据却没等到第3步，数据一行都没更新、还以为是修复没生效。
    /// </summary>
    public async Task<FetchResult> RunFetchShareholderAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        void Forward(string s) => progress?.Report(s);
        _shareholderProvider.OnStatus += Forward;
        try
        {
            return await RunFetchShareholderInternalAsync(progress, ct);
        }
        finally
        {
            _shareholderProvider.OnStatus -= Forward;
        }
    }

    private async Task<FetchResult> RunFetchShareholderInternalAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法拉取股东数据，请先执行一次\"拉取全部\"");
        _shareholderRepository.EnsureSchema();
        var stocks = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb);
        if (stocks.Count == 0)
            throw new InvalidOperationException("本地股票列表为空，无法拉取股东数据，请先执行一次\"拉取全部\"");

        var errors = new ConcurrentBag<string>();
        var failed = new ConcurrentBag<string>();
        var attempted = stocks.Select(s => s.Code).ToList();
        var sw = Stopwatch.StartNew();
        int completed = 0, withData = 0, suspiciousRows = 0;
        progress?.Report($"开始拉取股东数据（户数+十大股东+十大流通股东），共 {stocks.Count} 只，逐只抓、较慢...");

        var tasks = stocks.Select(async stock =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await _shareholderProvider.GetAsync(stock.Code, ct);
                if (data.Counts.Count > 0 || data.TopHolders.Count > 0)
                {
                    // 自洽性检查：排名夹在正数中间却是0 = 那一格没解析出来（2026-08-13 的箭头
                    // 事故就是这么静默混进14953行的，见 SqliteShareholderRepository
                    // .FindInconsistentZeroShares）。**只报警不拦截**——局部异常不该中断整批抓取，
                    // 而且宁可先入库、让用户看到问题，也好过悄悄丢数据。
                    var bad = SqliteShareholderRepository.FindInconsistentZeroShares(data.TopHolders);
                    if (bad.Count > 0)
                    {
                        Interlocked.Add(ref suspiciousRows, bad.Count);
                        foreach (var msg in bad.Take(3)) errors.Add($"⚠ 数据可疑 {msg}");
                    }
                    lock (_dbLock) _shareholderRepository.ReplaceByCode(stock.Code, data);
                    Interlocked.Increment(ref withData);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{stock.Code}: {ex.Message}"); failed.Add(stock.Code); }

            int done = Interlocked.Increment(ref completed);
            if (done % 50 == 0 || done == stocks.Count)
                progress?.Report($"股东数据 {done}/{stocks.Count}（有数据 {withData}、失败 {failed.Count}，已用时 {FormatElapsed(sw.Elapsed)}）");
        });
        await Task.WhenAll(tasks);

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedShareholderCodes = ComputeUpdatedFailedCodes(manifest.FailedShareholderCodes, attempted, failed.ToList());
            _manifestStore.Save(manifest);
        }

        progress?.Report($"股东数据完成：{withData} 只有数据、失败 {failed.Count} 只" +
                         (failed.Count > 0 ? "（可点\"重新拉取失败股票\"重试）" : ""));
        if (suspiciousRows > 0)
            progress?.Report($"⚠ 有 {suspiciousRows} 行持股数解析为0但排名夹在正数中间——" +
                             "这通常意味着数据源页面格式变了（比如在数字后面加了新的装饰符号）。" +
                             "数据已入库但那几行不可信，请检查 SinaShareholderProvider.ParseD 的清洗规则。");
        var result = new FetchResult();
        result.Errors.AddRange(errors);
        return result;
    }

    /// <summary>"重新拉取失败股票"里针对股东数据失败名单的重试（2026-07-16新增）——逐只精确重试，更新
    /// 失败名单，可反复点击直到清零。错误通过 progress 报告。</summary>
    private async Task RetryShareholderAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        _shareholderRepository.EnsureSchema();
        void Forward(string s) => progress?.Report(s);
        _shareholderProvider.OnStatus += Forward;

        var failed = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();
        int completed = 0;
        progress?.Report($"重试股东数据失败 {codes.Count} 只...");
        try
        {
            var tasks = codes.Select(async code =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var data = await _shareholderProvider.GetAsync(code, ct);
                    if (data.Counts.Count > 0 || data.TopHolders.Count > 0)
                        lock (_dbLock) _shareholderRepository.ReplaceByCode(code, data);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { progress?.Report($"{code} 股东数据重试仍失败：{ex.Message}"); failed.Add(code); }

                int done = Interlocked.Increment(ref completed);
                if (done % 50 == 0 || done == codes.Count)
                    progress?.Report($"股东数据重试 {done}/{codes.Count}（已用时 {FormatElapsed(sw.Elapsed)}）");
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            _shareholderProvider.OnStatus -= Forward;
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedShareholderCodes = ComputeUpdatedFailedCodes(manifest.FailedShareholderCodes, codes, failed.ToList());
            _manifestStore.Save(manifest);
        }
    }

    /// <summary>
    /// 融资余额并入"拉取全部/当天"时回看的交易日数。
    ///
    /// **为什么必须回看、不能只抓当天**：两所的融资余额是 **T+1 发布**的——收盘当晚查当天，接口返回空。
    /// 旧实现只抓 <c>today</c>，于是每次都拿到空、走"无数据（可能非交易日）"分支静默跳过，第二天又只
    /// 看新的一天，**昨天的数据永远补不上**。2026-08 实测：日线正常更新到 8/14、同样并入主流程的龙虎榜
    /// （当晚就发布）一天不缺，唯独融资余额从 7/30 起连续缺 12 个交易日；库里更早的记录也全是靠手动点
    /// "一键补齐每日历史"补的（抓取时间比数据日晚 1~30 天不等），没有一条是当天抓到当天的。
    ///
    /// 回看 10 个交易日足够跨过长假（春节最长 9 个交易日），本地已有的日子直接跳过、不发请求，
    /// 所以日常代价基本为零（正常只会真去抓 1 天）。
    /// </summary>
    private const int MarginLookbackTradingDays = 10;

    /// <summary>抓最近若干交易日的融资余额并写库，**跳过本地已有的日子**（非致命：失败只记 error，
    /// 不影响主流程其他步骤）——供"拉取全部/当天"并入调用。回看的原因见
    /// <see cref="MarginLookbackTradingDays"/>；补更早的历史用"一键补齐每日历史"。</summary>
    private async Task FetchMarginRecentAsync(DateTime day, ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            _marginRepository.EnsureSchema();
            var have = _marginRepository.GetTradeDates();
            var end = DateOnly.FromDateTime(day);
            // 按自然日往前退，够覆盖 MarginLookbackTradingDays 个交易日即可（周末/节假日接口返回空，
            // 只是白跑一次请求，不影响正确性）——这里不查交易日历，退 2 倍天数足够。
            var start = end.AddDays(-MarginLookbackTradingDays * 2);

            int wrote = 0, days = 0, skipped = 0;
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                if (have.Contains(d)) { skipped++; continue; }
                var rows = await _marginProvider.GetDetailAsync(d, ct);
                days++;
                if (rows.Count == 0) continue;          // 非交易日，或当天数据还没发布（明天这轮会补上）
                lock (_dbLock) _marginRepository.InsertOrIgnore(rows);
                wrote += rows.Count;
                progress?.Report($"融资余额 {d:yyyy-MM-dd}：{rows.Count} 条已写入");
            }

            progress?.Report(wrote > 0
                ? $"融资余额：本轮补了 {wrote} 条（试抓 {days} 天，跳过本地已有 {skipped} 天）"
                : $"融资余额：无新增（试抓 {days} 天都没数据，跳过本地已有 {skipped} 天）。" +
                  "两所是T+1发布，当天查不到属正常，明天这轮会自动补上。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"融资余额 {day:yyyy-MM-dd}：{ex.Message}"); }
    }

    /// <summary>抓某一天的龙虎榜并写库（非致命）——供"拉取全部/当天"并入调用。历史用"回补龙虎榜"补齐。</summary>
    private async Task FetchLhbOneDayAsync(DateTime day, ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            _lhbRepository.EnsureSchema();
            var d = DateOnly.FromDateTime(day);
            var rows = await _lhbProvider.GetDailyAsync(d, ct);
            if (rows.Count > 0)
            {
                lock (_dbLock) _lhbRepository.InsertOrIgnore(rows);
                progress?.Report($"龙虎榜 {d:yyyy-MM-dd}：{rows.Count} 条已写入");
            }
            else progress?.Report($"龙虎榜 {d:yyyy-MM-dd}：无数据（可能非交易日）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"龙虎榜 {day:yyyy-MM-dd}：{ex.Message}"); }
    }

    /// <summary>"回补融资余额"（2026-07-16新增）——按交易日区间回补历史（首次或补漏）。日常当天数据已并入
    /// "拉取全部/当天"，这个按钮用于第一次把历史补齐或补某段缺的日子。from/to 都为 null=今天；失败不进
    /// Manifest，重新指定区间再点即可。</summary>
    public async Task<FetchResult> RunFetchMarginAsync(DateOnly? from, DateOnly? to, IProgress<string>? progress, CancellationToken ct = default)
    {
        void Forward(string s) => progress?.Report(s);
        _marginProvider.OnStatus += Forward;
        try
        {
            _marginRepository.EnsureSchema();
            var start = from ?? DateOnly.FromDateTime(DateTime.Today);
            var end = to ?? start;
            if (end < start) (start, end) = (end, start);

            var errors = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();
            int days = 0, total = 0, fail = 0;
            progress?.Report($"开始回补融资余额 {start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}（交易所官方，按交易日）...");
            for (var d = start; d <= end; d = d.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                try
                {
                    var rows = await _marginProvider.GetDetailAsync(d, ct);
                    if (rows.Count > 0) { lock (_dbLock) _marginRepository.InsertOrIgnore(rows); total += rows.Count; }
                    days++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"融资余额 {d:yyyy-MM-dd}：{ex.Message}"); fail++; }
                progress?.Report($"融资余额 {d:yyyy-MM-dd}：累计写入 {total} 条（已用时 {FormatElapsed(sw.Elapsed)}）" + (fail > 0 ? $"，失败 {fail} 天" : ""));
            }
            progress?.Report($"融资余额回补完成：处理 {days} 天、写入 {total} 条、失败 {fail} 天" +
                             (fail > 0 ? "（失败的日期重新指定区间再点一次即可）" : ""));
            var result = new FetchResult();
            result.Errors.AddRange(errors);
            return result;
        }
        finally
        {
            _marginProvider.OnStatus -= Forward;
        }
    }

    /// <summary>
    /// "一键补齐每日历史"（2026-07-16新增）——把**每日数据**（融资余额、龙虎榜）的历史一次性补齐：范围从
    /// 本地 K线(Bar)最早那天到今天，逐交易日抓，**本地已有的交易日跳过、不重复请求**。一次性用途：开发中
    /// 新加了每日数据、之前没抓的，点一次补上历史；之后每天靠"拉取全部/当天"增量。以后再加每日数据也并进来。
    /// </summary>
    public async Task<FetchResult> RunBackfillDailyHistoryAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        void Forward(string s) => progress?.Report(s);
        _marginProvider.OnStatus += Forward;
        _lhbProvider.OnStatus += Forward;
        try
        {
            if (!File.Exists(_paths.CurrentDb))
                throw new InvalidOperationException("本地还没有任何数据，请先执行一次\"拉取全部\"（要用K线的最早日期作为补齐起点）");
            var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
            currentRepo.EnsureSchema();
            _marginRepository.EnsureSchema();
            _lhbRepository.EnsureSchema();

            var earliest = currentRepo.GetOverallEarliestPeriodStart(Granularity.Day);
            if (earliest == null)
                throw new InvalidOperationException("本地还没有K线数据，无法确定补齐起点，请先执行一次\"拉取全部\"");
            var start = DateOnly.FromDateTime(earliest.Value);
            var end = DateOnly.FromDateTime(DateTime.Today);

            var errors = new ConcurrentBag<string>();
            var sw = Stopwatch.StartNew();

            var marginHave = _marginRepository.GetTradeDates();
            await BackfillDailyAsync("融资余额", start, end, marginHave, async d =>
            {
                var rows = await _marginProvider.GetDetailAsync(d, ct);
                if (rows.Count > 0) { lock (_dbLock) { _marginRepository.InsertOrIgnore(rows); } }
                return rows.Count;
            }, errors, progress, sw, ct);

            var lhbHave = _lhbRepository.GetTradeDates();
            await BackfillDailyAsync("龙虎榜", start, end, lhbHave, async d =>
            {
                var rows = await _lhbProvider.GetDailyAsync(d, ct);
                if (rows.Count > 0) { lock (_dbLock) { _lhbRepository.InsertOrIgnore(rows); } }
                return rows.Count;
            }, errors, progress, sw, ct);

            progress?.Report("融资余额、龙虎榜历史补齐完毕。");
            var result = new FetchResult();
            result.Errors.AddRange(errors);
            return result;
        }
        finally
        {
            _marginProvider.OnStatus -= Forward;
            _lhbProvider.OnStatus -= Forward;
        }
    }

    /// <summary>逐交易日补齐一类每日数据：跳过周末和本地已有的日子，只抓缺的。<paramref name="fetchOne"/>
    /// 负责抓某天并写库、返回写入条数；异常记进 errors（非致命，继续下一天）。</summary>
    private static async Task BackfillDailyAsync(string label, DateOnly start, DateOnly end, HashSet<DateOnly> have,
        Func<DateOnly, Task<int>> fetchOne, ConcurrentBag<string> errors, IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        progress?.Report($"开始补齐{label}历史：{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}（跳过周末和本地已有的日子）...");
        int done = 0, wrote = 0, skipped = 0, fail = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (have.Contains(d)) { skipped++; continue; }
            try { wrote += await fetchOne(d); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{label} {d:yyyy-MM-dd}：{ex.Message}"); fail++; }
            if (++done % 20 == 0)
                progress?.Report($"{label} 补齐中：已抓 {done} 天、写入 {wrote} 条、失败 {fail}（跳过已有 {skipped} 天，已用时 {FormatElapsed(sw.Elapsed)}）");
        }
        progress?.Report($"{label}补齐完成：新抓 {done} 个交易日、写入 {wrote} 条、跳过已有 {skipped} 天、失败 {fail} 天" +
                         (fail > 0 ? "（失败的可再点一次一键补齐、只会补还缺的）" : ""));
    }

    /// <summary>
    /// "一键拉取定期数据"（2026-07-16新增）——把**不是每天更新**的数据一次点完：依次跑 指数成分/权重 →
    /// 股东数据（各自全量刷新）→ 财务报表（2026-07-31并入，按报告期增量跳过、二次运行几乎零成本）。
    /// 前一个整体失败不阻断后一个（分别 try/catch）；单项内部的逐指数/逐股失败
    /// 仍进各自失败名单、可用"重新拉取失败股票"重试。⚠️ 较慢，可能数小时，季度点一次即可。板块不在这里
    /// （它更新频率高、独立"拉取板块"按钮）。
    /// </summary>
    public async Task<FetchResult> RunFetchPeriodicAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var errors = new List<string>();
        progress?.Report("依次执行：行业分类 → 指数成分/权重 → 股东数据 → 财务报表 → 分红送配（较慢，可能数小时）");

        // 行业分类放最前：只要一两分钟，且后面几步都不依赖它，先跑完早出结果
        if (_industryProvider != null)
        {
            try { errors.AddRange((await RunFetchIndustryAsync(progress, ct)).Errors); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"行业分类整体失败：{ex.Message}"); }
        }

        try { errors.AddRange((await RunFetchIndexConsAsync(progress, ct)).Errors); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"指数成分/权重整体失败：{ex.Message}"); }

        try { errors.AddRange((await RunFetchShareholderAsync(progress, ct)).Errors); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"股东数据整体失败：{ex.Message}"); }

        try { errors.AddRange((await RunFetchFinancialsAsync(progress, ct)).Errors); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { errors.Add($"财务报表整体失败：{ex.Message}"); }

        if (_dividendProvider != null && _dividendRepository != null)
        {
            try { errors.AddRange((await RunFetchDividendAsync(progress, ct)).Errors); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"分红送配整体失败：{ex.Message}"); }
        }

        // 财务那一段可能是几小时前跑的，它自己那句剩余提示早被后面的日志刷走了，这里在最终
        // 汇总里再说一遍——否则用户看到"全部处理完毕"会以为财务也补齐了，实际可能只补了一轮。
        string financialTail = "";
        try
        {
            int stillPending = GetFinancialFetchPlan().AllPending.Count;
            if (stillPending > 0)
                financialTail = $" ⚠ 注意：财务报表还有 {stillPending} 只未补" +
                                $"（该接口配额严、每轮上限 {MaxFinancialFetchPerRun} 只，见运行日志里财务那一段）——" +
                                "建议勾选界面上的【空闲时自动补财务】，程序空着时会自己补完。";
        }
        catch (Exception) { /* 只是提示 */ }

        progress?.Report("行业分类、指数成分/权重、股东数据、财务报表、分红送配全部处理完毕。" + financialTail);
        return new FetchResult { Errors = errors };
    }

    /// <summary>
    /// 拉取全市场证监会行业分类（2026-08-04新增）——见 <see cref="ExchangeSinaIndustryProvider"/>。
    /// 两级：门类（两所官网，覆盖沪深全部）+ 大类（新浪，粒度合适但约58%覆盖），消费端优先用大类、
    /// 缺失退回门类。用途：① 因子法名单显示"板块"；② FactorLab 的行业中性化——原先用板块表只有
    /// 44% 覆盖、其余全挤在一个"未知"组里，中性IC 一直不够准。
    /// 行业极少变动，属定期数据，季度跟财报一起跑一次即可；整体覆盖写入，反复跑无副作用。
    /// </summary>
    /// <summary>
    /// 抓指定股票列表的财务报表（2026-08-29 新增）。跟 <see cref="RunFetchFinancialsAsync"/> 的区别：
    /// 那个按增量计划抓全市场、有每轮上限；这个直接抓给定的一小批，供【银行监管指标】做前置补数。
    ///
    /// 同样**必须顺序处理**，原因见 RunFetchFinancialsAsync 里那段关于信号量 FIFO 的注释。
    /// </summary>
    private async Task FetchFinancialsForCodesAsync(
        List<string> codes, IProgress<string>? progress, CancellationToken ct)
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

    /// <summary>
    /// 抓银行监管指标（2026-08-29 新增）——不良率、拨备覆盖率、核心一级资本充足率、客户集中度、
    /// 迁徙率。这些**三张报表里一个都没有**，只在财报正文的"会计数据和财务指标摘要"那两三页，
    /// 所以是下载 PDF + 解析，见 <see cref="BankReportFetcher"/> / <see cref="BankReportParser"/>。
    ///
    /// 银行名单靠**科目特征**认（利息净收入占营业收入四成以上），不查行业表——行业表覆盖率不满。
    /// 前置：银行得先按 v3 科目集抓过财务报表，否则库里没有 interest_net，一家都认不出来。
    ///
    /// 只抓年报和中报：一季报/三季报是简版，没有那几张监管指标表。
    /// </summary>
    public async Task<FetchResult> RunFetchBankRegulatoryAsync(
        IProgress<string>? progress, bool refetchAll = false, CancellationToken ct = default)
    {
        var result = new FetchResult();
        var repo = new SqliteBankRegulatoryRepository(_paths.CurrentDb);
        repo.EnsureSchema();

        var finRepo = new SqliteFinancialRepository(_paths.CurrentDb);
        var latest = finRepo.GetLatestSnapshotByCode();

        // ── 前置：自己把需要的财务数据补齐，不必等全市场 ──────────────────────────────
        // 银行是靠"利息净收入占营收四成以上"认出来的，而那个科目是 v3 才加的。如果要求用户先跑完
        // 全市场【拉取财务报表】（5000+ 只 × 3 张报表）才能用这个按钮，等待时间完全不成比例——
        // 真正需要的只有 40 来家银行。
        //
        // 鸡生蛋的地方在于：没抓 v3 之前认不出谁是银行。解法是用**老数据也判得出**的特征先粗筛：
        // 银行/券商/保险的利润表都没有"营业成本"。金融机构总共一百来只，全抓一遍也就几分钟，
        // 之后再用 interest_net 精确挑出银行。
        // ⚠ 判据必须是**科目集版本号**，不能是"某个科目在不在"。
        //   踩过的坑：原来写的是"缺 interest_net 就补抓"，可库里有 562 只已经抓到 v3（有
        //   interest_net、但没有 v4 才加的已赚保费/代理买卖证券业务净收入），于是券商和保险
        //   全部被跳过、永远识别不出来。版本号才是"科目齐不齐"的唯一可靠依据。
        var fetchState = finRepo.GetFetchStateByCode();
        var needFinancial = latest
            .Where(kv => kv.Value.Get(FinancialKeys.OperCost) is null or 0)       // 金融机构
            .Where(kv => !fetchState.TryGetValue(kv.Key, out var st)
                         || st.KeysVersion < FinancialKeys.Version)               // 科目集落后
            .Select(kv => kv.Key)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        if (needFinancial.Count > 0)
        {
            progress?.Report($"检测到 {needFinancial.Count} 只金融股的科目集低于 v{FinancialKeys.Version}"
                           + "（缺银行/券商/保险的特征科目，认不出机构类型），"
                           + "先补抓它们的财务报表——只抓这一批，不用等全市场。");
            await FetchFinancialsForCodesAsync(needFinancial, progress, ct);
            latest = finRepo.GetLatestSnapshotByCode();   // 重新读，这次才认得出银行
        }

        // 三类金融机构各有一套监管指标，解析时按类型选标签集（见 BankReportParser.LabelsFor）。
        var targets = latest
            .Select(kv => (Code: kv.Key,
                           Kind: Logic.Services.BankHealthCheckBuilder.ClassifyInstitution(kv.Value)))
            .Where(x => x.Kind is Logic.Models.FinancialInstitutionKind.Bank
                             or Logic.Models.FinancialInstitutionKind.Broker
                             or Logic.Models.FinancialInstitutionKind.Insurer)
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ToList();

        if (targets.Count == 0)
        {
            var msg = "没有识别出任何银行/券商/保险。若本地库从没抓过财务报表，请先跑一次"
                    + "【拉取财务报表】再回来点这个。";
            progress?.Report("⚠ " + msg);
            result.Errors.Add(msg);
            return result;
        }
        int nBank = targets.Count(t => t.Kind == Logic.Models.FinancialInstitutionKind.Bank);
        int nBroker = targets.Count(t => t.Kind == Logic.Models.FinancialInstitutionKind.Broker);
        int nInsurer = targets.Count(t => t.Kind == Logic.Models.FinancialInstitutionKind.Insurer);
        progress?.Report($"识别出 银行 {nBank} 家、券商 {nBroker} 家、保险 {nInsurer} 家，"
                       + "开始抓取监管指标（只抓年报和中报）...");

        // ── 先把本地已有的 PDF 全部重新解析一遍（不联网、几分钟）──────────────────────
        // 这一步是幂等自愈：解析规则改进后（各行版式差异会不断暴露新问题），已经下载过的报告
        // 不需要重新下载就能用新规则重跑，旧的错值被 INSERT OR REPLACE 覆盖掉。
        // 这正是"PDF 要留在本地"的意义所在——真实修过的坑：注释角标「（注3）」没清干净，
        // 平安银行的拨备覆盖率被存成了 3.0；目录页"七、资本充足率分析 42"的页码被当成资本充足率。
        ReparseCachedBankReports(repo, latest, progress, ct);

        var done = refetchAll ? new HashSet<(string, DateTime)>() : repo.GetSucceeded();

        // 「这家这一期披露了没有」——没披露就别去翻公告列表了。
        // 原来是无条件为每一家发请求查列表，而这一段限流很紧（约 17 请求/分钟，见下面的
        // RateLimiter 参数），87 家跑一轮要个把小时。披露季前期（比如 10 月上旬找三季报）
        // 绝大多数机构根本还没出报告，那一小时全是空转（2026-09-03 用户提出用预约日表来判断）。
        //
        // ⚠ 查不到披露记录的照常查——兜底方向只能是"多查"，不能因为查不到就漏掉一家。
        var disclosed = refetchAll
            ? new Dictionary<string, DateTime>(StringComparer.Ordinal)
            : new SqliteEarningsScheduleRepository(_paths.CurrentDb)
                .GetLatestDisclosedPeriodByCode(DateTime.Today);
        // 限流分两套，理由见 BankReportFetcher 的构造函数注释。
        // 页面侧参数参照 App.xaml.cs 里 financialProvider 那段血泪教训（3并发/1秒 → HTTP 456、
        // 整轮零成功）取保守值：单并发 + 3 秒 + 每 40 个歇 45 秒 ≈ 17 请求/分钟。
        // 文件侧打的是静态服务器、配额独立，但单个 PDF 几 MB，也不并发。
        var fetcher = new BankReportFetcher(
            pageLimiter: new RateLimiter(
                maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(3),
                batchSize: 40, restDuration: TimeSpan.FromSeconds(45)),
            fileLimiter: new RateLimiter(
                maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2),
                batchSize: 30, restDuration: TimeSpan.FromSeconds(30)),
            cacheDir: _paths.ReportsDir);
        void Forward(string s) => progress?.Report(s);
        fetcher.OnStatus += Forward;

        var sw = Stopwatch.StartNew();
        int okCount = 0, failCount = 0, skipCount = 0, metricTotal = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (code, kind) = targets[i];
                string kindName = kind switch
                {
                    Logic.Models.FinancialInstitutionKind.Bank => "银行",
                    Logic.Models.FinancialInstitutionKind.Broker => "券商",
                    _ => "保险",
                };
                // 已披露的最新一期本地已经拿到了 → 这一轮它没有新东西，一个请求都不用发。
                if (disclosed.TryGetValue(code, out var latestDisclosed)
                    && done.Contains((code, latestDisclosed)))
                {
                    skipCount++;
                    continue;
                }

                progress?.Report($"[{i + 1}/{targets.Count}] {code}（{kindName}）查找年报/中报...");

                // 每一期拿**全部候选**（标题只做粗筛+排序，见 TitleRank），下面逐个试到解析出指标为止
                SortedDictionary<DateTime, List<BankReportFetcher.ReportRef>> byDate;
                try { byDate = await fetcher.ListCandidatesAsync(code, maxPerKind: 2, ct); }
                catch (Exception ex)
                {
                    failCount++;
                    result.Errors.Add($"{code} 取公告列表失败：{ex.Message}");
                    continue;
                }

                foreach (var (period, candidates) in byDate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (done.Contains((code, period))) { skipCount++; continue; }
                    var r = candidates[0];

                    var (state, metrics) = await fetcher.FetchBestAsync(
                        candidates, kind, s => progress?.Report(s), ct);
                    // 成功失败都写状态——静默跳过会让界面分不清"没抓"和"抓失败"。
                    lock (_dbLock)
                    {
                        if (metrics.Count > 0) repo.Upsert(metrics);
                        repo.UpsertState(state);
                    }
                    if (state.Status == "ok")
                    {
                        okCount++; metricTotal += state.MetricCount;
                        progress?.Report($"    {r.ReportDate:yyyy-MM-dd} {r.Title} → {state.MetricCount} 个指标");
                    }
                    else
                    {
                        failCount++;
                        progress?.Report($"    ⚠ {r.ReportDate:yyyy-MM-dd} {state.Status}：{state.Message}");
                    }
                }
            }
        }
        finally { fetcher.OnStatus -= Forward; }

        progress?.Report($"金融监管指标完成：成功 {okCount} 份（共 {metricTotal} 个指标）、"
                       + $"失败 {failCount} 份、跳过已有 {skipCount} 份，用时 {FormatElapsed(sw.Elapsed)}。"
                       + $"PDF 缓存在 {_paths.ReportsDir}。");

        // ── 生成「待手工回填清单」 ──────────────────────────────────────────────
        // PDF 解析做不到 100%（个别年报的字体 PdfPig 和 pdftotext 都读不动），与其让体检表
        // 一直显示"待接入"、让人对着三个字发呆，不如直接给一份能照着干活的表：
        // 哪家、哪一期、缺哪几个数、翻年报的哪一章能找到。填完用【导入手工数据】写回来，
        // 之后重解析也不会覆盖（来源标 manual）。
        try
        {
            progress?.Report("正在生成待手工回填清单（要逐份核对财报里到底披露了哪些指标，请稍候）...");
            var listPath = ManualFillWorklist.Generate(
                _paths.CurrentDb, _paths.ReportsDir, _paths.ReportsDir, targets,
                s => progress?.Report(s));
            if (listPath != null)
            {
                var missing = File.ReadAllLines(listPath).Length - 1;
                progress?.Report($"⚠ 有 {missing} 项指标需要你过一遍，已生成清单：{listPath}"
                               + "（用 Excel 打开，看倒数第二列「OCR识别值」："
                               + "**有值的**是数字被转曲、只能靠 OCR 认出来的，对着 PDF 核一眼——"
                               + "认对了就别动，认错了才在最后一列填正确值；"
                               + "**空着的**是压根没解析出来的，请在最后一列填上。"
                               + "填完点【导入手工数据】写回，之后重新解析不会覆盖你确认过的值）。"
                               + "清单里**只列财报确实披露的项**——公司本身没有的指标"
                               + "（比如纯寿险公司没有综合成本率）不会让你去找。");
            }
            else progress?.Report("所有机构的核心监管指标都已齐全，也没有待核对的 OCR 值。");
        }
        catch (Exception ex) { result.Errors.Add($"生成手工回填清单失败：{ex.Message}"); }

        return result;
    }

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
                new SqliteMaintenance(_paths.CurrentDb).BuildIndexes(s => progress?.Report(s), ct);
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

    public async Task<FetchResult> RunFetchIndustryAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        if (_industryProvider == null)
            throw new InvalidOperationException("未配置行业分类数据源（IIndustryProvider）");

        void ForwardStatus(string msg) => progress?.Report(msg);
        _industryProvider.OnStatus += ForwardStatus;
        try
        {
            var sw = Stopwatch.StartNew();
            progress?.Report("开始抓取全市场行业分类（两所门类 + 新浪大类）...");
            var rows = await _industryProvider.GetAllAsync(ct);
            if (rows.Count == 0)
            {
                var msg = "行业分类返回空——接口可能变了，本轮跳过（不影响其它数据）";
                progress?.Report("⚠ " + msg);
                var empty = new FetchResult();
                empty.Errors.Add(msg);
                return empty;
            }

            var repo = new SqliteIndustryRepository(_paths.CurrentDb);
            repo.EnsureSchema();
            lock (_dbLock) { repo.Upsert(rows); }

            int withMajor = rows.Count(r => !string.IsNullOrEmpty(r.MajorName));
            progress?.Report($"行业分类完成：{rows.Count} 只（其中 {withMajor} 只有细分大类、其余只有门类），" +
                             $"用时 {FormatElapsed(sw.Elapsed)}。");
            return new FetchResult();
        }
        finally
        {
            _industryProvider.OnStatus -= ForwardStatus;
        }
    }

    /// <summary>
    /// 拉取财务报表（2026-07-31新增，FactorLab M4 基本面因子的数据基础）——对每只股票（含 2016 年后退市的，
    /// 它们是消除幸存者偏差的关键）从新浪抓三张报表全部历史的关键科目（见 <see cref="SinaFinancialProvider"/>），
    /// 整体覆盖写入 FinancialReport 表。
    ///
    /// **按报告期增量跳过**：本地已有"最近一个法定披露截止日已过的报告期"（如 7 月底时=一季报 0331）的股票
    /// 整只跳过、不发请求——首次全量约 5500 只 × 3 请求 ≈ 1.5~2 小时，之后每季度财报季各跑一次即可，
    /// 平时重复点几乎零成本。失败的股票本地报告期停在旧值，下次运行自动重试（自愈，无需失败名单）。
    /// </summary>
    /// <param name="maxCount">本轮最多抓多少只，覆盖 <see cref="MaxFinancialFetchPerRun"/>。
    /// 给"空闲时自动补"用：它可能只有到下一个定时任务之前的一小段时间，得按剩余时间压低只数，
    /// 保证在定时时刻前收尾，不跟定时任务撞车。null=用默认上限。</param>
    public async Task<FetchResult> RunFetchFinancialsAsync(IProgress<string>? progress,
        CancellationToken ct = default, int? maxCount = null)
    {
        if (_financialProvider == null)
            throw new InvalidOperationException("未配置财务报表数据源（IFinancialProvider）");

        void ForwardStatus(string msg) => progress?.Report(msg);
        _financialProvider.OnStatus += ForwardStatus;
        try
        {
            var sw = Stopwatch.StartNew();
            var repo = new SqliteFinancialRepository(_paths.CurrentDb);
            repo.EnsureSchema();

            int cap = maxCount is > 0 ? Math.Min(maxCount.Value, MaxFinancialFetchPerRun) : MaxFinancialFetchPerRun;
            var plan = GetFinancialFetchPlan(cap);
            var targets = plan.ThisRun;
            progress?.Report(plan.Describe(cap));
            if (targets.Count == 0) return new FetchResult();

            var errors = new List<string>();
            var failedCodes = new List<string>();
            int done = 0, wrote = 0, emptyCount = 0;

            // ⚠ **必须顺序处理，不能用 Task.WhenAll**（2026-08-27 修，这是个实打实踩过的坑）
            //
            // 每只票要抓 3 张报表，而每张表都要重新抢限速器的信号量。原来的写法是同时启动
            // 300 个任务，信号量只有 1 个名额且队列 FIFO，于是变成：
            //     票A表1 → 票B表1 → … → 票300表1 → 才轮到 票A表2 → …
            // 300 只票齐头并进、谁都差一张表，所以**谁都写不进库**。实测发出 420 个请求、
            // 零条写入、零错误、连进度都报不出来（done 一直是 0），看起来像卡死但其实在正常跑。
            // 要等三圈轮完（60 分钟）才会一次性全部写入。
            //
            // 顺序处理之后：一只票连续抓完 3 张表（约 12 秒）立刻落库，进度实时、随时可停、
            // 已抓的都算数。反正 maxConcurrency=1 已经把请求串行化了，并发写法只剩坏处。
            foreach (var code in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rows = await _financialProvider.GetAllAsync(code, ct);
                    if (rows.Count > 0)
                    {
                        repo.ReplaceByCode(code, rows);
                        wrote += rows.Count;
                    }
                    else
                    {
                        // 请求成功但一行都没解析出来——多半是该股没有这些报表（新上市/特殊标的），
                        // 单独计数：如果这个数很大，说明行名映射出问题了，不能静默混在"成功"里
                        emptyCount++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"财报 {code}: {ex.Message}");
                    failedCodes.Add(code);
                }

                done++;
                // 每 20 只报一次（顺序处理下单只约 12 秒，20 只≈4 分钟）。原来是 50 只，
                // 降速后那是 10 分钟一报，太稀疏，看着像卡住了。
                if (done % 20 == 0 || done == targets.Count)
                {
                    var per = sw.Elapsed.TotalSeconds / done;
                    var left = TimeSpan.FromSeconds(per * (targets.Count - done));
                    progress?.Report($"财务报表进度 ({done}/{targets.Count})，已写入 {wrote:N0} 条"
                                     + (failedCodes.Count > 0 ? $"，失败 {failedCodes.Count} 只" : "")
                                     + (emptyCount > 0 ? $"，{emptyCount} 只无报表数据" : "")
                                     + $"，已用时 {FormatElapsed(sw.Elapsed)}"
                                     + (done < targets.Count ? $"，预计还需 {FormatElapsed(left)}" : ""));
                }
            }

            // 本轮跑完后还剩多少——必须显式报出来。这个接口有每轮 300 只的上限（见
            // MaxFinancialFetchPerRun），"完成"两个字很容易被读成"全部补齐了"，实际可能只补了 5%。
            int stillPending = 0;
            try { stillPending = GetFinancialFetchPlan().AllPending.Count; }
            catch (Exception) { /* 只是提示，查不到就不提 */ }

            progress?.Report($"财务报表完成：抓取 {targets.Count} 只、写入 {wrote:N0} 条、失败 {failedCodes.Count} 只" +
                             (emptyCount > 0 ? $"、{emptyCount} 只无报表数据" : "") +
                             (failedCodes.Count > 0 ? "（失败的下次运行会自动重试）" : "") + $"，用时 {FormatElapsed(sw.Elapsed)}。" +
                             (stillPending > 0
                                 ? $"⚠ 还有 {stillPending} 只没补（本轮上限 {MaxFinancialFetchPerRun} 只）——" +
                                   "勾选界面上的【空闲时自动补财务】可以让它在程序空着时自己一轮一轮补完，" +
                                   "或者再点一次本按钮。已抓的不会重抓。"
                                 : "全部已补齐（报告期和科目集版本都是最新）。"));
            var result = new FetchResult();
            result.Errors.AddRange(errors);
            return result;
        }
        finally
        {
            _financialProvider.OnStatus -= ForwardStatus;
        }
    }

    /// <summary>
    /// 财务抓取的待抓清单（2026-08-27 抽出来公开）——界面要靠它回答"还剩多少没补"，
    /// 从而决定"空闲时自动补"要不要继续跑、什么时候可以停。
    /// </summary>
    public record FinancialFetchPlan(
        int TotalCodes,
        List<string> AllPending,
        List<string> ThisRun,
        int OutdatedPeriod,
        int StaleVersion,
        int WatchedCount,
        DateTime ExpectedPeriod,
        int Dormant = 0)
    {
        /// <summary>还剩多少只没补（本轮之外的）。</summary>
        public int Remaining => AllPending.Count - ThisRun.Count;

        public string Describe(int cap) =>
            $"财务报表：目标 {TotalCodes} 只，需要抓 {AllPending.Count} 只" +
            $"——其中 {OutdatedPeriod} 只报告期落后（按各自的**实际披露日**判断；" +
            $"查不到披露记录的那些按法定截止日算，当前是 {ExpectedPeriod:yyyy-MM-dd}）、" +
            $"{StaleVersion} 只科目集版本落后（本地数据是旧版代码抓的、科目不全，当前 v{FinancialKeys.Version}）；" +
            $"{TotalCodes - AllPending.Count - Dormant} 只已是最新、跳过。" +
            (Dormant > 0
                ? $"另有 {Dormant} 只报告期虽然落后，但已经一年多没有过任何成交（退市/长期停牌），" +
                  "公司本身不再披露新报告期，不再反复去问；哪天恢复交易，K线一到它自己会回到名单里。"
                : "") +
            (WatchedCount > 0 ? $"你关注的 {WatchedCount} 只（自选/底仓/主动仓）已排到最前。" : "") +
            (Remaining > 0 ? $"⚠ 本轮上限 {cap} 只，其余 {Remaining} 只下轮自动继续（抓过的不会重抓）。" : "") +
            (ThisRun.Count > 0
                ? $"本轮 {ThisRun.Count} 只 × 3 个请求，该接口已降速到约 10 请求/分钟，预计 {ThisRun.Count * 3 / 10} 分钟..."
                : "全部已是最新，无需抓取。");
    }

    /// <summary>
    /// 算出财务数据还有哪些票要抓。**不发任何网络请求**，只查本地库，所以界面可以随时调用
    /// （"空闲时自动补"每隔几分钟问一次也没有负担）。
    ///
    /// 增量判断有**两个**条件，缺一不可（2026-08-27 修）：
    ///   ① 报告期不够新 → 要抓
    ///   ② 科目集版本落后 → 也要抓
    /// 只看①的话，扩充科目后老数据的 report_date 仍是"最新"，新科目永远补不上：那天科目从 8 个
    /// 扩到 52 个之后跑全量拉取，5780 只里 5552 只被判定无需重抓，44 个新科目一条都没进库。
    /// 见 <see cref="FinancialKeys.Version"/>。
    /// </summary>
    /// <summary>
    /// 多久没有过成交就算"已经不交易了"，财务报表不再反复去问它。
    ///
    /// 取一年是往保守里选：停牌三五个月的公司照样会披露半年报，一年一根K线都没有的
    /// 基本都在退市流程里了。实测这个阈值筛掉 106 只、留下 8 只，没有误伤还在交易的。
    /// </summary>
    private static readonly TimeSpan DormantAfterNoTrading = TimeSpan.FromDays(365);

    public FinancialFetchPlan GetFinancialFetchPlan(int? cap = null)
    {
        var repo = new SqliteFinancialRepository(_paths.CurrentDb);
        repo.EnsureSchema();

        // 目标：在市个股 + 2016年后退市的（回测池同款；更早退市的没有K线、抓了也用不上）
        var codes = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Select(s => s.Code).ToList();
        var delisted = new SqliteDelistedRepository(_paths.CurrentDb).GetAll()
            .Where(r => r.DelistDate == null || r.DelistDate.Value.Year >= 2016)
            .Select(r => r.Code);
        codes = codes.Concat(delisted).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        var stateByCode = repo.GetFetchStateByCode();
        var expected = LatestExpectedReportPeriod(DateTime.Today);

        // 「这只票到底披露了没有」——按只看实际披露日，而不是拿法定截止日一刀切。
        // 详见 SqliteEarningsScheduleRepository.GetLatestDisclosedPeriodByCode 的注释：
        // 66% 的公司挤在法定截止日前那五天披露，等截止日过完再认这一期，5478 只会同时
        // 涌进待补队列，按每轮 300 只要补三四天。
        // 查不到记录的（新股/B股/老退市股）不在这个字典里，下面会退回 expected 兜底。
        var disclosed = new SqliteEarningsScheduleRepository(_paths.CurrentDb)
            .GetLatestDisclosedPeriodByCode(DateTime.Today);

        // 「已经不交易了就别再问」（2026-09-03 用户提）：ST/退市/长期停牌那批，公司本身早就
        // 不出新报告期了，每轮拿去问一遍纯属浪费配额——实测 114 只"报告期落后"里，
        // 有 106 只一年多没有过任何一根K线，真正还在交易的只有 1 只。
        //
        // 判据故意用"最近还有没有成交"而不是退市标记，因为它**自愈**：哪天恢复交易，
        // K线一到 gap 就缩回来，这只票自动回到待抓名单，不需要谁去手工恢复。
        // 基准取上证指数的最新交易日（本地判交易日一贯拿它当锚）。
        var lastBarByCode = new SqliteBarRepository(_paths.CurrentDb)
            .GetLatestPeriodStartByCode(Granularity.Day);
        var marketLatest = lastBarByCode.TryGetValue("sh000001", out var mkt) ? mkt : DateTime.Today;
        var dormantBefore = marketLatest - DormantAfterNoTrading;

        int stale = 0, outdatedPeriod = 0, dormant = 0;
        var pending = new List<string>();
        foreach (var c in codes)
        {
            if (!stateByCode.TryGetValue(c, out var st))
            {
                // 没有状态记录：要么从没抓过，要么是这张表出现之前抓的（版本按 0 算）
                pending.Add(c);
                stale++;
                continue;
            }
            // 该抓到哪一期：这只票已经披露的最新一期；没有披露记录的退回法定截止日。
            // ⚠ 兜底方向只能是"多取"——查不到就按老判据来，绝不能因为查不到而漏掉一只。
            var target = disclosed.TryGetValue(c, out var d) ? d : expected;
            bool periodOld = st.ReportDate.Date < target;
            bool versionOld = st.KeysVersion < FinancialKeys.Version;
            if (!periodOld && !versionOld) continue;

            // ⚠ 只有"报告期落后"这一条才认停牌豁免。科目集版本落后的照抓不误——
            //    那是本地数据不全（旧版代码抓的科目少），历史财报还在数据源上，
            //    补回来对回测有用，跟这只票现在还交不交易没关系。
            if (periodOld && !versionOld
                && lastBarByCode.TryGetValue(c, out var lastBar) && lastBar < dormantBefore)
            {
                dormant++;
                continue;
            }

            pending.Add(c);
            if (versionOld) stale++; else outdatedPeriod++;
        }

        // 自选/底仓/主动仓里的票排最前——它们是真正会被拿来分析的，先补上就能立刻用；
        // 剩下几千只不看的票慢慢磨。
        var watched = ReadWatchedCodes();
        int watchedCount = pending.Count(watched.Contains);
        if (watchedCount > 0)
            pending = pending
                .OrderByDescending(watched.Contains)
                .ThenBy(c => c, StringComparer.Ordinal)
                .ToList();

        var thisRun = pending.Take(cap is > 0 ? cap.Value : MaxFinancialFetchPerRun).ToList();
        return new FinancialFetchPlan(codes.Count, pending, thisRun, outdatedPeriod, stale, watchedCount, expected, dormant);
    }

    /// <summary>
    /// 读出"用户真正关注的票"——自选股 + 底仓 + 主动仓（2026-08-27）。财务抓取拿它做优先级排序。
    ///
    /// 为什么 Fetcher 能读到 Analyzer 的状态文件：两个 exe 装在同一个目录，<see cref="FetchPaths.BaseDir"/>
    /// 和 AnalyzerPaths.BaseDir 算出来是同一个 data 文件夹。这里只读 code 字段、不反序列化成完整
    /// 模型（那些模型在 Analyzer 项目里，Data 层引用不到，也没必要）。
    ///
    /// 文件不存在（Fetcher 单独部署、或用户还没建过自选）就返回空集合，排序退化成纯代码序。
    /// </summary>
    private HashSet<string> ReadWatchedCodes()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "watchlist.json", "core-positions.json" })
        {
            var path = Path.Combine(_paths.BaseDir, file);
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("Code", out var c) || el.TryGetProperty("code", out c))
                    {
                        var code = c.GetString();
                        if (!string.IsNullOrWhiteSpace(code)) result.Add(code);
                    }
            }
            catch (Exception)
            {
                // 读不了/格式坏了不影响抓取，只是失去优先级排序
            }
        }
        return result;
    }

    /// <summary>
    /// 拉取分红送配（2026-07-31新增）——对每只在市个股，从新浪分红派息页(<see cref="SinaDividendProvider"/>)
    /// 抓历年全部分红方案，按 code 整体覆盖写入 Dividend 表。库里原本没有任何分红明细（前复权把分红效果
    /// 揉进了价格、反而看不出哪天除权派了多少），做股息率因子/核对除权除息日都得靠这张表。
    ///
    /// 分红一年一次为主、慢变，跟股东数据一样每次全量刷新（不做按期跳过）；逐只失败进
    /// <see cref="Manifest.FailedDividendCodes"/>，可用"重新拉取失败股票"重试。较慢（每只1请求，全市场约5500只）。
    /// </summary>
    public async Task<FetchResult> RunFetchDividendAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        if (_dividendProvider == null || _dividendRepository == null)
            throw new InvalidOperationException("未配置分红数据源（IDividendProvider/IDividendRepository）");

        void Forward(string s) => progress?.Report(s);
        _dividendProvider.OnStatus += Forward;
        try
        {
            if (!File.Exists(_paths.CurrentDb))
                throw new InvalidOperationException("本地还没有任何数据，无法拉取分红，请先执行一次\"拉取全部\"");
            _dividendRepository.EnsureSchema();
            var stocks = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb);
            if (stocks.Count == 0)
                throw new InvalidOperationException("本地股票列表为空，无法拉取分红，请先执行一次\"拉取全部\"");

            var errors = new ConcurrentBag<string>();
            var failed = new ConcurrentBag<string>();
            var attempted = stocks.Select(s => s.Code).ToList();
            var sw = Stopwatch.StartNew();
            int completed = 0, withData = 0, wrote = 0, wroteRights = 0;
            progress?.Report($"开始拉取分红送配（含配股），共 {stocks.Count} 只，逐只抓、较慢...");

            var tasks = stocks.Select(async stock =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // 配股跟分红在源页面上是同一页的两张表，一次请求拿两份——分开抓等于把
                    // 5500 只的请求数翻倍（限流 3 并发/1 秒，多花半小时），没有任何好处。
                    var (rows, rights) = await _dividendProvider.GetAllWithRightsAsync(stock.Code, ct);
                    if (rows.Count > 0)
                    {
                        lock (_dbLock) _dividendRepository.ReplaceByCode(stock.Code, rows);
                        Interlocked.Increment(ref withData);
                        Interlocked.Add(ref wrote, rows.Count);
                    }
                    if (rights.Count > 0)
                    {
                        lock (_dbLock) SqliteRightsIssueUpsert.ReplaceByCode(_paths.CurrentDb, stock.Code, rights);
                        Interlocked.Add(ref wroteRights, rights.Count);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{stock.Code}: {ex.Message}"); failed.Add(stock.Code); }

                int done = Interlocked.Increment(ref completed);
                if (done % 50 == 0 || done == stocks.Count)
                    progress?.Report($"分红送配 {done}/{stocks.Count}（有分红 {withData} 只、共写 {Volatile.Read(ref wrote):N0} 条、"
                                   + $"配股 {Volatile.Read(ref wroteRights):N0} 条、失败 {failed.Count}，已用时 {FormatElapsed(sw.Elapsed)}）");
            });
            await Task.WhenAll(tasks);

            lock (_dbLock)
            {
                var manifest = _manifestStore.Load();
                manifest.FailedDividendCodes = ComputeUpdatedFailedCodes(manifest.FailedDividendCodes, attempted, failed.ToList());
                _manifestStore.Save(manifest);
            }

            progress?.Report($"分红送配完成：{withData} 只有分红、共写 {wrote:N0} 条、失败 {failed.Count} 只" +
                             (failed.Count > 0 ? "（可点\"重新拉取失败股票\"重试）" : "") + $"，用时 {FormatElapsed(sw.Elapsed)}。");
            var result = new FetchResult();
            result.Errors.AddRange(errors);
            return result;
        }
        finally
        {
            _dividendProvider.OnStatus -= Forward;
        }
    }

    /// <summary>"重新拉取失败股票"里针对分红失败名单的重试（2026-07-31新增）——逐只精确重试，更新
    /// 失败名单，可反复点击直到清零。</summary>
    private async Task RetryDividendAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        if (_dividendProvider == null || _dividendRepository == null) return;
        _dividendRepository.EnsureSchema();
        void Forward(string s) => progress?.Report(s);
        _dividendProvider.OnStatus += Forward;

        var failed = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();
        int completed = 0;
        progress?.Report($"重试分红失败 {codes.Count} 只...");
        try
        {
            var tasks = codes.Select(async code =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rows = await _dividendProvider.GetAllAsync(code, ct);
                    if (rows.Count > 0)
                        lock (_dbLock) _dividendRepository.ReplaceByCode(code, rows);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { progress?.Report($"{code} 分红重试仍失败：{ex.Message}"); failed.Add(code); }

                int done = Interlocked.Increment(ref completed);
                if (done % 50 == 0 || done == codes.Count)
                    progress?.Report($"分红重试 {done}/{codes.Count}（已用时 {FormatElapsed(sw.Elapsed)}）");
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            _dividendProvider.OnStatus -= Forward;
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedDividendCodes = ComputeUpdatedFailedCodes(manifest.FailedDividendCodes, codes, failed.ToList());
            _manifestStore.Save(manifest);
        }
    }

    /// <summary>今天应该已经能拿到的最新报告期——按法定披露截止日：一季报 4-30、半年报 8-31、
    /// 三季报 10-31、年报次年 4-30。用于财报抓取的"按报告期跳过"。</summary>
    internal static DateTime LatestExpectedReportPeriod(DateTime today)
    {
        var candidates = new List<(DateTime Period, DateTime Deadline)>();
        for (int y = today.Year - 1; y <= today.Year; y++)
        {
            candidates.Add((new DateTime(y, 3, 31), new DateTime(y, 4, 30)));
            candidates.Add((new DateTime(y, 6, 30), new DateTime(y, 8, 31)));
            candidates.Add((new DateTime(y, 9, 30), new DateTime(y, 10, 31)));
            candidates.Add((new DateTime(y, 12, 31), new DateTime(y + 1, 4, 30)));
        }
        return candidates.Where(c => c.Deadline <= today).Max(c => c.Period);
    }
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

    public string Summarize() =>
        $"跳过 {_skipped} 只（本地已是最新，未发起请求）、抓到新数据 {_fetchedWithNewData} 只、" +
        $"请求成功但无新数据 {_fetchedButEmpty} 只（比如请求的日期不是交易日）、失败 {_failed} 只";
}
