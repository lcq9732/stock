using System.Collections.Concurrent;
using System.Diagnostics;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 【拉取全部】拆出来的**单项入口**（2026-09-02 新增，设计见 doc/fetch-plan-atomic-tasks-design.md）。
///
/// ════ 这一步做了什么、没做什么 ════
/// 这里的每个方法都只是把 <see cref="FetchOrchestrator"/> 主体里对应的那一段包了个壳：
/// 自己建 errors/failedCodes/stats/计时器、自己收尾写 manifest。**抓取逻辑一行都没改**，
/// 所以【拉取全部】和"只跑这一步"走的是同一段代码，不会出现两套行为。
///
/// ════ 为什么每项要自己建状态 ════
/// 原来 13 步共享一份 errors/stats，最后汇总一次、写一次 manifest。拆开之后每项是独立的一轮，
/// 各自汇总、各自记录——这正是拆分想要的（一项失败不再拖累其它 12 项）。
///
/// ════ 步骤之间怎么传数据 ════
/// 原来第 1 步取到的股票列表是**内存**传给后面几步的。拆开后统一走库：名册项写 StockMeta，
/// 逐只项开跑时用 <see cref="LocalStockCodes"/> 读回来（<see cref="SqliteStockMetaUpsert.GetAll"/>
/// 只返回 type='stock'，指数/ETF/板块/退市股不会混进来）。
/// 库里还没有名册时直接抛异常提示先跑名册项——跟【补指定历史日】现在的做法一致。
/// </summary>
public partial class FetchOrchestrator
{
    /// <summary>每个单项开跑前的准备：打开库、建好本轮要用的收集器。</summary>
    private (SqliteBarRepository Repo, ConcurrentBag<string> Errors, ConcurrentBag<string> Failed,
             FetchStats Stats, Stopwatch Sw) BeginStep()
    {
        var repo = new SqliteBarRepository(_paths.CurrentDb);
        repo.EnsureSchema();
        return (repo, new ConcurrentBag<string>(), new ConcurrentBag<string>(), new FetchStats(), Stopwatch.StartNew());
    }

    /// <summary>本地已知的**个股**代码（不含指数/ETF/板块/退市股）。空库时抛异常，提示先跑名册项。</summary>
    private List<string> LocalStockCodes()
    {
        var codes = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Select(s => s.Code).ToList();
        if (codes.Count == 0)
            throw new InvalidOperationException(
                "本地还没有股票名册，这一步没有可抓的标的——请先跑一次【股票名册与流通市值】");
        return codes;
    }

    // ───────────────────────────── 1. 股票名册与流通市值 ─────────────────────────────

    /// <summary>
    /// 刷新全市场名册（写 StockMeta）+ 当下的流通市值快照。
    ///
    /// **一次扫描办两件事**：新浪那个列表接口一次就同时给出名册和 <c>nmc</c>（流通市值），
    /// 所以这一项先跑市值扫描、直接拿它带回来的全市场名单刷新名册
    /// （见 <see cref="MarketCapFetchResult.AllStocks"/>），比【拉取全部】原来的做法省掉
    /// 约 55 个重复请求——那边是"先扫一遍取名册、市值实现里面再扫一遍"。
    /// 逐只查询的市值实现（东财/腾讯）给不出全市场名单，这时才回退到单独取一次名册。
    ///
    /// 市值是**当下快照**、接口没有历史，所以这一项只有"增量"一种模式。
    /// </summary>
    public async Task<FetchResult> RunStepRosterAndMarketCapAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        _marketCapFetcher.OnStatus += Forward;
        try
        {
            // 传本地已知的代码：市值扫描据此判断谁是"新发现的"。空库时传空表，
            // 扫描回来的全市场标的会被当成新股一并写进名册。
            var known = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Select(s => s.Code).ToList();
            var (_, rosterRefreshed) = await FetchMarketCapAsync(source, known, progress, ct);

            if (!rosterRefreshed)
            {
                // 这个市值实现看不到全市场名单（逐只查询式），只好单独再取一次名册
                progress?.Report("市值来源给不出全市场名单，单独获取一次股票列表...");
                var stocks = await source.StockListProvider.GetAllStocksAsync(progress, ct);
                SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, stocks.Select(s => (s.Code, s.Name)));
            }

