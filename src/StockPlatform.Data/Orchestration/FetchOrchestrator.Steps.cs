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

    // 【股票名册与流通市值】整项 2026-09-18 迁到新任务框架
    // （StockPlatform.Tasks/RosterMarketCapTask）：整轮扫描、新股发现、
    // "值属于哪个交易日"的判定都在那儿——后者现在先问本地交易日历，问不出才抓上证指数日线。
    // 见 doc/index-roster-task-design.md。

    // ───────────────────────────── 2. 资金净流入 ─────────────────────────────
    //
    // 整项 2026-09-18 迁到新任务框架（StockPlatform.Tasks/NetInflowTask），本类不再有它的入口。
    // 三类待办（失败名单/整天缺失/残缺日）现在也都归它自己补——最后那类以前**没有人补**，
    // 每轮只在日志里喊一句"只能人工处理"。见 doc/netinflow-task-design.md。
    // 【拉取区间数据】里的资金流那半边仍在 FetchOrchestrator（FetchNetInflowRangeAsync）。

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
    //
    // 两项都已迁到新任务框架，本类不再有它们的入口：
    //   ·【融资余额】2026-09-18 → StockPlatform.Tasks/MarginTask（见 doc/margin-task-design.md）。
    //     顺带删掉了 RunStepBackfillDailyOneAsync——龙虎榜 09-17 迁走之后，两融是它唯一的用户。
    //   ·【龙虎榜】2026-09-17 → StockPlatform.Tasks/LhbTask，落库统一走 LhbDayWriter。
    // 两项的残缺日待办现在也都归各自的任务补（HandlesBacklog=true），
    // 见 doc/fill-backlog-to-tasks-design.md。

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

    // 【指数成分名单】【指数权重】整项 2026-09-18 迁到新任务框架
    // （StockPlatform.Tasks/IndexConsTask、IndexWeightTask）。权重那两道筛子
    // （本地这一期还新鲜 / 确认没有权重文件）跟着搬了过去——丢了它们就是每轮拿四五百个
    // 注定 404 的请求去撞中证的反爬。见 doc/index-roster-task-design.md。

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
