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
/// - <see cref="RunFetchDayAsync"/> ("拉取当天"): re-fetches exactly one caller-specified calendar
///   day for every stock already known locally, ignoring each stock's own watermark, for K线 —
///   does NOT re-scan the market-wide stock LIST for that purpose (run 拉取全部 at least once
///   first). Useful for manually topping up a specific day (e.g. today, after the market closed)
///   without re-scanning the whole market's K线 history — day bars are INSERT OR IGNORE, so
///   re-requesting an already-present day is harmless. **Market cap IS a full market-wide scan
///   even here** (see <see cref="FetchMarketCapAsync"/>/<see cref="SinaListMarketCapFetcher"/>) —
///   a deliberate exception, confirmed acceptable by the user (2026-07-08) even though it makes
///   拉取当天 slower than just "K线 for known stocks" would otherwise be.
/// - <see cref="RunRetryFailedAsync"/> ("重新拉取失败股票"): retries whatever's recorded in the
///   three failed-code lists on <see cref="Manifest"/>, without re-scanning the market list or
///   re-running announcements.
/// </summary>
public class FetchOrchestrator
{
    private readonly FetchPaths _paths;
    private readonly IManifestStore _manifestStore;
    private readonly IFundamentalMetricRepository _fundamentalRepository;
    private readonly IMarketCapFetcher _marketCapFetcher;
    private readonly INetInflowFetcher _netInflowFetcher;
    private readonly AnnouncementFetchOrchestrator _announcementOrchestrator;
    private readonly object _dbLock = new();

    // 拉取全部对同一批关键词、同一天窗口重复扫描是安全的（OrderWinAnnouncement 主键去重），所以
    // 不需要像K线那样维护"上次抓到哪"的水位线，固定回看这么多天足够覆盖两次拉取全部之间的间隔，
    // 代价很小（cninfo全文检索本来就比逐只股票查K线快得多）。
    private const int AnnouncementLookbackDaysForFetchAll = 14;

    // 主力净流入表里还没有记录的股票（新股票/第一次跑），从截止日往前回溯这么多天开始补，
    // 覆盖"耀哥法"新规则要看的最近3天再留足缓冲，不需要跟K线的lookbackYears一样长。
    private const int NetInflowInitialLookbackDays = 60;

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

    public FetchOrchestrator(
        FetchPaths paths,
        IManifestStore manifestStore,
        IFundamentalMetricRepository fundamentalRepository,
        IMarketCapFetcher marketCapFetcher,
        INetInflowFetcher netInflowFetcher,
        AnnouncementFetchOrchestrator announcementOrchestrator)
    {
        _paths = paths;
        _manifestStore = manifestStore;
        _fundamentalRepository = fundamentalRepository;
        _marketCapFetcher = marketCapFetcher;
        _netInflowFetcher = netInflowFetcher;
        _announcementOrchestrator = announcementOrchestrator;
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
        try
        {
            return await RunFetchAllInternalAsync(source, lookbackYears, announcementKeywords, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
        }
    }

    /// <summary>See the class remarks — "拉取当天", independent of each stock's watermark.</summary>
    public async Task<FetchResult> RunFetchDayAsync(
        NamedBarSource source, DateOnly date, IReadOnlyList<string> announcementKeywords,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        try
        {
            return await RunFetchDayInternalAsync(source, date, announcementKeywords, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
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
        progress?.Report($"共 {stocks.Count} 只股票，数据源：{source.Name}，开始抓取（已用时 {sw.Elapsed:mm\\:ss}）");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, stocks.Select(s => (s.Code, s.Name)));

        await FetchMarketCapAsync(stocks.Select(s => s.Code).ToList(), progress, ct);
        await FetchNetInflowAsync(stocks.Select(s => s.Code).ToList(), today, exactDayOnly: false, progress, ct);
        await FetchAnnouncementsAsync(
            announcementKeywords, DateOnly.FromDateTime(today.AddDays(-AnnouncementLookbackDaysForFetchAll)),
            DateOnly.FromDateTime(today), progress, ct);

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();
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
            return ProcessOneStockAsync(stock.Code, source, start, today, currentRepo, errors, failedCodes, stats, progress, stocks.Count, () => Interlocked.Increment(ref completed), sw, ct);
        });
        await Task.WhenAll(tasks);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "拉取全部", stocks.Select(s => s.Code).ToList(), failedCodes);
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
        progress?.Report($"按天抓取 {date:yyyy-MM-dd}，共 {stocks.Count} 只股票（使用本地已有列表，不重新扫描全市场），数据源：{source.Name}");

        await FetchMarketCapAsync(stocks.Select(s => s.Code).ToList(), progress, ct);
        await FetchNetInflowAsync(stocks.Select(s => s.Code).ToList(), day, exactDayOnly: true, progress, ct);
        await FetchAnnouncementsAsync(announcementKeywords, date, date, progress, ct);

