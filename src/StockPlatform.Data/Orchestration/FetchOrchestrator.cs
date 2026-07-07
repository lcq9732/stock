using System.Collections.Concurrent;
using System.Diagnostics;
using StockPlatform.Data.CloudStorage;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// Ties together fetching, local storage, week/month aggregation, master/daily file
/// production, and the "prompt to upload" hand-off — see doc/data-platform-design.md
/// sections 6.2 and 6.6. This is the Fetcher program's core, independent of its UI.
///
/// Every run uses exactly one data source, chosen by the caller (see doc/data-platform-design.md
/// section 3.4) — no automatic mixing or failover between vendors, since different vendors can
/// compute derived fields slightly differently and silently blending sources within one dataset
/// is worse than a clear, deliberate manual switch when a source stops working.
///
/// Two fetch modes, sharing the same per-stock fetch/write/aggregate logic
/// (<see cref="ProcessOneStockAsync"/>) and output-file production (<see cref="ProduceOutputFileAsync"/>):
/// - <see cref="RunFetchAsync"/> ("拉取全部"): refreshes the market-wide stock list, then for each
///   stock resumes from wherever it last left off (its own latest day-bar date in the local
///   database) up to today. This makes it safe to stop and re-run at any time — an interrupted
///   run or a handful of per-stock failures just get retried/caught up on the next run, since
///   nothing advances a stock's watermark unless that stock's fetch actually succeeded.
/// - <see cref="RunFetchDayAsync"/> ("拉取当天"): re-fetches exactly one caller-specified calendar
///   day for every stock already known locally, ignoring each stock's own watermark. Does NOT
///   refresh the stock list (run 拉取全部 at least once first). Useful for manually topping up a
///   specific day (e.g. today, after the market closed) without re-scanning the whole market's
///   history — day bars are INSERT OR IGNORE, so re-requesting an already-present day is harmless.
/// </summary>
public class FetchOrchestrator
{
    private readonly FetchPaths _paths;
    private readonly IManifestStore _manifestStore;
    private readonly ICloudStorageClient _cloudStorage;
    private readonly object _dbLock = new();

    public FetchOrchestrator(
        FetchPaths paths,
        IManifestStore manifestStore,
        ICloudStorageClient cloudStorage)
    {
        _paths = paths;
        _manifestStore = manifestStore;
        _cloudStorage = cloudStorage;
    }

    /// <summary>
    /// Fetches every A-share stock automatically — the user does not type in codes, they
    /// just pick a source and click "拉取全部"; the program looks up the full market list itself.
    /// </summary>
    public async Task<FetchResult> RunFetchAsync(NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        // Forward the rate limiter's out-of-band status (e.g. "intentionally pausing, not
        // hung" — see RateLimiter/IBarDataFetcher.OnStatus) into this run's progress log. The
        // fetcher/its RateLimiter live for the whole app session, so this must be unsubscribed
        // when the run ends — otherwise a later run would get duplicate deliveries.
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        try
        {
            return await RunFetchAllInternalAsync(source, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
        }
    }

    /// <summary>See the class remarks — "拉取当天", independent of each stock's watermark.</summary>
    public async Task<FetchResult> RunFetchDayAsync(NamedBarSource source, DateOnly date, IProgress<string>? progress, CancellationToken ct = default)
    {
        void ForwardStatus(string msg) => progress?.Report(msg);
        source.Fetcher.OnStatus += ForwardStatus;
        try
        {
            return await RunFetchDayInternalAsync(source, date, progress, ct);
        }
        finally
        {
            source.Fetcher.OnStatus -= ForwardStatus;
        }
    }

    private async Task<FetchResult> RunFetchAllInternalAsync(NamedBarSource source, IProgress<string>? progress, CancellationToken ct)
    {
        var isFirstRun = !File.Exists(_paths.CurrentDb);
        var today = DateTime.Today;
        var currentRepo = new SqliteBarRepository(_paths.CurrentDb);
        currentRepo.EnsureSchema();

        var sw = Stopwatch.StartNew();
        progress?.Report("正在获取全市场股票列表...");
        var stocks = await source.StockListProvider.GetAllStocksAsync(progress, ct);
        progress?.Report($"共 {stocks.Count} 只股票，数据源：{source.Name}，开始抓取（已用时 {sw.Elapsed:mm\\:ss}）");
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, stocks.Select(s => (s.Code, s.Name)));

        var errors = new ConcurrentBag<string>();
        var deltaBars = new ConcurrentBag<Bar>();
        var stats = new FetchStats();
        int completed = 0;
        var tasks = stocks.Select(stock =>
        {
            // Resume point is per-stock, not a single global watermark — an interrupted run or a
            // stock that failed last time just gets its gap re-requested next time, since nothing
            // advanced its latest-date unless the fetch actually succeeded (see class remarks).
            DateTime start;
            lock (_dbLock)
            {
                var lastDate = currentRepo.GetLatestPeriodStart(stock.Code, Granularity.Day);
                start = lastDate?.AddDays(1) ?? today.AddYears(-3);
            }
            return ProcessOneStockAsync(stock.Code, source, start, today, currentRepo, deltaBars, errors, stats, progress, stocks.Count, () => Interlocked.Increment(ref completed), sw, ct);
        });
        await Task.WhenAll(tasks);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        return await ProduceOutputFileAsync(isFirstRun, today, deltaBars, errors, progress, ct);
    }