            progress?.Report($"名册已刷新，本地个股 {SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).Count} 只。");
        }
        finally { _marketCapFetcher.OnStatus -= Forward; }

        // attempted 传空：K线失败名单跟这一步无关，别把它清了（市值有自己的失败名单，
        // 由 FetchMarketCapAsync 内部维护）。
        return FinishFetchRun(errors, "股票名册与流通市值", Array.Empty<string>(), failed, progress);
    }

    // ───────────────────────────── 2. 资金净流入 ─────────────────────────────

    /// <summary>
    /// 逐只抓主力资金净流入（新浪）。失败名单由 FetchNetInflowAsync 内部维护。
    /// <paramref name="specificDay"/> 有值＝"只抓那一天"模式（exactDayOnly，原【补指定历史日】的做法）。
    /// </summary>
    public async Task<FetchResult> RunStepNetInflowAsync(
        IProgress<string>? progress, CancellationToken ct = default, DateTime? specificDay = null)
    {
        var (_, errors, failed, _, _) = BeginStep();
        var codes = LocalStockCodes();
        void Forward(string m) => progress?.Report(m);
        _netInflowFetcher.OnStatus += Forward;
        try
        {
            await FetchNetInflowAsync(codes, specificDay ?? DateTime.Today,
                exactDayOnly: specificDay.HasValue, progress, ct);
        }
        finally { _netInflowFetcher.OnStatus -= Forward; }
        return FinishFetchRun(errors, "资金净流入", Array.Empty<string>(), failed, progress);
    }

    // ───────────────────────────── 3. 中标/订单公告 ─────────────────────────────

    /// <summary>
    /// 按关键词抓中标/订单公告（巨潮检索 → 正文）。关键词为空就是空跑（跟原来一致）。
    /// 回看窗口默认沿用【拉取全部】的 AnnouncementLookbackDaysForFetchAll 天。
    /// </summary>
    public async Task<FetchResult> RunStepAnnouncementsAsync(
        IReadOnlyList<string> keywords, IProgress<string>? progress, CancellationToken ct = default,
        int? lookbackDays = null, DateTime? specificDay = null)
    {
        var (_, errors, failed, _, _) = BeginStep();
        if (keywords.Count == 0)
        {
            progress?.Report("（公告关键词为空，这一项跳过）");
            return new FetchResult { NothingToDo = true };
        }
        // "只抓某一天"就把窗口收成那一天（原【补指定历史日】的做法）；否则按回看窗口。
        DateOnly start, end;
        if (specificDay is { } day)
        {
            start = end = DateOnly.FromDateTime(day);
        }
        else
        {
            var today = DateTime.Today;
            start = DateOnly.FromDateTime(today.AddDays(-(lookbackDays ?? AnnouncementLookbackDaysForFetchAll)));
            end = DateOnly.FromDateTime(today);
        }
        await FetchAnnouncementsAsync(keywords, start, end, progress, ct);
        return FinishFetchRun(errors, "中标/订单公告", Array.Empty<string>(), failed, progress);
    }

    // ───────────────────────────── 4. 指数日K ─────────────────────────────

    /// <summary>大盘指数日K（水位线增量）。它同时是"最近一个已收盘交易日"的锚，很多快照类判断靠它。</summary>
    public async Task<FetchResult> RunStepIndexBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchIndexBarsAsync(source, DateTime.Today, lookbackYears, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "指数日K",
            MarketIndexCatalog.All.Select(i => i.Symbol).ToList(), failed, progress);
    }

    // ──────────────────── 5~7. 个股日K（前复权 / 后复权 / 不复权） ────────────────────

    /// <summary>个股日K·前复权：按水位线续抓，顺带重算周/月线、记录复权基准漂移名单。</summary>
    public async Task<FetchResult> RunStepStockDayBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        var codes = LocalStockCodes();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchStockDayBarsAsync(source, codes, DateTime.Today, lookbackYears,
                repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "个股日K·前复权", codes, failed, progress);
    }

    /// <summary>
    /// 个股日K·前复权，**只抓指定那一天**（模式「只抓某一天」，原【补指定历史日】那一路）：
    /// 不看水位线、也不补断档，就是把那一天补上。日常请用增量模式——只有它会自动补断档。
    /// </summary>
    public async Task<FetchResult> RunStepStockDayBarsForDayAsync(
        NamedBarSource source, DateTime day, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        var codes = LocalStockCodes();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            progress?.Report($"按天抓取个股前复权日K：{day:yyyy-MM-dd}，共 {codes.Count} 只（用本地名册，不重新扫全市场）");
            await FetchStockDayBarsForDayAsync(source, codes, day, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "个股日K·前复权", codes, failed, progress);
    }

    /// <summary>个股日K·后复权（回测遗留口径，水位线独立于前复权）。</summary>
    public Task<FetchResult> RunStepStockHfqBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default) =>
        RunStepStockAdjustedBarsAsync(source, lookbackYears, Granularity.DayHfq, "个股日K·后复权", progress, ct);

    /// <summary>个股日K·不复权（原始成交价，day_adj 的输入）。日常增量走这里；首次整段回补用
    /// <see cref="RunFetchRawBarsAsync"/>。</summary>
    public Task<FetchResult> RunStepStockRawBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default) =>
        RunStepStockAdjustedBarsAsync(source, lookbackYears, Granularity.DayRaw, "个股日K·不复权", progress, ct);

    private async Task<FetchResult> RunStepStockAdjustedBarsAsync(
        NamedBarSource source, int lookbackYears, string gran, string kind,
        IProgress<string>? progress, CancellationToken ct)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        var codes = LocalStockCodes();
        var today = DateTime.Today;
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchHfqBarsAsync(source, codes,
                code => HfqWatermarkWindow(repo, code, today, lookbackYears, gran),
                repo, errors, failed, stats, progress, sw, ct, gran: gran);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, kind, codes, failed, progress);
    }

    // ───────────────────────────── 8. ETF 日K ─────────────────────────────

    /// <summary>全市场 ETF 日K（新浪名单 + 所选K线源，水位线增量）。</summary>
    public async Task<FetchResult> RunStepEtfBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        List<string> etfCodes;
        try
        {
            etfCodes = await FetchEtfBarsAsync(source, DateTime.Today, lookbackYears, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "ETF日K", etfCodes, failed, progress);
    }

    // ───────────────────────────── 9. 退市股收尾 ─────────────────────────────

    /// <summary>刷新退市名单，并给"本地跟踪过、但最后一根K线还早于终止日"的票补完最后那几天。</summary>
    public async Task<FetchResult> RunStepDelistedTailsAsync(
        NamedBarSource source, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        List<string> codes;
        try
        {
            codes = await CatchUpDelistedTailsAsync(source, repo, errors, failed, stats, progress, sw, ct);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "退市股收尾", codes, failed, progress);
    }

    // ─────────────────────── 10. 板块指数合成（本地计算） ───────────────────────

    /// <summary>
    /// 用本地成分股 + 个股日K 等权合成板块指数日K。**不联网、纯 CPU**，所以推到线程池上跑，
    /// 免得在 UI 线程上把界面冻住（同 RunFetchBankRegulatoryAsync 的理由）。
    /// </summary>
    public async Task<FetchResult> RunStepSynthesizeBoardIndexAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, _, _) = BeginStep();
        await Task.Run(() => SynthesizeBoardIndexCore(repo, errors, progress, ct), ct);
        return FinishFetchRun(errors, "板块指数合成", Array.Empty<string>(), failed, progress);
    }

    // ─────────────────────── 11~12. 融资余额 / 龙虎榜 ───────────────────────

    /// <summary>融资余额（交易所）——以指定日为终点回看最近几个交易日、跳过本地已有的。默认今天。</summary>
    public async Task<FetchResult> RunStepMarginRecentAsync(
        DateTime? day, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        _marginProvider.OnStatus += Forward;
        try { await FetchMarginRecentAsync(day ?? DateTime.Today, errors, progress, ct); }
        finally { _marginProvider.OnStatus -= Forward; }
        return FinishFetchRun(errors, "融资余额", Array.Empty<string>(), failed, progress);
    }

    /// <summary>
    /// 融资余额 / 龙虎榜的**整段回补**（原【一键补齐每日历史】的两半，2026-09-02 拆开）：
    /// 从本地K线最早那天补到今天，跳过已有的交易日，幂等、可反复跑、可随时停。
    ///
    /// 拆开的理由跟别处一样——两家源（交易所 / 新浪）、两张表、失败互不相干；
    /// 想只补龙虎榜历史时，不必连着把融资余额也跑一遍。
    /// </summary>
    public async Task<FetchResult> RunStepBackfillMarginAsync(
        IProgress<string>? progress, CancellationToken ct = default) =>
        await RunStepBackfillDailyOneAsync("融资余额", _marginProvider.EarliestAvailable,
            ct2 => _marginRepository.GetTradeDates(),
            async d =>
            {
                var rows = await _marginProvider.GetDetailAsync(d, ct);
                if (rows.Count > 0) { lock (_dbLock) { _marginRepository.InsertOrIgnore(rows); } }
                return rows.Count;
            },
            h => _marginProvider.OnStatus += h, h => _marginProvider.OnStatus -= h,
            () => _marginRepository.EnsureSchema(), progress, ct);

    /// <summary>见 <see cref="RunStepBackfillMarginAsync"/>——龙虎榜那一半。</summary>
    public async Task<FetchResult> RunStepBackfillLhbAsync(
        IProgress<string>? progress, CancellationToken ct = default) =>
        await RunStepBackfillDailyOneAsync("龙虎榜", _lhbProvider.EarliestAvailable,
            ct2 => _lhbRepository.GetTradeDates(),
            async d =>
            {
                var rows = await _lhbProvider.GetDailyAsync(d, ct);
                if (rows.Count > 0) { lock (_dbLock) { _lhbRepository.InsertOrIgnore(rows); } }
                return rows.Count;
            },
            h => _lhbProvider.OnStatus += h, h => _lhbProvider.OnStatus -= h,
            () => _lhbRepository.EnsureSchema(), progress, ct);

    private async Task<FetchResult> RunStepBackfillDailyOneAsync(
        string label,
        DateOnly earliestAvailable,
        Func<CancellationToken, HashSet<DateOnly>> haveDates,
        Func<DateOnly, Task<int>> fetchOne,
        Action<Action<string>> subscribe, Action<Action<string>> unsubscribe,
        Action ensureSchema,
        IProgress<string>? progress, CancellationToken ct)
    {
        var (repo, errors, failed, _, sw) = BeginStep();
        void Forward(string s) => progress?.Report(s);
        subscribe(Forward);
        try
        {
            ensureSchema();
            var earliest = repo.GetOverallEarliestPeriodStart(Granularity.Day)
                ?? throw new InvalidOperationException(
                    "本地还没有K线数据，无法确定补齐起点——请先跑一次【个股日K·前复权】");
            await BackfillDailyAsync(label, DateOnly.FromDateTime(earliest),
                DateOnly.FromDateTime(DateTime.Today), haveDates(ct), fetchOne, errors, progress, sw,
                earliestAvailable, ct);
        }
        finally { unsubscribe(Forward); }
        return FinishFetchRun(errors, $"{label}·整段回补", Array.Empty<string>(), failed, progress);
    }

    /// <summary>龙虎榜（新浪）——只抓指定那一天，默认今天。</summary>
    public async Task<FetchResult> RunStepLhbDayAsync(
        DateTime? day, IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        _lhbProvider.OnStatus += Forward;
        try { await FetchLhbOneDayAsync(day ?? DateTime.Today, errors, progress, ct); }
        finally { _lhbProvider.OnStatus -= Forward; }
        return FinishFetchRun(errors, "龙虎榜", Array.Empty<string>(), failed, progress);
    }

    // ─────────────────── 13. 当日覆盖率体检（本地查库） ───────────────────

    /// <summary>
    /// 以上证指数最新一根日线当交易日锚，查出"上一个交易日有、这一天没有"的个股，写进待重试名单。
    /// 纯查库，可能要扫几 GB，所以推到线程池上跑。
    /// </summary>
    public async Task<FetchResult> RunStepDayCoverageCheckAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        var (_, missing) = await Task.Run(() => CheckLatestDayCoverage(progress), ct);
        var result = FinishFetchRun(errors, "当日覆盖率体检", Array.Empty<string>(), failed, progress);
        result.NothingToDo = missing == 0;
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  另外三处复合动作拆出来的项（2026-09-02，设计文档 3.3 节）
    // ══════════════════════════════════════════════════════════════════════════

    // ─────────────── 指数成分 / 指数权重 / ETF↔指数映射 ───────────────
    // 【指数成分/权重】原来是一个动作：逐个指数先问新浪要成分、再问中证要权重。
    // 拆开的依据是"中间产物有没有独立价值"：成分名单和权重**各自入库、各自能单独用**
    // （成分名单本身就是一种选股全集），所以是两件事；而中证那一侧不稳、失败率高，
    // 合在一起时它会把整项拖成"失败"。ETF↔指数映射是纯本地匹配，按"本地计算单独成项"拆出来。

    /// <summary>指数成分名单（新浪）——逐个指数抓，失败进 FailedIndexConsCodes。</summary>
    public async Task<FetchResult> RunStepIndexConsOnlyAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, sw) = BeginStep();
        _indexRepository.EnsureSchema();
        var indexes = IndexCatalog.All;
        if (indexes.Count == 0)
            return new FetchResult { Errors = ["内置指数清单为空（IndexCatalog.csv 未打包？），无法拉取指数成分"] };

        var consFailed = new List<string>();
        var attempted = indexes.Select(i => i.Code).ToList();
        var now = DateTime.Now;
        int ok = 0, empty = 0, done = 0;

        void Forward(string s) => progress?.Report(s);
        _indexConsProvider.OnStatus += Forward;
        try
        {
            progress?.Report($"开始拉取指数成分名单（新浪），共 {indexes.Count} 个指数...");
            foreach (var (code, _) in indexes)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var members = await _indexConsProvider.GetConsAsync(code, ct);
                    // 新浪对某些老指数本来就没有成分，返回空不算失败（跟原来的判断一致）
                    if (members.Count > 0) { lock (_dbLock) _indexRepository.ReplaceCons(code, members, now); ok++; }
                    else empty++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"指数 {code} 成分抓取失败：{ex.Message}"); consFailed.Add(code); }

                if (++done % 20 == 0 || done == indexes.Count)
                    progress?.Report($"指数成分 {done}/{indexes.Count}（成功 {ok}，已用时 {FormatElapsed(sw.Elapsed)}）");
            }
        }
        finally { _indexConsProvider.OnStatus -= Forward; }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.FailedIndexConsCodes = ComputeUpdatedFailedCodes(manifest.FailedIndexConsCodes, attempted, consFailed);
            _manifestStore.Save(manifest);
        }
        progress?.Report($"指数成分完成：{ok} 个指数有数据、{empty} 个无成分、失败 {consFailed.Count} 个"
                       + (consFailed.Count > 0 ? "（可点【重新拉取失败】重试）" : ""));
        return FinishFetchRun(errors, "指数成分名单", Array.Empty<string>(), failed, progress);
    }

    /// <summary>本地这一期权重多新才算"不用再抓"。中证的 closeweight.xls 是月度更新（基准日=月末交易日），
    /// 25 天足够覆盖一个更新周期，又不会把月初的新一期漏掉。</summary>
    private const int IndexWeightFreshDays = 25;

    /// <summary>确认 404 之后隔多久再问一次。中证偶尔会给新指数补上文件，所以不能永久拉黑。</summary>
    private const int IndexWeightMissingRetryDays = 30;

    /// <summary>
    /// 指数权重（中证 OSS）。非中证系没有权重文件（404），不算失败。
    ///
    /// ════ 为什么要先筛一遍再抓（2026-09-02 改）════
    /// 内置指数全集 732 个，而 closeweight.xls **只有中证系才有**，其余一律 404；而权重本身是
    /// **月度**更新的。原来每次跑都把 732 个硬敲一遍，等于每次拿四五百个注定 404 的请求去撞中证的
    /// 反爬——数据一条也拿不到。现在两道筛子：
    ///   ① 本地这一期还新鲜（<see cref="IndexWeightFreshDays"/> 天内）→ 跳过，月中跑基本全跳过；
    ///   ② 上次已经确认没有文件、且没过 <see cref="IndexWeightMissingRetryDays"/> 天 → 跳过。
    /// 稳态下每次真正发出的请求从 732 降到接近 0，只有月初那一轮才会实抓中证系那两三百个。
    /// </summary>
    public async Task<FetchResult> RunStepIndexWeightOnlyAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, sw) = BeginStep();
        _indexRepository.EnsureSchema();
        var indexes = IndexCatalog.All;
        if (indexes.Count == 0)
            return new FetchResult { Errors = ["内置指数清单为空（IndexCatalog.csv 未打包？），无法拉取指数权重"] };

        // ── 先筛：本地已是最新一期的、以及确认没有文件的，都不用再问 ──
        var latestByIndex = _indexRepository.GetLatestWeightDateByIndex();
        Dictionary<string, DateTime> missing;
        lock (_dbLock) missing = new(_manifestStore.Load().IndexWeightMissing, StringComparer.Ordinal);

        var today = DateTime.Today;
        var targets = new List<string>();
        int freshSkip = 0, missingSkip = 0;
        foreach (var (code, _) in indexes)
        {
            if (latestByIndex.TryGetValue(code, out var asOf)
                && (today - asOf).TotalDays < IndexWeightFreshDays) { freshSkip++; continue; }
            if (missing.TryGetValue(code, out var confirmedAt)
                && (today - confirmedAt.Date).TotalDays < IndexWeightMissingRetryDays) { missingSkip++; continue; }
            targets.Add(code);
        }

        progress?.Report($"指数权重：全集 {indexes.Count} 个，本轮要问 {targets.Count} 个"
            + $"（{freshSkip} 个本地已是最新一期、{missingSkip} 个确认没有权重文件——"
            + "中证是月度更新，这两道筛子是为了少撞它的反爬）。");
        if (targets.Count == 0)
        {
            progress?.Report("　都不用抓，这一轮无事可做。");
            var idleResult = FinishFetchRun(errors, "指数权重", Array.Empty<string>(), failed, progress);
            idleResult.NothingToDo = true;
            return idleResult;
        }

        var weightFailed = new List<string>();
        var newlyMissing = new List<string>();
        int ok = 0, none = 0, done = 0;

        void Forward(string s) => progress?.Report(s);
        _indexWeightProvider.OnStatus += Forward;
        try
        {
            foreach (var code in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var weights = await _indexWeightProvider.GetWeightsAsync(code, ct);
                    if (weights.Count > 0) { lock (_dbLock) _indexRepository.ReplaceWeights(code, weights); ok++; }
                    else { none++; newlyMissing.Add(code); }   // 404＝这个指数没有权重文件，记下来别再问
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"指数 {code} 权重抓取失败：{ex.Message}"); weightFailed.Add(code); }

                if (++done % 20 == 0 || done == targets.Count)
                    progress?.Report($"指数权重 {done}/{targets.Count}（成功 {ok}，已用时 {FormatElapsed(sw.Elapsed)}）");
            }
        }
        finally { _indexWeightProvider.OnStatus -= Forward; }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            // 失败名单只针对**本轮问过的**那些（跳过的不该被清出名单，也不该被记进去）
            manifest.FailedIndexWeightCodes =
                ComputeUpdatedFailedCodes(manifest.FailedIndexWeightCodes, targets, weightFailed);
            foreach (var code in newlyMissing) manifest.IndexWeightMissing[code] = today;
            // 这次抓到权重的，把"没有文件"的记录撤掉（中证补上了文件的情况）
            foreach (var code in targets.Except(newlyMissing, StringComparer.Ordinal))
                manifest.IndexWeightMissing.Remove(code);
            _manifestStore.Save(manifest);
        }

        progress?.Report($"指数权重完成：{ok} 个有权重、{none} 个没有权重文件（已记下、{IndexWeightMissingRetryDays} 天内不再问）、"
                       + $"失败 {weightFailed.Count} 个"
                       + (weightFailed.Count > 0 ? "（中证这侧偏不稳，可点【重新拉取失败】重试）" : ""));
        return FinishFetchRun(errors, "指数权重", Array.Empty<string>(), failed, progress);
    }

    /// <summary>ETF↔指数 名称匹配（本地、不联网）——供"股票→指数→ETF"反查。</summary>
    public async Task<FetchResult> RunStepEtfIndexMapAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        _indexRepository.EnsureSchema();
        await Task.Run(() =>
        {
            var map = BuildEtfIndexMap(progress);
            lock (_dbLock) _indexRepository.ReplaceEtfIndexMap(map);
        }, ct);
        return FinishFetchRun(errors, "ETF指数映射", Array.Empty<string>(), failed, progress);
    }

    // ─────────────── 板块行情与成分（不含合成） ───────────────

    /// <summary>
    /// 只抓板块行情与成分股，**不合成板块指数**——合成是独立的一项
    /// （<see cref="RunStepSynthesizeBoardIndexAsync"/>），排在它后面即可。
    /// 老按钮【拉取板块】仍旧是"抓完顺带合成"，行为不变。
    /// </summary>
    public async Task<FetchResult> RunStepBoardsOnlyAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        var skipped = await FetchBoardsCoreAsync(errors, progress, ct);
        if (skipped != null)
            return new FetchResult { Errors = errors.ToList(), SkippedReason = skipped };
        return FinishFetchRun(errors, "板块行情与成分", Array.Empty<string>(), failed, progress);
    }

    /// <summary>
    /// 只抓板块**名单和行情快照**（2026-09-04 拆分）。约 10 个请求，日更。
    /// 拆分理由见 <see cref="FetchBoardListCoreAsync"/>：这 10 个请求原来跟 2500 个成分股
    /// 请求抢同一批配额，而限流器每 15 个就要主动歇一次。
    /// </summary>
    public async Task<FetchResult> RunStepBoardListAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        var skipped = await FetchBoardListCoreAsync(errors, progress, ct);
        if (skipped != null)
            return new FetchResult { Errors = errors.ToList(), SkippedReason = skipped };
        return FinishFetchRun(errors, "概念和行业板块", Array.Empty<string>(), failed, progress);
    }

    /// <summary>
    /// 只抓板块**成分股**（2026-09-04 拆分）。约 2500 个请求，空闲时补、跑不完下轮接着来。
    /// 名单从库里读，所以【板块列表】没跑也能干活（软依赖）。
    /// </summary>
    public async Task<FetchResult> RunStepBoardMembersAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        _memberProgressText = null;
        var skipped = await FetchBoardMembersCoreAsync(errors, progress, ct);
        if (skipped != null)
            return new FetchResult { Errors = errors.ToList(), SkippedReason = skipped };
        var r = FinishFetchRun(errors, "板块成分股", Array.Empty<string>(), failed, progress);
        r.Progress = _memberProgressText;   // 让界面显示"已抓 144/1000，还剩 856"
        return r;
    }


    // ─────────────── 全库数据体检（本地查库） ───────────────

    /// <summary>一次查多少只票的空洞。太大一次 join 上千万行、内存和时间都难看；太小则来回开连接。</summary>
    private const int AuditBatchSize = 500;

    /// <summary>
    /// 一个交易日过去多久，才算"数据源确实该有了"。T+1 发布 + 盘后逐步更新，留 2 天很宽松。
    /// 比这更近的缺口不算数——那多半只是数据源还没更新完。
    /// </summary>
    private const int AuditSettleDays = 2;

    /// <summary>
    /// 体检要扫的"标的类型 × 口径"矩阵里**能联网补**的那几个面（2026-09-04 扩，原来只有个股×前复权）。
    /// 这些面查出来的空洞进 <see cref="Manifest.MissingBars"/>，由【重新拉取失败】按口径逐段补回来。
    ///
    /// ════ 为什么必须扫这么多面 ════
    /// 2026-09-02 把【拉取全部】拆成 13 个独立任务之后，后复权、不复权、ETF、指数各自成了一项——
    /// 独立就意味着**可以被漏排、可以单独失败**，而只体检前复权的话，这些面缺了没有任何人会发现。
    /// 回测吃的是 day_adj，它由 day_raw 推出来：不复权缺一天，回测序列就跟着错一天。
    ///
    /// ════ 哪些面不在这里 ════
    /// · 板块指数：本地合成的，缺了要重新合成、不是去抓（见 <see cref="AuditLocalOnlyScopes"/>）；
    /// · day_adj：本地重算的，同上；
    /// · 退市股：数据源不再更新它们，报出来也补不到，只会补满两轮之后堆进白名单变成噪声——
    ///   它们缺的最后那几天由【退市股收尾】负责。
    /// </summary>
    private static readonly (string Type, string Gran, string Label)[] AuditFetchableScopes =
    [
        (SqliteStockMetaUpsert.TypeStock, Granularity.Day,    "个股·前复权"),
        (SqliteStockMetaUpsert.TypeStock, Granularity.DayHfq, "个股·后复权"),
        (SqliteStockMetaUpsert.TypeStock, Granularity.DayRaw, "个股·不复权"),
        (SqliteStockMetaUpsert.TypeEtf,   Granularity.Day,    "ETF"),
        (SqliteStockMetaUpsert.TypeIndex, Granularity.Day,    "指数"),
    ];

    /// <summary>
    /// **全库数据体检**（2026-09-02 新增，2026-09-04 从"只查个股前复权"扩成全口径、全标的）：
    /// 逐只对照交易日历找日线空洞，能联网补的写进 <see cref="Manifest.MissingBars"/> 交给
    /// 【重新拉取失败】去补；补不靠网络的（板块指数、回测序列）只报数、并说清楚该跑哪一项。
    ///
    /// ════ 为什么要有它 ════
    /// 日更末尾的【当日覆盖率体检】只查**最新一个交易日的个股前复权**，挡的是"跑早了、数据源还没
    /// 更新完"那个坑。可要是程序停了几天、某天那轮跑挂了、或者某个口径的任务压根没排进计划，
    /// 中间那些天的缺口就没人发现——而"哪天缺了"恰恰是最不该让用户自己去判断的事。
    ///
    /// ════ 为什么是手动触发、不是每天跑 ════
    /// 全库扫描是重活（千万行级 join × 好几个面）。日更本身已经有检查，正常不会有缺口；真出问题多半
    /// 是别的原因（停机、断电、库损坏、某一项被漏排），那种情况隔一阵子手动体检一次就够。
    ///
    /// ════ 停牌怎么办 ════
    /// 停牌那几天在数据上跟漏抓一模一样——交易日历里有、这只票没有，查是分不开的。所以：
    /// 体检只负责**报**，补不到的由【重新拉取失败】在补过两轮之后写进 MissingBarConfirmed 白名单，
    /// 往后体检跳过（白名单按口径分开存，互不影响）。想推翻这些结论就用「彻底体检」
    /// （<paramref name="thorough"/>），它会先清空白名单。
    /// </summary>
    /// <param name="thorough">true＝忽略并清空"确认没有"白名单，全部重查一遍。</param>
    public async Task<FetchResult> RunStepFullAuditAsync(
        IProgress<string>? progress, CancellationToken ct = default, bool thorough = false)
    {
        var (repo, errors, failed, _, sw) = BeginStep();
        var result = await Task.Run(() =>
        {
            var audit = new SqliteMissingBarRepository(_paths.CurrentDb);
            if (thorough)
            {
                int had = audit.ConfirmedCount();
                audit.ClearConfirmed();
                progress?.Report($"彻底体检：已清空「确认没有」白名单（原有 {had} 条），全部重查。");
            }

            var instruments = SqliteStockMetaUpsert.GetAllInstruments(_paths.CurrentDb);
            if (instruments.Count == 0)
            {
                progress?.Report("本地还没有标的名册，没什么可体检的。");
                return new FetchResult { NothingToDo = true };
            }

            var byType = instruments
                .GroupBy(i => i.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(i => i.Code).ToList(), StringComparer.Ordinal);
            List<string> CodesOf(string type) => byType.TryGetValue(type, out var l) ? l : [];

            // 太新的日子不算缺（数据源可能还没更新完），这条线由 AuditSettleDays 定
            var cutoff = DateTime.Today.AddDays(-AuditSettleDays);
            var ranges = new List<MissingBarRange>();
            var summary = new List<string>();

            progress?.Report($"开始全库体检：{AuditFetchableScopes.Length} 个可联网补的面（"
                           + string.Join("、", AuditFetchableScopes.Select(x => x.Label))
                           + $"），逐只对照交易日历找日线空洞（{cutoff:yyyy-MM-dd} 之后的日子不算，"
                           + "数据源可能还没更新完）…");

            foreach (var (type, gran, label) in AuditFetchableScopes)
            {
                ct.ThrowIfCancellationRequested();
                var codes = CodesOf(type);
                if (codes.Count == 0)
                {
                    summary.Add($"　{label}：本地一只都没有，跳过");
                    continue;
                }

                // 「整只票一根都没有这个口径」FindGaps 是查不出来的——它只看每只票自己
                // [最早, 最晚] 区间内的洞，一根都没有的票根本进不了那张区间表。这类问题跟"缺几天"
                // 完全是两回事（多半是那一项从来没排进计划、或者一直在失败），所以单独数出来提醒，
                // **不进待补名单**：整段回补该走【拉取区间数据】，几千只×十年塞进逐段重试里跑不完。
                var have = repo.GetLatestPeriodStartByCode(gran);
                int none = codes.Count(c => !have.ContainsKey(c));

                var (withGaps, days) = AuditScanScope(audit, codes, gran, cutoff, thorough, label,
                                                      ranges, progress, sw, ct);
                summary.Add($"　{label}：{codes.Count} 只，{withGaps} 只有空洞、共 {days} 个交易日"
                          + (none > 0 ? $"；另有 {none} 只**一根都没有**（该口径从没抓过，要整段回补）" : ""));
            }

            // 已经在名单里的保留原有的 Tries（别把补过两轮的计数清零，否则永远确认不了）。
            // key 是"代码+口径"：同一只票的三个口径各自计数，前复权补上了不该把不复权的进度抹掉。
            lock (_dbLock)
            {
                var manifest = _manifestStore.Load();
                var triesByKey = new Dictionary<(string Code, string Gran), int>();
                foreach (var m in manifest.MissingBars)
                    triesByKey[(m.Code, NormalizeGran(m.Granularity))] = m.Tries;
                foreach (var r in ranges)
                    if (triesByKey.TryGetValue((r.Code, r.Granularity), out var t)) r.Tries = t;
                manifest.MissingBars = ranges
                    .OrderBy(r => r.Code, StringComparer.Ordinal)
                    .ThenBy(r => r.Granularity, StringComparer.Ordinal)
                    .ToList();
                _manifestStore.Save(manifest);
            }

            // ── 补不靠网络的那两个面：只报数，说清楚该跑哪一项 ──
            var localHints = AuditLocalOnlyScopes(audit, CodesOf, cutoff, thorough, progress, ct);

            // ── 覆盖形状（起点晚了 / 尾巴停了）：FindGaps 天生看不见的两种形状 ──
            localHints.AddRange(AuditCoverageShape(repo, CodesOf, cutoff, progress, ct));

            // ── K线之外的日频表（资金流/融资余额/龙虎榜/东财三张）──
            localHints.AddRange(AuditDailyTables(cutoff, thorough, progress, ct));

            int delisted = CodesOf(SqliteStockMetaUpsert.TypeDelisted).Count;
            if (delisted > 0)
                summary.Add($"　退市股 {delisted} 只：不体检（数据源不再更新，报了也补不到；"
                          + "缺的最后几天由【退市股收尾】负责）");
            summary.AddRange(localHints);

            int totalDays = ranges.Sum(r => r.Days);
            progress?.Report($"全库体检完成（用时 {FormatElapsed(sw.Elapsed)}）：\n"
                + string.Join("\n", summary) + "\n"
                + (ranges.Count == 0
                    ? "　可联网补的面没有发现空洞。"
                    : $"　合计 {ranges.Count} 段、{totalDays} 个交易日的日线缺失，已记入待补名单。\n"
                      + "　下一步：跑一次【重新拉取失败】去补。补得到的自动划掉；"
                      + "连补两轮拿不到的会被判定为\"数据源确实没有\"（多半是停牌），写进白名单、以后体检不再报。\n"
                      + "　⚠ 第一次体检查出的量通常很大（十年下来的停牌天数都在里面），补一轮可能要几小时。"));

            return new FetchResult { NothingToDo = ranges.Count == 0 };
        }, ct);

        result.Errors.AddRange(errors);
        FinishFetchRun(errors, "全库数据体检", Array.Empty<string>(), failed, progress);
        return result;
    }

    /// <summary>老 manifest 里的记录没有口径字段（2026-09-04 之前只体检前复权），一律按前复权算。</summary>
    private static string NormalizeGran(string? gran) =>
        string.IsNullOrEmpty(gran) ? Granularity.Day : gran;

    /// <summary>
    /// 扫一个面（一批标的 × 一个口径），把空洞按**包络区间**追加进 <paramref name="ranges"/>。
    /// 返回 (有空洞的标的数, 缺失交易日总数)，只用来写汇总行。
    /// </summary>
    private (int WithGaps, int Days) AuditScanScope(
        SqliteMissingBarRepository audit, List<string> codes, string gran, DateTime cutoff,
        bool thorough, string label, List<MissingBarRange> ranges,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        int scanned = 0, withGaps = 0, days = 0;
        foreach (var batch in codes.Chunk(AuditBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var gaps = audit.FindGaps(batch, gran,
                MarketIndexCatalog.ShanghaiCompositeSymbol, ignoreConfirmed: thorough);

            foreach (var (code, gapDays) in gaps)
            {
                var settled = gapDays.Where(d => d.Date <= cutoff).ToList();
                if (settled.Count == 0) continue;
                // 空洞不连续时取包络：一次请求覆盖整段，比逐日请求划算得多
                ranges.Add(new MissingBarRange
                {
                    Code = code, Granularity = gran,
                    From = settled[0], To = settled[^1], Days = settled.Count, Tries = 0,
                });
                withGaps++;
                days += settled.Count;
            }

            scanned += batch.Length;
            progress?.Report($"体检 {label}：{scanned}/{codes.Count}，已发现 {withGaps} 只有空洞"
                           + $"（已用时 {FormatElapsed(sw.Elapsed)}）");
        }
        return (withGaps, days);
    }

    /// <summary>
    /// 体检那两个**补不靠网络**的面（2026-09-04）：板块指数是本地合成的、day_adj 是本地重算的。
    /// 它们缺了不该去发请求——把这种空洞塞进待补名单，只会让【重新拉取失败】对着本地合成出来的
    /// 代码空抓两轮，然后错误地判定"数据源确实没有"、写进白名单。所以这里只查、只报，
    /// 并直接告诉用户该跑哪一项。
    /// </summary>
    private List<string> AuditLocalOnlyScopes(
        SqliteMissingBarRepository audit, Func<string, List<string>> codesOf,
        DateTime cutoff, bool thorough, IProgress<string>? progress, CancellationToken ct)
    {
        var lines = new List<string>();

        // ① 板块指数（本地等权合成，code 是 gn_xxx/new_xxx）
        var boards = codesOf(SqliteStockMetaUpsert.TypeBoard);
        if (boards.Count > 0)
        {
            progress?.Report($"体检 板块指数：{boards.Count} 个（本地合成，只报不补）…");
            int withGaps = 0, days = 0;
            foreach (var batch in boards.Chunk(AuditBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (_, gapDays) in audit.FindGaps(batch, Granularity.Day,
                             MarketIndexCatalog.ShanghaiCompositeSymbol, ignoreConfirmed: thorough))
                {
                    int n = gapDays.Count(d => d.Date <= cutoff);
                    if (n == 0) continue;
                    withGaps++; days += n;
                }
            }
            lines.Add(withGaps == 0
                ? $"　板块指数：{boards.Count} 个，没有空洞"
                : $"　板块指数：{boards.Count} 个里 {withGaps} 个有空洞、共 {days} 个交易日"
                  + "——**不进待补名单**（本地合成的，抓不来）：先把个股日K补齐，再跑一次【板块指数合成】");
        }

        // ② 回测序列 day_adj（＝不复权 × 本地算的复权因子）
        ct.ThrowIfCancellationRequested();
        progress?.Report("体检 回测序列(day_adj)：对比不复权的进度…");
        int pending = GetPendingAdjRebuildCount();
        var stocks = codesOf(SqliteStockMetaUpsert.TypeStock);
        int adjWithGaps = 0, adjDays = 0;
        foreach (var batch in stocks.Chunk(AuditBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (_, gapDays) in audit.FindGaps(batch, Granularity.DayAdj,
                         MarketIndexCatalog.ShanghaiCompositeSymbol, ignoreConfirmed: thorough))
            {
                int n = gapDays.Count(d => d.Date <= cutoff);
                if (n == 0) continue;
                adjWithGaps++; adjDays += n;
            }
        }
        lines.Add(pending == 0 && adjWithGaps == 0
            ? "　回测序列(day_adj)：跟不复权一样新，没有空洞"
            : $"　回测序列(day_adj)：{pending} 只落后于不复权、{adjWithGaps} 只区间内有空洞（{adjDays} 个交易日）"
              + "——**不进待补名单**（本地算的，抓不来）：先把个股·不复权补齐，再跑一次【重算回测序列】");

        return lines;
    }

    /// <summary>尾巴落后超过这么多个交易日的标的不算"漏抓"：长期停牌的在市股票（*ST 那些）
    /// 一停就是几个月甚至几年，全报出来只会把真正的"最近几天没跑成"淹掉。</summary>
    private const int AuditTailSuspectLimit = 10;

    /// <summary>
    /// **覆盖形状体检**（2026-09-06 新增）——查 <see cref="SqliteMissingBarRepository.FindGaps"/>
    /// 天生看不见的两种形状：起点比该有的晚一大截、尾巴停在几天前。判据是纯函数，
    /// 放在 <see cref="CoverageShapeAuditor"/> 里单独测。
    ///
    /// ════ 为什么尾巴要分"全局"和"个别票"两档 ════
    /// **全局**：某个口径所有票里最新的那一根都落后了 → 这一项最近根本没跑成（漏排、连续失败），
    /// 这是几乎零误报的信号，也是最该立刻处理的。
    /// **个别票**：只有几只落后 → 多半是那几只当天没抓到；但长期停牌的在市股票也长这样，
    /// 所以只数落后在 <see cref="AuditTailSuspectLimit"/> 个交易日以内的，再久的当停牌处理。
    /// </summary>
    private List<string> AuditCoverageShape(
        SqliteBarRepository repo, Func<string, List<string>> codesOf, DateTime cutoff,
        IProgress<string>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        progress?.Report("体检 覆盖形状：起点/尾巴跟交易日历对照…");

        var calendar = repo.Query(MarketIndexCatalog.ShanghaiCompositeSymbol, Granularity.Day)
            .Select(b => b.PeriodStart.Date)
            .Where(d => d <= cutoff.Date)
            .OrderBy(d => d)
            .ToList();
        if (calendar.Count == 0)
        {
            lines.Add("　覆盖形状：本地上证指数日线为空，没有交易日历可比，跳过");
            return lines;
        }

        var stocks = codesOf(SqliteStockMetaUpsert.TypeStock).ToHashSet(StringComparer.Ordinal);

        // 每个口径的最早/最晚日各查一次就够——一次 GROUP BY 要扫一千多万行，
        // 下面 day 这一套会被个股/ETF/指数三个面用到，不缓存就是白扫三遍。
        var earliestCache = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        var latestCache = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        Dictionary<string, DateTime> Earliest(string gran) =>
            earliestCache.TryGetValue(gran, out var v) ? v
                : earliestCache[gran] = repo.GetEarliestPeriodStartByCode(gran);
        Dictionary<string, DateTime> Latest(string gran) =>
            latestCache.TryGetValue(gran, out var v) ? v
                : latestCache[gran] = repo.GetLatestPeriodStartByCode(gran);

        // ① 起点：以前复权为基准，后复权/不复权比它晚太多就是"整段没补上"
        var baseEarliest = Only(Earliest(Granularity.Day), stocks);
        foreach (var (gran, label) in
                 new[] { (Granularity.DayHfq, "个股·后复权"), (Granularity.DayRaw, "个股·不复权") })
        {
            ct.ThrowIfCancellationRequested();
            var late = CoverageShapeAuditor.FindLateStarts(
                calendar, baseEarliest, Only(Earliest(gran), stocks));
            if (late.Count == 0) continue;

            var worst = late.OrderByDescending(g => g.TradingDays).Take(3)
                .Select(g => $"{g.Code} 晚 {g.TradingDays} 天");
            lines.Add($"　{label}：{late.Count} 只的历史起点比前复权晚 "
                    + $"{CoverageShapeAuditor.DefaultLateStartThreshold} 个交易日以上（{string.Join("、", worst)}…）"
                    + "——**不进待补名单**（整段回补该走【拉取区间数据】，逐段重试跑不完）");
        }

        // ② 尾巴：先看全局（这一项是不是最近没跑成），再看个别票
        foreach (var (type, gran, label) in AuditFetchableScopes)
        {
            ct.ThrowIfCancellationRequested();
            var codes = codesOf(type).ToHashSet(StringComparer.Ordinal);
            if (codes.Count == 0) continue;

            var latest = Only(Latest(gran), codes);
            if (latest.Count == 0) continue;

            var globalLatest = latest.Values.Max().Date;
            int behind = calendar.Count - 1 - calendar.FindLastIndex(d => d <= globalLatest);
            if (behind > 0)
            {
                lines.Add($"　{label}：**整个口径最新只到 {globalLatest:yyyy-MM-dd}、落后 {behind} 个交易日**"
                        + "——这一项最近没跑成（漏排或连续失败），先去看它的运行记录");
                continue;   // 全局都落后时，逐票再数一遍没有意义
            }

            var tails = CoverageShapeAuditor.FindLateTails(
                calendar, latest, Only(Earliest(gran), codes))
                .Where(g => g.TradingDays <= AuditTailSuspectLimit)
                .ToList();
            if (tails.Count == 0) continue;

            var worst = tails.OrderByDescending(g => g.TradingDays).Take(3)
                .Select(g => $"{g.Code} 停在 {g.Actual:MM-dd}");
            lines.Add($"　{label}：{tails.Count} 只的最新一根停在 {AuditTailSuspectLimit} 个交易日以内的过去"
                    + $"（{string.Join("、", worst)}…）——多半是临时停牌，长于这个的不计（那是长期停牌）");
        }

        return lines;
    }

    /// <summary>只留这一批代码的那些项——GetEarliestPeriodStartByCode 是按口径查全库的，
    /// 里面混着 ETF、指数和板块指数的代码。</summary>
    private static Dictionary<string, DateTime> Only(
        Dictionary<string, DateTime> byCode, HashSet<string> codes) =>
        byCode.Where(kv => codes.Contains(kv.Key))
              .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>补两轮还拿不到，就判定"数据源确实没有"（多半是停牌），写进白名单、以后体检跳过。</summary>
    private const int AuditMaxTries = 2;

    /// <summary>
    /// 把【全库数据体检】查出来的历史空洞补上（2026-09-02，2026-09-04 改成按口径分组）——
    /// 【重新拉取失败】的一部分。
    ///
    /// 每只标的按**区间**抓一次（数据源一页固定返回 640 根K线，抓一段跟抓一天成本几乎一样），
    /// 抓完立刻复查这一段还缺不缺：
    ///   · 补上了 → 从待补名单划掉；
    ///   · 还缺、但没到 <see cref="AuditMaxTries"/> 轮 → 留着，下次再来；
    ///   · 还缺、且已经补满两轮 → 写进 MissingBarConfirmed 白名单，判定"数据源确实没有"，
    ///     以后体检不再报它——不这么收敛的话，停牌的票会年复一年地每次都被报出来、每次都白抓一遍。
    ///
    /// ════ 为什么要按口径分组 ════
    /// 名单里现在混着前复权/后复权/不复权三套（还有 ETF 和指数的前复权）。抓的时候要把口径
    /// 传给数据源——拿前复权的请求去补不复权的洞，补完复查还是缺，两轮之后就被错判成"数据源
    /// 确实没有"。更要紧的是**后复权和不复权只有腾讯给**：数据源切到新浪时这两组必须原样留着、
    /// 连 Tries 都不能加，否则跑两轮就把一大片正常数据永久打进白名单。
    /// </summary>
    private async Task FillAuditedGapsAsync(
        NamedBarSource source, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, List<string> done,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        List<MissingBarRange> pending;
        lock (_dbLock) pending = _manifestStore.Load().MissingBars.ToList();
        if (pending.Count == 0) return;

        // 老 manifest 的记录没有口径字段，一律按前复权（2026-09-04 之前只体检前复权）
        foreach (var r in pending) r.Granularity = NormalizeGran(r.Granularity);

        var audit = new SqliteMissingBarRepository(_paths.CurrentDb);
        var stillMissing = new List<MissingBarRange>();
        int filledTotal = 0, confirmedTotal = 0;
        var parts = new List<string>();

        progress?.Report($"补全库体检查出的历史空洞：{pending.Count} 段、"
                       + $"共 {pending.Sum(r => r.Days)} 个交易日（每段按区间抓一次）…");

        // 前复权先补：它是界面和大多数分析用的口径，也是另外两套的参照
        foreach (var group in pending.GroupBy(r => r.Granularity, StringComparer.Ordinal)
                                     .OrderBy(g => g.Key == Granularity.Day ? 0 : 1)
                                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var gran = group.Key;
            var list = group.ToList();
            string label = GranLabel(gran);

            // 后复权/不复权只有腾讯给。数据源不支持时整组原样留着、**Tries 一动不动**——
            // 让它们空跑两轮的后果是几千只票被永久打进"数据源确实没有"白名单。
            if (gran != Granularity.Day && !source.Fetcher.SupportsHfq)
            {
                stillMissing.AddRange(list);
                progress?.Report($"（{label} {list.Count} 段先留着：数据源 {source.Name} 不提供这个口径，"
                               + "要补请把数据源切到 Tencent 再跑一次【重新拉取失败】）");
                parts.Add($"{label} {list.Count} 段跳过（数据源不支持）");
                continue;
            }

            progress?.Report($"补 {label} 空洞：{list.Count} 段、共 {list.Sum(r => r.Days)} 个交易日…");
            var stats = new FetchStats();
            int done2 = 0;
            foreach (var range in list)
            {
                ct.ThrowIfCancellationRequested();
                // 顺序抓、不并发：这批可能上千只，并发只会更快撞数据源配额（见 PlanRunner 的类注释）
                await ProcessOneStockAsync(range.Code, source, range.From, range.To, currentRepo,
                    errors, failedCodes, stats, progress, list.Count,
                    () => Interlocked.Increment(ref done2), sw, ct, granularity: gran);
            }

            // 抓完复查这一段还缺不缺——判据仍是"交易日历里有、这只票没有"
            var toConfirm = new List<(string Code, DateTime Day)>();
            int stillCount = 0;
            foreach (var chunk in list.Chunk(AuditBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var gaps = audit.FindGaps(chunk.Select(r => r.Code).ToList(), gran,
                    MarketIndexCatalog.ShanghaiCompositeSymbol);
                foreach (var r in chunk)
                {
                    if (!gaps.TryGetValue(r.Code, out var days)) continue;      // 补齐了
                    var left = days.Where(d => d >= r.From && d <= r.To).ToList();
                    if (left.Count == 0) continue;

                    int tries = r.Tries + 1;
                    if (tries >= AuditMaxTries)
                        toConfirm.AddRange(left.Select(d => (r.Code, d)));      // 认了：数据源就是没有
                    else
                    {
                        stillMissing.Add(new MissingBarRange
                        {
                            Code = r.Code, Granularity = gran,
                            From = left[0], To = left[^1], Days = left.Count, Tries = tries,
                        });
                        stillCount++;
                    }
                }
            }

            if (toConfirm.Count > 0) audit.Confirm(toConfirm, gran, AuditMaxTries);
            int confirmed = toConfirm.Select(x => x.Code).Distinct().Count();
            int filled = Math.Max(0, list.Count - stillCount - confirmed);
            filledTotal += filled;
            confirmedTotal += confirmed;

            progress?.Report($"　{label}：{stats.Summarize()}；补上 {filled} 段、"
                           + $"还缺 {stillCount} 段（下轮再试）、{confirmed} 段判定数据源确实没有");
            parts.Add($"{label} 补上 {filled}/{list.Count} 段");
        }

        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            manifest.MissingBars = stillMissing;
            _manifestStore.Save(manifest);
        }

        progress?.Report($"历史空洞补齐汇总：{string.Join("；", parts)}。\n"
            + $"　合计补上 {filledTotal} 段；还缺 {stillMissing.Count} 段（下轮再试）；"
            + $"{confirmedTotal} 段补满 {AuditMaxTries} 轮仍拿不到，"
            + "已判定为数据源确实没有（多半是停牌），以后体检不再报。");
        done.Add($"历史空洞 {pending.Count} 段");
    }

    /// <summary>日期列举最多列这么多个，再多就只报个数——日志是给人看的，糊满 200 个日期没人读。</summary>
    private const int AuditMaxListedDays = 8;

    /// <summary>
    /// 体检 K线之外的**日频表**（2026-09-04）：资金净流入、融资余额、龙虎榜，以及 2026-09-03
    /// 接进来的东财三张（资金流明细、龙虎榜席位、大宗交易）。
    ///
    /// 这些表拆成原子项之后同样是"可以被漏排、可以单独失败"，而且失败得比K线更静默——K线至少
    /// 还有当日覆盖率体检兜着，这几张一天都没抓到的话，本地是一点动静都没有的。
    /// 判据和补法见 <see cref="SqliteDailyTableAuditor"/>：只认"某个交易日一行都没有"和
    /// "行数不到中位数两成"，**只报不补**（各表补法不同，塞进统一重试里既补不对也说不清）。
    /// </summary>
    private List<string> AuditDailyTables(
        DateTime cutoff, bool thorough, IProgress<string>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        var auditor = new SqliteDailyTableAuditor(_paths.CurrentDb);

        foreach (var spec in SqliteDailyTableAuditor.DailyTables)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"体检 {spec.Label}：按交易日核对覆盖…");

            SqliteDailyTableAuditor.Result? r;
            try { r = auditor.Check(spec, MarketIndexCatalog.ShanghaiCompositeSymbol, cutoff); }
            catch (Exception ex)
            {
                lines.Add($"　{spec.Label}：体检没跑成（{ex.Message}）");
                continue;
            }

            if (r == null)
            {
                lines.Add($"　{spec.Label}：本地还没有这类数据，跳过");
                continue;
            }

            // 资金净流入的空日进待补名单，交给【重新拉取失败】一轮补掉（见 Manifest.MissingNetInflowDays）
            var queued = spec.Table == "NetInflow"
                ? QueueMissingNetInflowDays(r.EmptyDays, thorough)
                : 0;

            string span = $"{r.From:yyyy-MM-dd}~{r.To:yyyy-MM-dd} 共 {r.TradingDays} 个交易日"
                        + $"（每日约 {r.MedianRows} 行）";
            if (r.EmptyDays.Count == 0 && r.ThinDays.Count == 0 && r.TailMissingDays.Count == 0)
            {
                lines.Add($"　{spec.Label}：{span}，齐");
                continue;
            }

            var parts = new List<string>();
            // 尾部滞后放最前面：它是"这一项最近根本没跑成"，比十年前少几行紧急得多
            if (r.TailMissingDays.Count > 0)
                parts.Add($"**最新只到 {r.To:yyyy-MM-dd}、落后 {r.TailMissingDays.Count} 个交易日**"
                        + $"（{FormatDays(r.TailMissingDays)}）");
            if (r.EmptyDays.Count > 0)
                parts.Add($"{r.EmptyDays.Count} 天一行都没有（{FormatDays(r.EmptyDays)}）"
                        + (queued > 0 ? $"，其中 {queued} 天已记入待补名单" : ""));
            if (r.ThinDays.Count > 0)
                parts.Add($"{r.ThinDays.Count} 天行数明显偏少、疑似只抓了一半"
                        + $"（{FormatDays(r.ThinDays.Select(t => t.Day).ToList())}）");
            lines.Add($"　{spec.Label}：{span}——{string.Join("；", parts)}。补法：{spec.HowToFill}");
        }

        return lines;
    }

    /// <summary>
    /// 把资金净流入的空日写进待补名单（2026-09-06）。已经在名单里的保留原有 Tries——
    /// 跟 K 线空洞一个道理，别把补过一轮的计数清零，否则永远收敛不到"数据源确实没有"。
    /// </summary>
    /// <param name="thorough">「彻底体检」：连之前判定"数据源确实没有"的那些天也一起重查。</param>
    /// <returns>这一轮实际记进名单的天数。</returns>
    private int QueueMissingNetInflowDays(List<DateTime> emptyDays, bool thorough)
    {
        lock (_dbLock)
        {
            var manifest = _manifestStore.Load();
            if (thorough) manifest.ConfirmedNetInflowDays = [];

            var confirmed = manifest.ConfirmedNetInflowDays.Select(d => d.Date).ToHashSet();
            var triesByDay = manifest.MissingNetInflowDays
                .GroupBy(m => m.Day.Date)
                .ToDictionary(g => g.Key, g => g.Max(m => m.Tries));

            manifest.MissingNetInflowDays = emptyDays
                .Select(d => d.Date)
                .Where(d => !confirmed.Contains(d))
                .Distinct()
                .OrderBy(d => d)
                .Select(d => new MissingDayRetry { Day = d, Tries = triesByDay.GetValueOrDefault(d) })
                .ToList();
            _manifestStore.Save(manifest);
            return manifest.MissingNetInflowDays.Count;
        }
    }

    /// <summary>日期列表转成人读的一行，超过 <see cref="AuditMaxListedDays"/> 个就截断。</summary>
    private static string FormatDays(List<DateTime> days) =>
        days.Count <= AuditMaxListedDays
            ? string.Join("、", days.Select(d => d.ToString("MM-dd")))
            : string.Join("、", days.Take(AuditMaxListedDays).Select(d => d.ToString("MM-dd")))
              + $"… 等 {days.Count} 天";

    /// <summary>口径的中文名，只用在日志里。</summary>
    private static string GranLabel(string gran) => gran switch
    {
        Granularity.DayHfq => "后复权",
        Granularity.DayRaw => "不复权",
        Granularity.DayAdj => "回测序列",
        _ => "前复权",
    };

    // ─────────────── 已下载 PDF 的重解析（本地） ───────────────

    /// <summary>
    /// 用**当前**解析规则把本地已经下载的银行/券商/保险年报中报重跑一遍，**一个网络请求都不发**。
    ///
    /// 为什么值得单独成项：解析规则一直在改（各家版式差异会不断暴露新坑——注释角标「（注3）」
    /// 没清干净让平安银行的拨备覆盖率变成 3.0、目录页的页码被当成资本充足率），改完想全库重跑时，
    /// 原来只能连带把联网下载那一大段也跑一遍。现在纯本地这一步可以随时单独跑，几分钟就完。
    /// </summary>
    public async Task<FetchResult> RunStepReparseBankReportsAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (_, errors, failed, _, _) = BeginStep();
        await Task.Run(() =>
        {
            var repo = new SqliteBankRegulatoryRepository(_paths.CurrentDb);
            repo.EnsureSchema();
            // 机构类型是靠财务特征科目认出来的，这里只**读**本地已有的快照，不联网补抓——
            // 补抓是【金融监管指标】那一项的事。认不出类型的按银行的标签集解析（同原逻辑）。
            var latest = new SqliteFinancialRepository(_paths.CurrentDb).GetLatestSnapshotByCode();
            ReparseCachedBankReports(repo, latest, progress, ct);
        }, ct);
        return FinishFetchRun(errors, "重解析已有PDF", Array.Empty<string>(), failed, progress);
    }
}
