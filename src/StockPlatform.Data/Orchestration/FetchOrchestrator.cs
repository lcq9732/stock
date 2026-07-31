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
/// on the Analyzer side (it only ever reads a manually-copied `total.sqlite`), so producing those
/// files served no purpose. The real, current workflow is: run the Fetcher, then manually copy
/// <see cref="FetchPaths.CurrentDb"/> to the Analyzer's data folder as `total.sqlite`.
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
public class FetchOrchestrator
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

    /// <summary>单轮最多修正多少只漂移股票——分红季一天可能上百只，全修会让当轮变得很长。
    /// 没修完的下一轮还会被检测到（库里仍是旧值），检测本身是自愈的，所以可以安全地限量。</summary>
    private const int MaxDriftRepairPerRun = 200;

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
        IDividendRepository? dividendRepository = null)
    {
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

    /// <summary>
    /// 抓取板块数据（概念/题材 + 行业）及各板块成分股，整体覆盖写入本地库（见 IBoardRepository）。
    /// 独立于 K线/市值/资金流的抓取——是一个单独的按钮触发（"拉取板块"），因为板块热点是"当下快照"、
    /// 跟历史K线的增量抓取不是一回事，也不想让它拖慢主抓取。
    /// </summary>
    public async Task<FetchResult> RunFetchBoardsAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var errors = new ConcurrentBag<string>();
        void Forward(string s) => progress?.Report(s);
        _boardFetcher.OnStatus += Forward;
        try
        {
            _boardRepository.EnsureSchema();
            var all = new List<Board>();
            foreach (var (type, label) in new[] { (BoardType.Concept, "概念/题材"), (BoardType.Industry, "行业") })
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"抓取{label}板块列表...");
                var list = await _boardFetcher.FetchBoardListAsync(type, ct);
                progress?.Report($"{label}板块 {list.Count} 个，开始抓取成分股...");
                for (int i = 0; i < list.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        list[i].MemberCodes = await _boardFetcher.FetchMembersAsync(list[i].BoardCode, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add($"{label}板块「{list[i].Name}」成分股抓取失败：{ex.Message}");
                    }
                    if ((i + 1) % 20 == 0 || i + 1 == list.Count)
                        progress?.Report($"{label}板块成分股：{i + 1}/{list.Count}");
                }
                all.AddRange(list);
            }

            _boardRepository.ReplaceAll(all);
            progress?.Report($"板块数据抓取完成：共 {all.Count} 个板块，已写入本地库。");

            // 拉完板块紧接着合成板块指数（不联网，用本地已有个股K线按新成分重算）——2026-07-16 合并为
            // 一步，不再需要单独点"合成板块指数"。日常的重算仍并在"拉取全部/当天"末尾。
            var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
            currentRepo.EnsureSchema();
            SynthesizeBoardIndexCore(currentRepo, errors, progress, ct);
        }
        finally
        {
            _boardFetcher.OnStatus -= Forward;
        }
        return new FetchResult { Errors = errors.ToList() };
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
                if (bars.Count > 0) { withBars++; totalBars += bars.Count; synthesizedMeta.Add((board.BoardCode, board.Name)); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"板块「{board.Name}」({board.BoardCode}) 合成失败：{ex.Message}"); }
            if (++done % 40 == 0 || done == boards.Count)
                progress?.Report($"合成板块指数：{done}/{boards.Count}（已生成 {withBars} 个板块、{totalBars} 根日K）");
        }
        // 有指数K的板块名称写进 StockMeta（type=board）——让"查询"页能搜到板块、看行情（不影响个股选股）。
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

        await FetchMarketCapAsync(stocks.Select(s => s.Code).ToList(), progress, ct);
        await FetchNetInflowAsync(stocks.Select(s => s.Code).ToList(), today, exactDayOnly: false, progress, ct);
        await FetchAnnouncementsAsync(
            announcementKeywords, DateOnly.FromDateTime(today.AddDays(-AnnouncementLookbackDaysForFetchAll)),
            DateOnly.FromDateTime(today), progress, ct);

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();
        var driftedCodes = new ConcurrentBag<string>();

        await FetchIndexBarsAsync(source, today, lookbackYears, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        int completed = 0;
        var tasks = stocks.Select(stock =>
        {
            // Resume point is per-stock, not a single global watermark — an interrupted run or a
            // stock that failed last time just gets its gap re-requested next time, since nothing
            // advanced its latest-date unless the fetch actually succeeded (see class remarks).
            // "今天"这一天是特例：本地已经有记录了，但如果是盘中抓的，还不能算数——要看抓取时间
            // 是不是已经过了收盘（IsConfirmedFinal），过了才跳过，没过就还要再抓一次去覆盖修正。
            DateTime start;
            lock (_dbLock)
            {
                var info = currentRepo.GetLatestBarInfo(stock.Code, Granularity.Day);
                if (info == null)
                    start = today.AddYears(-lookbackYears);
                else if (info.Value.PeriodStart.Date < today.Date)
                    start = info.Value.PeriodStart.AddDays(1);
                else
                    start = IsConfirmedFinal(info.Value.FetchedAt, today) ? today.AddDays(1) : today;
            }
            return ProcessOneStockAsync(stock.Code, source, start, today, currentRepo, errors, failedCodes, stats, progress, stocks.Count, () => Interlocked.Increment(ref completed), sw, ct,
                Granularity.Day, overwrite: false, driftedCodes: driftedCodes);
        });
        await Task.WhenAll(tasks);

        // 复权基准漂移修正：上面抓取时顺带发现的（分红/送转导致数据源基准变了），把更早的历史也重抓覆盖
        await RepairDriftedHistoryAsync(source, driftedCodes.ToList(), currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 后复权日K（回测专用，见 FetchHfqBarsAsync）——只对个股，指数/ETF不需要。
        await FetchHfqBarsAsync(source, stocks.Select(s => s.Code).ToList(),
            code => HfqWatermarkWindow(currentRepo, code, today, lookbackYears),
            currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 个股抓完后，末尾顺带跑 ETF 和板块指数合成（合成放最后，要读当天个股K线）。
        var etfCodes = await FetchEtfBarsAsync(source, today, lookbackYears, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 退市股收尾：只补"本地已跟踪过、但最后一根K线还早于终止日"的那几只（见方法注释）。
        var delistedCodes = await CatchUpDelistedTailsAsync(source, currentRepo, errors, failedCodes, stats, progress, sw, ct);

        SynthesizeBoardIndexCore(currentRepo, errors, progress, ct);

        // 每日数据（融资余额/龙虎榜）并入主流程当天抓取——非致命，失败只记 error 不影响 K线；历史用各自
        // 的"回补"按钮补齐（见 RunFetchMarginAsync / RunFetchLhbAsync）。
        await FetchMarginOneDayAsync(today, errors, progress, ct);
        await FetchLhbOneDayAsync(today, errors, progress, ct);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        var attempted = stocks.Select(s => s.Code)
            .Concat(MarketIndexCatalog.All.Select(i => i.Symbol))
            .Concat(etfCodes)
            .Concat(delistedCodes).ToList();
        return FinishFetchRun(errors, "拉取全部", attempted, failedCodes);
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
        var newCodes = await FetchMarketCapAsync(stocks.Select(s => s.Code).ToList(), progress, ct);
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

        int completed = 0;
        var tasks = stocks.Select(stock =>
        {
            // "拉取当天"请求的是某个具体日期（往往就是今天）——跟"拉取全部"不一样，这里没有一个
            // "水位线"概念可用（本来就是"不管之前抓到哪天了，就抓这一天"），所以直接查这个具体日期
            // 本地是否已经有记录、以及是不是收盘后确认的。past（非今天）的日期一旦有记录就必然是
            // 最终的（过去的交易日不会再变），IsConfirmedFinal对任何早于今天的date天然成立。
            DateTime start;
            lock (_dbLock)
            {
                var latest = currentRepo.GetLatestBarInfo(stock.Code, Granularity.Day);
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
                    var existing = currentRepo.Query(stock.Code, Granularity.Day, day, day).FirstOrDefault();
                    start = (existing != null && IsConfirmedFinal(existing.FetchedAt, day)) ? day.AddDays(1) : day;
                }
            }
            return ProcessOneStockAsync(stock.Code, source, start, day, currentRepo, errors, failedCodes, stats, progress, stocks.Count, () => Interlocked.Increment(ref completed), sw, ct,
                Granularity.Day, overwrite: false, driftedCodes: driftedCodes);
        });
        await Task.WhenAll(tasks);

        // 复权基准漂移修正（见 RepairDriftedHistoryAsync）——日常入口就是这里，所以放在个股抓完之后
        await RepairDriftedHistoryAsync(source, driftedCodes.ToList(), currentRepo, errors, failedCodes, stats, progress, sw, ct);

        // 后复权日K（回测专用）——跟 ETF/指数一样走自己的水位线增量而不是"只抓这一天"，所以升级后
        // 第一次跑"拉取当天"会自动把最近 DefaultLookbackYears 年的后复权补上；要一次补齐十年历史
        // 仍需跑一次"拉取区间数据"（见 FetchHfqBarsAsync 注释）。
        await FetchHfqBarsAsync(source, stocks.Select(s => s.Code).ToList(),
            code => HfqWatermarkWindow(currentRepo, code, day, DefaultLookbackYears),
            currentRepo, errors, failedCodes, stats, progress, sw, ct);

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

        // 每日数据（融资余额/龙虎榜）并入"拉取当天"——抓的是本次指定的那一天。
        await FetchMarginOneDayAsync(day, errors, progress, ct);
        await FetchLhbOneDayAsync(day, errors, progress, ct);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        var attempted = stocks.Select(s => s.Code)
            .Concat(MarketIndexCatalog.All.Select(i => i.Symbol))
            .Concat(etfCodes)
            .Concat(delistedCodes).ToList();
        return FinishFetchRun(errors, "补指定历史日", attempted, failedCodes);
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
    /// 当前基准重写）。用来一次性抹平历史上分批入库造成的复权基准接缝，见 <see cref="RepairDriftedHistoryAsync"/>。
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

        progress?.Report($"{rangeLabel}补齐完毕，总用时 {FormatElapsed(sw.Elapsed)}。");
        var attempted = stockCodes
            .Concat(MarketIndexCatalog.All.Select(i => i.Symbol))
            .Concat(etfCodes)
            .Concat(delistedCodes).ToList();
        return FinishFetchRun(errors, $"拉取{rangeLabel}", attempted, failedCodes);
    }

    /// <summary>
    /// 复权基准漂移的全历史修正（2026-07-30新增）——<see cref="ProcessOneStockAsync"/> 在日常抓取中
    /// 顺带发现某只股票的历史值与数据源当前基准对不上（说明它分红或送转了），只能就手上那一页（≈2.5年）
    /// 先覆盖掉；更早的历史要靠这里从本地最早一根重抓一遍并整体覆盖。
    ///
    /// 为什么需要：数据源的前复权是"原价 − 之后累计分红送配"，基准随抓取时点变化；而我们的历史是分批
    /// 入库、且 INSERT OR IGNORE 不覆盖，于是同一只股票不同时间段落在不同基准上，接缝处出现假跳空
    /// （实测有股票在接缝处虚增 50%）。后复权（day_hfq）不受影响，所以回测那条线本来就是干净的，
    /// 这里修的是界面展示和各选股法用的前复权序列。
    ///
    /// 限量 <see cref="MaxDriftRepairPerRun"/> 只：分红季一天可能上百只，全修会让当轮很长；没修完的
    /// 下一轮还会被重新检测出来（库里仍是旧值），检测是自愈的，所以限量是安全的。
    /// </summary>
    private async Task RepairDriftedHistoryAsync(
        NamedBarSource source, IReadOnlyList<string> driftedCodes, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        if (driftedCodes.Count == 0) return;

        var earliestByCode = currentRepo.GetEarliestPeriodStartByCode(Granularity.Day);
        // 只有历史比"手上那一页"更长的才需要重抓——短历史的股票刚才已经整段覆盖好了
        var pageCovered = DateTime.Today.AddDays(-DriftCheckLookbackDays).Date;
        var targets = driftedCodes.Distinct(StringComparer.Ordinal)
            .Where(c => earliestByCode.TryGetValue(c, out var e) && e.Date < pageCovered)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
        {
            progress?.Report($"复权基准修正：{driftedCodes.Distinct().Count()} 只已在抓取时就地覆盖完毕，无需重抓更早历史。");
            return;
        }

        bool capped = targets.Count > MaxDriftRepairPerRun;
        if (capped) targets = targets.Take(MaxDriftRepairPerRun).ToList();
        progress?.Report($"复权基准修正：{targets.Count} 只股票的更早历史需要按新基准重抓覆盖" +
                         (capped ? $"（本轮上限 {MaxDriftRepairPerRun} 只，其余下轮自动继续）" : "") + "...");

        int done = 0;
        await Task.WhenAll(targets.Select(code =>
        {
            var start = earliestByCode[code];
            return ProcessOneStockAsync(code, source, start, DateTime.Today, currentRepo, errors, failedCodes,
                stats, progress, targets.Count, () => Interlocked.Increment(ref done), sw, ct,
                Granularity.Day, overwrite: true);
        }));
        progress?.Report($"复权基准修正完成（{targets.Count} 只）。");
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
    private async Task<List<string>> FetchHfqBarsAsync(
        NamedBarSource source, IReadOnlyList<string> codes, Func<string, (DateTime Start, DateTime End)> windowFor,
        SqliteBarRepository currentRepo, ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes,
        FetchStats stats, IProgress<string>? progress, Stopwatch sw, CancellationToken ct, string label = "")
    {
        if (!source.Fetcher.SupportsHfq)
        {
            progress?.Report($"（数据源 {source.Name} 不提供后复权，跳过回测用的后复权日线——要补请把数据源切到 Tencent）");
            return new List<string>();
        }
        if (codes.Count == 0) return new List<string>();

        progress?.Report($"开始抓{label}后复权日K（{codes.Count} 只，回测专用；前复权已有的不受影响）...");

        // 起飞前先探一只：接口挂了/被限流/换了返回格式时，立刻停这一轮并说清楚原因，
        // 而不是对着几千只股票空跑几小时（用户 2026-07-30 反馈：取不到数据就该直接停）。
        var probeCode = codes[0];
        try
        {
            var (_, probeBars) = await source.Fetcher.FetchAsync(probeCode, Granularity.DayHfq,
                DateTime.Today.AddYears(-1), DateTime.Today, ct);
            if (probeBars.Count == 0)
                throw new InvalidOperationException("接口返回空数据（可能是返回格式变了，或该代码已无数据）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = $"后复权探测失败（用 {probeCode} 试抓最近一年）：{ex.Message}。本轮跳过后复权，不做无谓的空跑——" +
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
            await Task.WhenAll(codes.Select(async code =>
            {
                var (s, e) = windowFor(code);
                await ProcessOneStockAsync(code, source, s, e, currentRepo, errors, phaseFailed, stats, progress,
                    codes.Count, () => Interlocked.Increment(ref done), sw, abortCts.Token, Granularity.DayHfq);

                int finished = Volatile.Read(ref done);
                if (finished >= HfqAbortCheckAfter && phaseFailed.Count > finished * 0.9 && !abortCts.IsCancellationRequested)
                {
                    aborted = true;
                    progress?.Report($"⚠ {label}后复权连续失败（已完成 {finished} 只、失败 {phaseFailed.Count} 只），主动中止本轮后复权抓取。" +
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
            ? $"{label}后复权日K已中止（失败 {phaseFailed.Count} 只）。"
            : $"{label}后复权日K完成（{codes.Count} 只，失败 {phaseFailed.Count} 只）。");
        return codes.ToList();
    }

    /// <summary>"拉取全部/当天"给后复权用的水位线窗口：跟前复权同一套规则，只是读 day_hfq 自己的水位线
    /// （所以第一次跑会按 <see cref="DefaultLookbackYears"/> 年回看，不会因为前复权已经是最新就跳过）。</summary>
    private (DateTime Start, DateTime End) HfqWatermarkWindow(
        SqliteBarRepository currentRepo, string code, DateTime end, int lookbackYears)
    {
        lock (_dbLock)
        {
            var info = currentRepo.GetLatestBarInfo(code, Granularity.DayHfq);
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

        if (failedCodesList.Count == 0 && failedMarketCapCodes.Count == 0 && failedNetInflowCodes.Count == 0
            && failedIndexConsCodes.Count == 0 && failedIndexWeightCodes.Count == 0 && failedShareholderCodes.Count == 0
            && failedDividendCodes.Count == 0)
        {
            progress?.Report("目前没有记录到抓取失败的股票，不需要重试");
            return new FetchResult();
        }

        if (failedMarketCapCodes.Count > 0)
            await FetchMarketCapAsync(failedMarketCapCodes, progress, ct);

        if (failedNetInflowCodes.Count > 0)
            await FetchNetInflowAsync(failedNetInflowCodes, DateTime.Today, exactDayOnly: false, progress, ct);

        // 指数成分/权重的失败重试（2026-07-16新增）——跟市值/资金流一样，在K线重试之前处理，
        // 各自用自己的失败名单精确重试，可反复点击直到清零（见 RetryIndexAsync）。
        if (failedIndexConsCodes.Count > 0 || failedIndexWeightCodes.Count > 0)
            await RetryIndexAsync(failedIndexConsCodes, failedIndexWeightCodes, progress, ct);

        // 股东数据的失败重试（2026-07-16新增）——逐只精确重试，见 RetryShareholderAsync。
        if (failedShareholderCodes.Count > 0)
            await RetryShareholderAsync(failedShareholderCodes, progress, ct);

        // 分红送配的失败重试（2026-07-31新增）——逐只精确重试，见 RetryDividendAsync。
        if (failedDividendCodes.Count > 0)
            await RetryDividendAsync(failedDividendCodes, progress, ct);

        if (failedCodesList.Count == 0)
        {
            progress?.Report("K线没有失败的股票需要重试");
            return new FetchResult();
        }

        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();
        var today = DateTime.Today;
        var sw = Stopwatch.StartNew();
        progress?.Report($"重新拉取上次失败的K线，共 {failedCodesList.Count} 只，数据源：{source.Name}");

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();
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

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "重新拉取失败股票", failedCodesList, failedCodes);
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
    /// 由调用方在本轮末尾对这些股票做全历史重抓（<see cref="RepairDriftedHistoryAsync"/>）。</param>
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
    /// MidCapPullbackAnalysisEngine 的条件4判不满足，不会导致程序崩溃或影响其他方法）。写入用
    /// Upsert（主键是 code+metric_key+as_of_date）——同一天内如果跑了不止一次，后面这次会覆盖
    /// 前面那次，只认最后一次抓到的值，不是"当天已经有了就跳过"（比如某天先在盘中跑过一次、收盘
    /// 后又跑了一次，库里最终留下的是收盘后那次更准的值，不会被盘中那次锁住）。
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
    private async Task<List<(string Code, string Name)>> FetchMarketCapAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        List<string> failedThisRun;
        var newlyDiscovered = new List<(string Code, string Name)>();
        try
        {
            progress?.Report("正在扫描全市场股票列表以刷新流通市值（顺带发现新股），会比较慢...");
            var result = await _marketCapFetcher.GetMarketCapsAsync(codes, progress, ct);
            var fetchedAt = DateTime.Now;
            var metrics = result.Entries.Select(e => new FundamentalMetric
            {
                Code = e.Code,
                MetricKey = MetricKeys.CirculatingMarketCap,
                AsOfDate = DateTime.Today,
                Value = e.CirculatingMarketCap,
                Source = "EastMoney",
                FetchedAt = fetchedAt,
            });
            _fundamentalRepository.Upsert(metrics);
            progress?.Report($"流通市值写入完成，共 {result.Entries.Count} 条");

            if (result.NewlyDiscoveredCodes.Count > 0)
            {
                SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, result.NewlyDiscoveredCodes);
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

        return newlyDiscovered;
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
        IReadOnlyCollection<string> attemptedCodes, ConcurrentBag<string> failedCodesThisRun)
    {
        var manifest = _manifestStore.Load();
        manifest.LastFetchAt = DateTime.Now;
        manifest.LastFetchKind = fetchKind;
        manifest.FailedCodes = ComputeUpdatedFailedCodes(manifest.FailedCodes, attemptedCodes, failedCodesThisRun);
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
        if (!File.Exists(_paths.CurrentDb))
            return new DataStatus { LastFetchAt = manifest.LastFetchAt, LastFetchKind = manifest.LastFetchKind };

        var repo = new SqliteBarRepository(_paths.CurrentDb);
        return new DataStatus
        {
            EarliestDay = repo.GetOverallEarliestPeriodStart(Granularity.Day),
            LatestDay = repo.GetOverallLatestPeriodStart(Granularity.Day),
            LastFetchAt = manifest.LastFetchAt,
            LastFetchKind = manifest.LastFetchKind,
        };
    }

    /// <summary>K线/市值/资金净流入三份失败名单加起来的股票数（2026-07-09起——之前只统计K线）——
    /// 让Fetcher UI在这三者任意一个有失败记录时都能显示/启用"重新拉取失败股票"按钮。同一只股票
    /// 如果在多份名单里都出现会被重复计数（比如K线和资金净流入都失败），这样数字才能反映"总共
    /// 还有多少件事没做完"，而不是去重后的股票数。</summary>
    public int GetFailedCodeCount()
    {
        var manifest = _manifestStore.Load();
        return manifest.FailedCodes.Count + manifest.FailedMarketCapCodes.Count + manifest.FailedNetInflowCodes.Count
             + manifest.FailedIndexConsCodes.Count + manifest.FailedIndexWeightCodes.Count
             + manifest.FailedShareholderCodes.Count + manifest.FailedDividendCodes.Count;
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
    /// 独立按钮，不掺进主流程（股东数据季度级慢变，不必每天跑）。
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
        int completed = 0, withData = 0;
        progress?.Report($"开始拉取股东数据（户数+十大股东+十大流通股东），共 {stocks.Count} 只，逐只抓、较慢...");

        var tasks = stocks.Select(async stock =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await _shareholderProvider.GetAsync(stock.Code, ct);
                if (data.Counts.Count > 0 || data.TopHolders.Count > 0)
                {
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

    /// <summary>抓某一天的融资余额并写库（非致命：失败只记 error，不影响主流程其他步骤）——供"拉取全部/
    /// 当天"并入调用。历史用"回补融资余额"补齐。</summary>
    private async Task FetchMarginOneDayAsync(DateTime day, ConcurrentBag<string> errors, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            _marginRepository.EnsureSchema();
            var d = DateOnly.FromDateTime(day);
            var rows = await _marginProvider.GetDetailAsync(d, ct);
            if (rows.Count > 0)
            {
                lock (_dbLock) _marginRepository.InsertOrIgnore(rows);
                progress?.Report($"融资余额 {d:yyyy-MM-dd}：{rows.Count} 条已写入");
            }
            else progress?.Report($"融资余额 {d:yyyy-MM-dd}：无数据（可能非交易日）");
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
        progress?.Report("依次执行：指数成分/权重 → 股东数据 → 财务报表 → 分红送配（较慢，可能数小时）");

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

        progress?.Report("指数成分/权重、股东数据、财务报表、分红送配全部处理完毕。");
        return new FetchResult { Errors = errors };
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
    public async Task<FetchResult> RunFetchFinancialsAsync(IProgress<string>? progress, CancellationToken ct = default)
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

            // 目标：在市个股 + 2016年后退市的（回测池同款；更早退市的没有K线、抓了也用不上）
            var codes = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Select(s => s.Code).ToList();
            var delisted = new SqliteDelistedRepository(_paths.CurrentDb).GetAll()
                .Where(r => r.DelistDate == null || r.DelistDate.Value.Year >= 2016)
                .Select(r => r.Code);
            codes = codes.Concat(delisted).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

            var latestByCode = repo.GetLatestReportDateByCode();
            var expected = LatestExpectedReportPeriod(DateTime.Today);
            var targets = codes
                .Where(c => !latestByCode.TryGetValue(c, out var have) || have.Date < expected)
                .ToList();
            progress?.Report($"财务报表：目标 {codes.Count} 只，其中 {codes.Count - targets.Count} 只本地已有最新报告期" +
                             $"（{expected:yyyy-MM-dd}）无需重抓，待抓 {targets.Count} 只（每只3个请求）...");
            if (targets.Count == 0) return new FetchResult();

            var errors = new ConcurrentBag<string>();
            var failedCodes = new ConcurrentBag<string>();
            int done = 0, wrote = 0;
            await Task.WhenAll(targets.Select(async code =>
            {
                try
                {
                    var rows = await _financialProvider.GetAllAsync(code, ct);
                    if (rows.Count > 0)
                    {
                        lock (_dbLock) { repo.ReplaceByCode(code, rows); }
                        Interlocked.Add(ref wrote, rows.Count);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"财报 {code}: {ex.Message}");
                    failedCodes.Add(code);
                }
                int n = Interlocked.Increment(ref done);
                if (n % 50 == 0 || n == targets.Count)
                    progress?.Report($"财务报表进度 ({n}/{targets.Count})，已写入 {Volatile.Read(ref wrote):N0} 条，已用时 {FormatElapsed(sw.Elapsed)}");
            }));

            progress?.Report($"财务报表完成：抓取 {targets.Count} 只、写入 {wrote:N0} 条、失败 {failedCodes.Count} 只" +
                             (failedCodes.Count > 0 ? "（失败的下次运行会自动重试）" : "") + $"，用时 {FormatElapsed(sw.Elapsed)}。");
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
            int completed = 0, withData = 0, wrote = 0;
            progress?.Report($"开始拉取分红送配，共 {stocks.Count} 只，逐只抓、较慢...");

            var tasks = stocks.Select(async stock =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var rows = await _dividendProvider.GetAllAsync(stock.Code, ct);
                    if (rows.Count > 0)
                    {
                        lock (_dbLock) _dividendRepository.ReplaceByCode(stock.Code, rows);
                        Interlocked.Increment(ref withData);
                        Interlocked.Add(ref wrote, rows.Count);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"{stock.Code}: {ex.Message}"); failed.Add(stock.Code); }

                int done = Interlocked.Increment(ref completed);
                if (done % 50 == 0 || done == stocks.Count)
                    progress?.Report($"分红送配 {done}/{stocks.Count}（有分红 {withData} 只、共写 {Volatile.Read(ref wrote):N0} 条、失败 {failed.Count}，已用时 {FormatElapsed(sw.Elapsed)}）");
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