    private async Task<FetchResult> RunFetchDayInternalAsync(NamedBarSource source, DateOnly date, IProgress<string>? progress, CancellationToken ct)
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

        var errors = new ConcurrentBag<string>();
        var deltaBars = new ConcurrentBag<Bar>();
        var stats = new FetchStats();
        int completed = 0;
        var tasks = stocks.Select(stock =>
            ProcessOneStockAsync(stock.Code, source, day, day, currentRepo, deltaBars, errors, stats, progress, stocks.Count, () => Interlocked.Increment(ref completed), sw, ct));
        await Task.WhenAll(tasks);

        progress?.Report($"本轮汇总：{stats.Summarize()}");
        return await ProduceOutputFileAsync(isFirstRun: false, day, deltaBars, errors, progress, ct);
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
        ConcurrentBag<Bar> deltaBars, ConcurrentBag<string> errors, FetchStats stats, IProgress<string>? progress,
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
            stats.Fail();
            reportCompleted();
            return;
        }

        if (newDayBars.Count > 0)
        {
            stats.FetchedWithNewData();
            lock (_dbLock)
            {
                currentRepo.InsertOrIgnore(newDayBars);
                foreach (var b in newDayBars) deltaBars.Add(b);

                // Week/month are derived, not raw facts — recompute over the code's FULL day
                // history (not just the increment) so the still-open current week/month stays
                // correct, then upsert (overwrite) rather than insert-or-ignore.
                var allDayBars = currentRepo.Query(code, Granularity.Day);
                var weekBars = BarAggregator.ToWeekly(allDayBars);
                var monthBars = BarAggregator.ToMonthly(allDayBars);
                SqliteBarUpsert.Upsert(_paths.CurrentDb, weekBars);
                SqliteBarUpsert.Upsert(_paths.CurrentDb, monthBars);
                foreach (var b in weekBars) deltaBars.Add(b);
                foreach (var b in monthBars) deltaBars.Add(b);
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
    /// Shared tail for both fetch modes: turns whatever landed in <paramref name="deltaBars"/>
    /// into the master (first run) or daily (every other run/按天抓取) output file and hands it
    /// to <see cref="_cloudStorage"/> — see doc/data-platform-design.md sections 6.2 and 6.6.
    /// <paramref name="asOfDate"/> is the date the produced file's name/manifest entry should
    /// carry — "today" for 拉取全部, the caller-specified day for 拉取当天.
    /// </summary>
    private async Task<FetchResult> ProduceOutputFileAsync(
        bool isFirstRun, DateTime asOfDate, ConcurrentBag<Bar> deltaBars, ConcurrentBag<string> errors,
        IProgress<string>? progress, CancellationToken ct)
    {
        var result = new FetchResult();
        result.Errors.AddRange(errors);

        if (deltaBars.IsEmpty)
        {
            progress?.Report("没有新数据需要抓取");
            return result;
        }

        var manifest = _manifestStore.Load();
        string producedFile;
        string remoteFolder;

        if (isFirstRun)
        {
            var asOf = DateOnly.FromDateTime(asOfDate);
            producedFile = FileNaming.MasterFile(asOf);
            var outPath = Path.Combine(_paths.OutputDir, producedFile);
            File.Copy(_paths.CurrentDb, outPath, overwrite: true);
            manifest.CurrentMaster = new ManifestFileRef { File = producedFile, AsOfDate = asOf };
            remoteFolder = "";
            await _cloudStorage.UploadAsync(outPath, remoteFolder, ct);
        }
        else
        {
            var date = DateOnly.FromDateTime(asOfDate);
            producedFile = FileNaming.DailyFile(date);
            var outPath = Path.Combine(_paths.OutputDir, producedFile);
            if (File.Exists(outPath)) File.Delete(outPath);
            var deltaRepo = new SqliteBarRepository(outPath);
            deltaRepo.EnsureSchema();
            deltaRepo.InsertOrIgnore(deltaBars);
            manifest.DailyFiles.Add(new ManifestDailyRef { File = producedFile, Date = date });
            remoteFolder = "daily";
            await _cloudStorage.UploadAsync(outPath, remoteFolder, ct);
        }

        _manifestStore.Save(manifest);
        await _cloudStorage.UploadAsync(_paths.ManifestPath, "", ct);

        result.ProducedFile = producedFile;
        result.OutboxPath = _cloudStorage is ManualUploadPrompter prompter
            ? Path.Combine(prompter.OutboxFolder, remoteFolder)
            : null;

        progress?.Report($"完成，已生成 {producedFile}（manifest.json 已一并放入暂存目录）");
        return result;
    }

    /// <summary>
    /// The daily increment files produced since the last merge, still waiting to be folded into
    /// a new master file. Lets the UI show the user "还有N个日增量文件待合并" as a nudge — see
    /// doc/data-platform-design.md section 6.2 (合并 is a purely manual, user-triggered action).
    /// </summary>
    public List<ManifestDailyRef> GetPendingDailyFiles() => _manifestStore.Load().DailyFiles;

    public async Task<MergeResult> RunMergeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法合并，请先执行抓取");

        var manifest = _manifestStore.Load();
        var oldMaster = manifest.CurrentMaster;

        var asOf = DateOnly.FromDateTime(DateTime.Today);
        var newMasterFile = FileNaming.MasterFile(asOf);
        var outPath = Path.Combine(_paths.OutputDir, newMasterFile);
        File.Copy(_paths.CurrentDb, outPath, overwrite: true);

        manifest.PreviousMaster = oldMaster;
        manifest.CurrentMaster = new ManifestFileRef { File = newMasterFile, AsOfDate = asOf };

        // best-effort cleanup of daily files that are now folded into the new master
        foreach (var daily in manifest.DailyFiles)
        {
            try { await _cloudStorage.DeleteAsync(daily.File, "daily", ct); }
            catch { /* cleanup is best-effort; a leftover file on the cloud drive is harmless */ }
        }
        manifest.DailyFiles.Clear();

        await _cloudStorage.UploadAsync(outPath, "", ct);
        _manifestStore.Save(manifest);
        await _cloudStorage.UploadAsync(_paths.ManifestPath, "", ct);

        return new MergeResult
        {
            NewMasterFile = newMasterFile,
            OutboxPath = _cloudStorage is ManualUploadPrompter prompter ? prompter.OutboxFolder : "",
        };
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
