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

    /// <summary>
    /// 大盘指数日K。它同时是"最近一个已收盘交易日"的锚，很多快照类判断靠它。
    ///
    /// 两个模式：
    ///   · <b>增量</b>：每条指数从自己的水位线续到今天，日常就用它。
    ///   · <b>首次整段回补</b>（<paramref name="fullBackfill"/>，2026-09-10 加）：不看水位线，
    ///     从 A股开市首日抓起。**往指数清单里加了新指数之后必须跑一次**——水位线只往后走，
    ///     新指数第一次被增量抓到的只有回看窗口那几年，之后水位线就钉在最新一根上，
    ///     再也不会回头补前面的历史（2026-09-09 加深证综指等三条时踩到：只抓到 3 年，
    ///     而龙虎榜偏离值要拿它当基准回溯到 2004）。
    /// </summary>
    public async Task<FetchResult> RunStepIndexBarsAsync(
        NamedBarSource source, int lookbackYears, IProgress<string>? progress,
        CancellationToken ct = default, bool fullBackfill = false)
    {
        var (repo, errors, failed, stats, sw) = BeginStep();
        void Forward(string m) => progress?.Report(m);
        source.Fetcher.OnStatus += Forward;
        try
        {
            await FetchIndexBarsAsync(source, DateTime.Today, lookbackYears, repo, errors, failed, stats, progress, sw, ct, fullBackfill);
        }
        finally { source.Fetcher.OnStatus -= Forward; }
        progress?.Report($"本项汇总：{stats.Summarize()}");
        return FinishFetchRun(errors, "指数日K",
            MarketIndexCatalog.All.Select(i => i.Symbol).ToList(), failed, progress,
            taskId: RetryTaskIds.IndexBars);
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
        return FinishFetchRun(errors, kind, codes, failed, progress,
            taskId: RetryTaskIds.ForGranularity(gran));
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
        return FinishFetchRun(errors, "ETF日K", etfCodes, failed, progress, taskId: RetryTaskIds.EtfBars);
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
        return FinishFetchRun(errors, "退市股收尾", codes, failed, progress, taskId: RetryTaskIds.DelistedTails);
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
    /// 【回填"无更早数据"水位】（2026-09-07）——不联网，把本地已有历史里能推出的水位一次性
    /// 写进 <c>BarProbeFloor</c>，让往后的【拉取区间数据】不再对着"那些年还没上市"的票空跑。
    ///
    /// 判据与实测数据见 <see cref="ProbeFloorPlanner.PlanFromLocalHistory"/>（纯计算、可单测）。
    /// 这里只负责查三路水位线、落库、把结果说清楚——尤其要说清**哪些没填、为什么**，
    /// 否则人会以为跑完就万事大吉，而 ETF 那 1655 只其实还留给真探测。
    ///
    /// 幂等：水位表只抬不降（见 <see cref="SqliteBarProbeFloorRepository.Record"/>），反复跑无害。
    /// </summary>
    public Task<FetchResult> RunStepFillProbeFloorAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var (repo, errors, failed, _, sw) = BeginStep();
        var floors = new SqliteBarProbeFloorRepository(_paths.CurrentDb);
        floors.EnsureSchema();

        progress?.Report("正在查本地三路（前复权/后复权/不复权）日K的最早一根……23GB 库上约需半分钟，不联网。");
        var eDay = repo.GetEarliestPeriodStartByCode(Granularity.Day);
        ct.ThrowIfCancellationRequested();
        var eHfq = repo.GetEarliestPeriodStartByCode(Granularity.DayHfq);
        ct.ThrowIfCancellationRequested();
        var eRaw = repo.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        ct.ThrowIfCancellationRequested();

        var plan = ProbeFloorPlanner.PlanFromLocalHistory(eDay, eHfq, eRaw,
            Granularity.Day, Granularity.DayHfq, Granularity.DayRaw,
            out int agreed, out int disagreed, out int dayOnly);

        int before = floors.Count();
        foreach (var (gran, rows) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count > 0) floors.Record(rows, gran);
        }
        int after = floors.Count();

        progress?.Report($"三路最早一根一致的标的 {agreed} 只 → 已按它写入水位（三个粒度各 {agreed} 条，"
                       + $"表里从 {before} 条变成 {after} 条）。这些票往后的区间回补连请求都不会发。");
        if (disagreed > 0)
            progress?.Report($"三路最早一根不一致的 {disagreed} 只**没有填**——那说明其中某一路确实还缺前段，"
                           + "该抓。下一次【拉取区间数据】会照旧请求它们。");
        if (dayOnly > 0)
            progress?.Report($"只有前复权一路的 {dayOnly} 个标的（ETF / 大盘指数 / 板块指数）**没有填**："
                           + "它们没有另外两路可以交叉印证，不敢凭一路下结论。板块指数是本地合成的、区间回补本来就不抓；"
                           + "ETF 留给真探测（约 20 分钟一轮，探完水位会自动记下来）。");
        progress?.Report($"回填完毕，用时 {FormatElapsed(sw.Elapsed)}。要作废这些结论，"
                       + "跑【全库数据体检】并勾上「彻底体检」。");

        return Task.FromResult(FinishFetchRun(errors, "回填\"无更早数据\"水位", Array.Empty<string>(), failed, progress));
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
        await RunStepBackfillDailyOneAsync("融资余额", IDailyFetchNoDataRepository.MarginDataset, _marginProvider.EarliestAvailable,
            // 扣掉已知残缺日（2026-09-16）：GetTradeDates 只看"这天有没有行"，
            // 2026-08-21 有 1,998 行沪市就被算作"已有"，深市那一半永远补不回来。
            ct2 => _marginRepository.GetTradeDates().Except(PartialDaysOf(RetryTaskIds.Margin)).ToHashSet(),
            async d =>
            {
                var rows = await _marginProvider.GetDetailAsync(d, ct);
                if (rows.Count > 0) { lock (_dbLock) { _marginRepository.InsertOrIgnore(rows); } }
                return rows.Count;
            },
            h => _marginProvider.OnStatus += h, h => _marginProvider.OnStatus -= h,
            () => _marginRepository.EnsureSchema(), progress, ct);

    private async Task<FetchResult> RunStepBackfillDailyOneAsync(
        string label,
        string dataset,
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
            await BackfillDailyAsync(label, dataset, DateOnly.FromDateTime(earliest),
                DateOnly.FromDateTime(DateTime.Today), haveDates(ct), fetchOne, errors, progress, sw,
                earliestAvailable, ct);
        }
        finally { unsubscribe(Forward); }
        return FinishFetchRun(errors, $"{label}·整段回补", Array.Empty<string>(), failed, progress);
    }

    // 【龙虎榜】的 RunStepLhbDayAsync / RunStepBackfillLhbAsync 删于 2026-09-17：
    // 整项迁去了 StockPlatform.Tasks/LhbTask（增量、只抓某一天、整段回补三条路都在那儿，
    // 落库统一走 LhbDayWriter）。补残缺日仍在这边走 PartialDayRepair，按天重抓的动作
    // 跟任务侧共用同一个 LhbDayWriter——见 doc/lhb-seat-task-design.md §8。

    // ─────────────────── 13. 当日完整性体检（本地查库） ───────────────────
    //
    // 这一项的入口 2026-09-17 迁去了 StockPlatform.Tasks/DayCompletenessTask（新任务框架），
    // 原来的 RunStepDayCoverageCheckAsync 一并删掉。判据和编排在
    // SqliteDayCompletenessAuditor，orchestrator 这边只剩 CheckLatestDayCoverage 那个薄封装
    // ——【重新拉取失败】收尾时还要用它重建名单，跟新任务共用同一个 auditor。

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
            SetFailedTodo(manifest, RetryTaskIds.IndexCons, attempted, consFailed);
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
            SetFailedTodo(manifest, RetryTaskIds.IndexWeight, targets, weightFailed);
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


    // ─────────────── 体检查出的空洞：补回来（【重新拉取失败】的一部分）───────────────
    //
    // 体检本身 2026-09-09 迁到了 StockPlatform.Tasks/FullAuditTask（判据见
    // doc/full-audit-task-migration-design.md），这里只剩"拿着名单去补"这一半，
    // 以及它跟体检共用的几个常量/小工具。

    /// <summary>一次补多少段。太大一次 join 上千万行、内存和时间都难看；太小则来回开连接。</summary>
    private const int AuditBatchSize = 500;

    /// <summary>老 manifest 里的记录没有口径字段（2026-09-04 之前只体检前复权），一律按前复权算。</summary>
    private static string NormalizeGran(string? gran) =>
        string.IsNullOrEmpty(gran) ? Granularity.Day : gran;

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
    private async Task FillGapTodoAsync(
        string taskId, NamedBarSource source, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, List<string> done,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        // 只认领**自己这一条**待办（2026-09-13 二期）。以前这里是一次读走整份 MissingBars、
        // 自己按口径分组——那是因为待办没有归属、只能按口径归堆。现在归属在存储里，
        // 分组由分派完成（谁调这个方法就补谁的），这里少了一层循环。
        List<RetryTarget> pending;
        lock (_dbLock)
            pending = _manifestStore.Load().Todo(taskId, RetryTodoKind.Gap)?.Targets.ToList()
                      ?? new List<RetryTarget>();
        if (pending.Count == 0) return;

        var gran = NormalizeGran(pending[0].Gran);
        string label = TaskLabel(taskId);
        var audit = new SqliteMissingBarRepository(_paths.CurrentDb);

        // 后复权/不复权只有腾讯给。数据源不支持时整条原样留着、**Tries 一动不动**——
        // 让它们空跑两轮的后果是几千只票被永久打进"数据源确实没有"白名单。
        if (gran != Granularity.Day && !source.Fetcher.SupportsHfq)
        {
            progress?.Report($"（{label} {pending.Count} 段先留着：数据源 {source.Name} 不提供这个口径，"
                           + "要补请把数据源切到 Tencent 再跑一次【重新拉取失败】）");
            done.Add($"{label}空洞 {pending.Count} 段跳过（数据源不支持）");
            return;
        }

        // **每跑完一批就落账**（2026-09-07 改）。原来是全部跑完才写一次，实测后果：
        // 前复权那 2852 段跑了 1 小时 52 分、Tries 从 0 加到 1，可这个记账只在内存里——
        // 中途停掉（或程序崩了、断电了）全部白费，下一轮又从 Tries=0 开始，那 8103 段
        // 永远收敛不进"数据源确实没有"白名单。用户的原话：完成多少就记录多少，应该落库。
        //
        // 粒度是"一批 500 段"（约 17 分钟），中断最多损失这一批。做得到是因为这里的抓取
        // 本来就是**顺序**的（见下面那句注释：并发只会更快撞配额），抓完一批立刻能复查。
        var still = new List<RetryTarget>();
        int batchNo = 0, filled = 0, confirmed = 0;
        int batchTotal = (pending.Count + AuditBatchSize - 1) / AuditBatchSize;

        // 已处理的批换成复查结果，没轮到的批原样留着——少了后半句就会把还没跑的那部分
        // 整个清掉，比不落库更糟（安静的错）。
        void SaveProgress()
        {
            lock (_dbLock)
            {
                var m = _manifestStore.Load();
                m.SetTodo(taskId, RetryTodoKind.Gap,
                    still.Concat(pending.Skip(batchNo * AuditBatchSize)).ToList());
                _manifestStore.Save(m);
            }
        }

        progress?.Report($"补 {label} 空洞：{pending.Count} 段、共 {pending.Sum(r => r.Days)} 个交易日"
                       + $"（每段按区间抓一次，分 {batchTotal} 批，每批跑完就落账）…");
        var stats = new FetchStats();
        int done2 = 0;

        foreach (var batch in pending.Chunk(AuditBatchSize))
        {
            batchNo++;
            foreach (var range in batch)
            {
                ct.ThrowIfCancellationRequested();
                // 顺序抓、不并发：这批可能上千只，并发只会更快撞数据源配额（见 PlanRunner 的类注释）
                await ProcessOneStockAsync(range.Code, source, range.From ?? AShareMarketOpen,
                    range.To ?? DateTime.Today, currentRepo,
                    errors, failedCodes, stats, progress, pending.Count,
                    () => Interlocked.Increment(ref done2), sw, ct, granularity: gran);
            }

            // 抓完立刻复查这一批还缺不缺——判据仍是"交易日历里有、这只票没有"
            var toConfirm = new List<(string Code, DateTime Day)>();
            var batchStill = new List<RetryTarget>();
            var gaps = audit.FindGaps(batch.Select(r => r.Code).ToList(), gran,
                MarketIndexCatalog.ShanghaiCompositeSymbol);
            foreach (var r in batch)
            {
                if (!gaps.TryGetValue(r.Code, out var days)) continue;          // 补齐了
                var left = days.Where(d => d >= (r.From ?? DateTime.MinValue)
                                        && d <= (r.To ?? DateTime.MaxValue)).ToList();
                if (left.Count == 0) continue;

                int tries = r.Tries + 1;
                if (tries >= AuditMaxTries)
                    toConfirm.AddRange(left.Select(d => (r.Code, d)));          // 认了：数据源就是没有
                else
                    batchStill.Add(new RetryTarget
                    {
                        Code = r.Code, Gran = gran,
                        From = left[0], To = left[^1], Days = left.Count, Tries = tries,
                    });
            }

            // 白名单本来就是即时落库的；这里补上的是"还缺几段、Tries 加到几"那部分记账
            if (toConfirm.Count > 0) audit.Confirm(toConfirm, gran, AuditMaxTries);
            int confirmedInBatch = toConfirm.Select(x => x.Code).Distinct().Count();
            int filledInBatch = Math.Max(0, batch.Length - batchStill.Count - confirmedInBatch);
            filled += filledInBatch;
            confirmed += confirmedInBatch;
            still.AddRange(batchStill);

            SaveProgress();
            progress?.Report($"　{label} 第 {batchNo}/{batchTotal} 批已落账："
                           + $"补上 {filledInBatch} 段、还缺 {batchStill.Count} 段、"
                           + $"{confirmedInBatch} 段判定数据源确实没有"
                           + (batchNo < batchTotal ? "（现在停也不会丢前面几批的进度）" : ""));
        }

        SaveProgress();
        progress?.Report($"　{label}：{stats.Summarize()}；补上 {filled} 段、"
                       + $"还缺 {still.Count} 段（下轮再试）、{confirmed} 段判定数据源确实没有"
                       + (confirmed > 0 ? "（多半是停牌，以后体检不再报）" : ""));
        done.Add($"{label}空洞 {pending.Count} 段");
    }

    /// <summary>
    /// 修体检报出的**值问题**（行在但值错）——认领自己那一条（2026-09-13 二期）。
    ///
    /// 跟缺行分开是因为复查方式根本不同：值错的行**一直都在**，拿"行在不在"去复查会一律
    /// 判成"已补齐"划掉，哪怕值根本没被覆盖。实际的抓改逻辑在 <see cref="FillValueIssuesAsync"/>，
    /// 那里和 <c>ValueIssueFixPlan</c> 一行没动——这里只做待办格式的进出转换。
    /// </summary>
    private async Task FillValueTodoAsync(
        string taskId, NamedBarSource source, SqliteBarRepository currentRepo,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, List<string> done,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct)
    {
        List<RetryTarget> pending;
        lock (_dbLock)
            pending = _manifestStore.Load().Todo(taskId, RetryTodoKind.ValueIssue)?.Targets.ToList()
                      ?? new List<RetryTarget>();
        if (pending.Count == 0) return;

        void Save(List<MissingBarRange> updated)
        {
            lock (_dbLock)
            {
                var m = _manifestStore.Load();
                m.SetTodo(taskId, RetryTodoKind.ValueIssue, updated.Select(ToTarget).ToList());
                _manifestStore.Save(m);
            }
        }

        var parts = new List<string>();
        var left = await FillValueIssuesAsync(
            source, currentRepo, pending.Select(ToRange).ToList(),
            errors, failedCodes, parts, progress, sw, ct, Save);
        Save(left);
        done.Add($"{TaskLabel(taskId)}值问题 {pending.Count} 段");
    }

    /// <summary>待办 ↔ 值问题修复那条链用的老结构之间的转换（只在 <see cref="FillValueTodoAsync"/> 用）。</summary>
    private static MissingBarRange ToRange(RetryTarget t) => new()
    {
        Code = t.Code, Granularity = NormalizeGran(t.Gran),
        From = t.From ?? DateTime.MinValue, To = t.To ?? DateTime.MaxValue,
        Days = t.Days, Tries = t.Tries, Reason = t.Reason ?? AuditFindingKind.Gap,
    };

    private static RetryTarget ToTarget(MissingBarRange r) => new()
    {
        Code = r.Code, Gran = NormalizeGran(r.Granularity),
        From = r.From, To = r.To, Days = r.Days, Tries = r.Tries,
        Reason = r.EffectiveReason,
    };

    /// <summary>任务 id → 日志里那个短标签。</summary>
    private static string TaskLabel(string taskId) => taskId switch
    {
        RetryTaskIds.StockHfqBars => "个股·后复权",
        RetryTaskIds.StockRawBars => "个股·不复权",
        RetryTaskIds.StockDayBars => "个股·前复权",
        RetryTaskIds.EtfBars => "ETF",
        RetryTaskIds.EtfRawBars => "ETF·不复权",
        RetryTaskIds.IndexBars => "指数",
        RetryTaskIds.NetInflow => "资金净流入",
        RetryTaskIds.Roster => "流通市值",
        RetryTaskIds.IndexCons => "指数成分",
        RetryTaskIds.IndexWeight => "指数权重",
        RetryTaskIds.Shareholder => "股东数据",
        RetryTaskIds.Dividend => "分红送配",
        RetryTaskIds.DelistedTails => "退市股收尾",
        _ => taskId,
    };


    /// <summary>
    /// 体检报出的**值问题**（行在但值错）跟缺行用同一份 <see cref="Manifest.MissingBars"/>，
    /// 但补法和复查方式必须分开，见 <see cref="FetchTaskCatalog"/> 之外的
    /// doc/bar-value-audit-design.md §5。这个常量是复查用的截止线，跟体检那边的
    /// <c>FullAuditTask.SettleDays</c> 是同一个 2 天——两处改了一处就会各说各话。
    /// </summary>
    private const int ValueRecheckSettleDays = 2;

    /// <summary>
    /// 修体检报出的值问题（2026-09-09）。跟缺行那条路有**三处**关键不同：
    ///
    /// ① **抓法按 Reason 分**。"多口径量额对不上"只覆盖 volume/amount/turnover 三列、绝不动 OHLC
    ///    （那三列不受复权影响，任何时候抓都是同一个值；而历史行的价格是当年的复权基准，
    ///    覆盖会造成同一序列里新旧基准混杂）。其余三类整段重抓，靠
    ///    <see cref="SqliteBarRepository.InsertOrRefreshUnconfirmed"/> 覆盖掉未确认的行。
    ///
    /// ② **复查用对应判据，不是 FindGaps**。值错的行**一直都在**，拿"行在不在"去复查会一律判成
    ///    "已补齐"划掉——哪怕值根本没被覆盖（比如又在盘中跑了一次）。复查只查这一批的 code，
    ///    不是全库扫描（那等于把体检重跑一遍）。
    ///
    /// ③ **不进「确认没有」白名单**。那份名单是给停牌用的（补两轮拿不到就认了），让错值进去
    ///    等于发永久豁免。<c>Tries</c> 到顶就一直留在名单里报警。
    /// </summary>
    /// <param name="commit">每批跑完调一次，传入"当前完整的值类名单"，由调用方写回 manifest。</param>
    private async Task<List<MissingBarRange>> FillValueIssuesAsync(
        NamedBarSource source, SqliteBarRepository currentRepo, List<MissingBarRange> pending,
        ConcurrentBag<string> errors, ConcurrentBag<string> failedCodes, List<string> parts,
        IProgress<string>? progress, Stopwatch sw, CancellationToken ct,
        Action<List<MissingBarRange>> commit)
    {
        var still = new List<MissingBarRange>();
        var auditor = new SqliteBarValueAuditor(_paths.CurrentDb);
        var cutoff = DateTime.Today.AddDays(-ValueRecheckSettleDays);
        var stats = new FetchStats();
        int done = 0, fixedTotal = 0, skippedTotal = 0, batchNo = 0;
        int batchTotal = (pending.Count + AuditBatchSize - 1) / AuditBatchSize;

        progress?.Report($"修体检报出的值问题：{pending.Count} 段"
                       + $"（{string.Join("、", pending.GroupBy(r => r.EffectiveReason).Select(g => $"{ReasonLabel(g.Key)} {g.Count()}"))}）"
                       + $"，分 {batchTotal} 批，每批跑完就落账…");

        foreach (var batch in pending.Chunk(AuditBatchSize))
        {
            batchNo++;
            // 阶段一/阶段二（挑出抓不了的、按 (票,口径) 分组、inconsistent 连基准 day 一起抓）
            // 都在 ValueIssueFixPlan 里——纯函数、有单测，理由同 ValueIssueRecheck：
            // 这条路错了是静默的（每轮照发请求、每轮修不掉），而 orchestrator 没法单测。
            var plan = ValueIssueFixPlan.Build(batch, source.Fetcher.SupportsHfq);
            var skipped = plan.Skipped;

            foreach (var f in plan.Fetches)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (_, fresh) = await source.Fetcher.FetchAsync(
                        f.Code, f.Granularity, f.From, f.To, ct);

                    if (f.Write == ValueFixWrite.ThreeColumns)
                    {
                        // 只覆盖 volume/amount/turnover 三列、**绝不动 OHLC**：历史行的价格是
                        // 当年抓取时的复权基准，覆盖会造成同一序列里新旧基准混杂。
                        int n;
                        lock (_dbLock) n = currentRepo.UpdateVolumeAmountTurnover(fresh, f.Granularity);
                        if (n > 0) stats.FetchedWithNewData(); else stats.FetchedButEmpty();
                    }
                    else if (fresh.Count > 0)
                    {
                        // 盘中固化 / NULL / OHLC：抓回来**直接交给 InsertOrRefreshUnconfirmed**，
                        // 让它的 UPSERT 条件裁决（只覆盖未确认的行，已确认的一行不动）。
                        //
                        // ⚠ 这里**不能走 ProcessOneStockAsync**（2026-09-09 生产实测踩的）：
                        // 它在 overwrite=false 时只把"库里没有的行"放进 toInsert，而值错的行是
                        // "**存在**但值错"，压根到不了 UPSERT 那一步——覆盖条件没机会生效。
                        // 那一轮 5282 段盘中固化全部判"还在"，002650 的 OHLC 一直是四价合一 6.04。
                        lock (_dbLock) currentRepo.InsertOrRefreshUnconfirmed(fresh);
                        stats.FetchedWithNewData();
                    }
                    else stats.FetchedButEmpty();

                    // 基准重抓不是名单里的段（是为了修别的口径顺带抓的），不计进度
                    if (!f.IsBaselineRefetch) Interlocked.Increment(ref done);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{f.Code} {GranLabel(f.Granularity)} 值修复失败：{ex.Message}");
                    failedCodes.Add(f.Code);
                }
            }

            // ── 复查：按 Reason 用对应判据重查这一批（只查这批 code）──
            var codes = batch.Select(r => r.Code).Distinct().ToList();
            var live = new Dictionary<(string, string, string), List<DateTime>>();
            try
            {
                foreach (var i in auditor.RowIssues(cutoff, codes: codes)
                                         .Concat(auditor.CrossGranularityMismatch(cutoff, codes: codes)))
                {
                    var key = (i.Code, i.Granularity, i.Kind);
                    if (!live.TryGetValue(key, out var days)) live[key] = days = [];
                    days.Add(i.Day);
                }
            }
            catch (Exception ex)
            {
                // 复查查不动就保守处理：这一批原样留着（宁可下轮重来，也不能当成"修好了"划掉）
                errors.Add($"值问题复查失败（本批原样留着）：{ex.Message}");
                still.AddRange(batch);
                commit(still.Concat(pending.Skip(batchNo * AuditBatchSize)).ToList());
                continue;
            }

            int fixedInBatch = 0, stillInBatch = 0;
            foreach (var r in batch)
            {
                if (skipped.Contains(r)) { still.Add(r); skippedTotal++; continue; }

                // 判定本体在 ValueIssueRecheck（Logic 层纯函数，有单测）——判据不再命中就是修好了
                live.TryGetValue((r.Code, r.Granularity, r.EffectiveReason), out var days);
                var survived = ValueIssueRecheck.Survives(r, days);
                if (survived == null) { fixedInBatch++; continue; }

                stillInBatch++;
                still.Add(survived);
            }
            fixedTotal += fixedInBatch;

            commit(still.Concat(pending.Skip(batchNo * AuditBatchSize)).ToList());
            progress?.Report($"　值问题 第 {batchNo}/{batchTotal} 批已落账：修好 {fixedInBatch} 段、"
                           + $"还在 {stillInBatch} 段"
                           + (batchNo < batchTotal ? "（现在停也不会丢前面几批的进度）" : ""));
        }

        commit(still);
        parts.Add($"值问题 修好 {fixedTotal}/{pending.Count} 段");
        progress?.Report($"　值问题：{stats.Summarize()}；修好 {fixedTotal} 段、还在 {still.Count - skippedTotal} 段"
                       + (skippedTotal > 0 ? $"、{skippedTotal} 段跳过（数据源不支持该口径 / 本地重算的口径）" : "")
                       + "。⚠ 还在的**不会**进「数据源确实没有」白名单——那是给停牌用的，"
                       + "值错进去等于发永久豁免，所以它会一直报到真修好为止。");
        return still;
    }

    /// <summary>值问题的中文名，只用在日志里。</summary>
    private static string ReasonLabel(string reason) => reason switch
    {
        AuditFindingKind.Intraday => "盘中固化",
        AuditFindingKind.NullValue => "关键列NULL",
        AuditFindingKind.Ohlc => "OHLC不自洽",
        AuditFindingKind.Inconsistent => "多口径量额对不上",
        _ => reason,
    };

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