        var errors = new ConcurrentBag<string>();
        var failedCodes = new ConcurrentBag<string>();
        var stats = new FetchStats();
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
                var existing = currentRepo.Query(stock.Code, Granularity.Day, day, day).FirstOrDefault();
                start = (existing != null && IsConfirmedFinal(existing.FetchedAt, day)) ? day.AddDays(1) : day;
            }
            return ProcessOneStockAsync(stock.Code, source, start, day, currentRepo, errors, failedCodes, stats, progress, stocks.Count, () => Interlocked.Increment(ref completed), sw, ct);
        });
        await Task.WhenAll(tasks);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "拉取当天", stocks.Select(s => s.Code).ToList(), failedCodes);
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
        try
        {
            return await RunRetryFailedInternalAsync(source, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
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

        if (failedCodesList.Count == 0 && failedMarketCapCodes.Count == 0 && failedNetInflowCodes.Count == 0)
        {
            progress?.Report("目前没有记录到抓取失败的股票，不需要重试");
            return new FetchResult();
        }

        if (failedMarketCapCodes.Count > 0)
            await FetchMarketCapAsync(failedMarketCapCodes, progress, ct);

        if (failedNetInflowCodes.Count > 0)
            await FetchNetInflowAsync(failedNetInflowCodes, DateTime.Today, exactDayOnly: false, progress, ct);

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
    /// Fetches one stock's bars for the given [start, end] range from the single given source (no
    /// failover — see the class remarks), then aggregates/writes to the local database (writes are
    /// serialized via <see cref="_dbLock"/> — SQLite only allows one writer at a time; network
    /// fetches still run concurrently across stocks, throttled by the source's own rate limiter).
    /// The caller decides what start/end means (per-stock watermark for 拉取全部, a fixed single
    /// day for 拉取当天) — this method doesn't care which.
    /// </summary>
    private async Task ProcessOneStockAsync(
        string code, NamedBarSource source, DateTime start, DateTime end, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, FetchStats stats, IProgress<string>? progress,
        int totalCount, Func<int> reportCompleted, Stopwatch sw, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // This is the "is it already up to date locally" check the caller computed start/end
        // from — a stock whose watermark is already >= end never even reaches the network call,
        // which is what makes 拉取全部/拉取当天 safe to re-run without re-downloading everything.
        // See FetchStats.Summarize(), reported once at the end of the run, for visible proof of
        // how many stocks this run actually skipped vs fetched vs failed.
        if (start.Date > end.Date) { stats.Skip(); reportCompleted(); return; }

        List<Bar>? newDayBars = null;
        try
        {
            var (_, bars) = await source.Fetcher.FetchAsync(code, Granularity.Day, start, end, ct);
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
            reportCompleted();
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
                if (olderBars.Count > 0) currentRepo.InsertOrIgnore(olderBars);
                if (todaysBars.Count > 0) SqliteBarUpsert.Upsert(_paths.CurrentDb, todaysBars);

                // Week/month are derived, not raw facts — recompute over the code's FULL day
                // history (not just the increment) so the still-open current week/month stays
                // correct, then upsert (overwrite) rather than insert-or-ignore.
                var allDayBars = currentRepo.Query(code, Granularity.Day);
                var weekBars = BarAggregator.ToWeekly(allDayBars);
                var monthBars = BarAggregator.ToMonthly(allDayBars);
                SqliteBarUpsert.Upsert(_paths.CurrentDb, weekBars);
                SqliteBarUpsert.Upsert(_paths.CurrentDb, monthBars);
            }
        }
        else
        {
            // Fetch succeeded but returned nothing — e.g. the requested range is entirely a
            // weekend/holiday with no trading. Not an error, not a local-DB skip either.
            stats.FetchedButEmpty();
        }

        var done = reportCompleted();
        // Reported often (every 5, not 50) so the log keeps moving and it's clear the run is
        // still alive rather than stuck — a single stock can legitimately take up to ~48s under
        // the rate limiter's retry/circuit-breaker (see RateLimiter), so long gaps are expected,
        // not a hang.
        if (done % 5 == 0 || done == totalCount)
            progress?.Report($"正在抓取 ({done}/{totalCount})，已用时 {sw.Elapsed:mm\\:ss}");
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
    /// </summary>
    private async Task FetchMarketCapAsync(IReadOnlyList<string> codes, IProgress<string>? progress, CancellationToken ct)
    {
        List<string> failedThisRun;
        try
        {
            progress?.Report($"正在获取流通市值（共 {codes.Count} 只股票，逐只查询，会比较慢）...");
            var entries = await _marketCapFetcher.GetMarketCapsAsync(codes, progress, ct);
            var fetchedAt = DateTime.Now;
            var metrics = entries.Select(e => new FundamentalMetric
            {
                Code = e.Code,
                MetricKey = MetricKeys.CirculatingMarketCap,
                AsOfDate = DateTime.Today,
                Value = e.CirculatingMarketCap,
                Source = "EastMoney",
                FetchedAt = fetchedAt,
            });
            _fundamentalRepository.Upsert(metrics);
            progress?.Report($"流通市值写入完成，共 {entries.Count} 条");
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
        return manifest.FailedCodes.Count + manifest.FailedMarketCapCodes.Count + manifest.FailedNetInflowCodes.Count;
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
