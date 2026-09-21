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

    // 整项 2026-09-21 迁到新任务框架（StockPlatform.Tasks/IndexBarTask），本类不再有它的入口。
    // 写入判据两边共用 Logic 层的 BarWritePlanner，所以跟仍走老路的个股三口径不会分叉。
    // 见 doc/bar-tasks-migration-design.md 第②步。

    // ──────────────────── 5~7. 个股日K（前复权 / 后复权 / 不复权） ────────────────────

    // 三个口径整项 2026-09-21 迁到新任务框架（StockPlatform.Tasks/StockDayBarTask、
    // StockAdjustedBarTask ×2），本类不再有它们的入口。
    //   · 写入判据两边共用 Logic 层的 BarWritePlanner；
    //   · 漂移名单的筛选走 QfqRepairPlanner；不复权"补齐了没有"走 RawBarCompletenessRule；
    //   · 后复权/不复权那两道闸（探一只 + 失败率熔断）走 HfqProbeGate。
    // ⚠ FetchHfqBarsAsync / ProcessOneStockAsync / RunFetchRawBarsAsync **都还在**：
    //   【拉取区间数据】【重取前复权】【重新拉取失败】【全库体检值回补】仍在用它们。

    // ───────────────────────────── 8. ETF 日K ─────────────────────────────

    // 整项 2026-09-21 迁到新任务框架（StockPlatform.Tasks/EtfBarTask），本类不再有它的入口。
    // 名单那道"半截就改用库里存量"的闸搬进了 Logic 层的 EtfListGuard。
    // 【拉取区间数据】里的 ETF 那半边仍在 FetchOrchestrator（FetchEtfBarsForYearAsync）。

    // ───────────────────────────── 9. 退市股收尾 ─────────────────────────────

    // 整项 2026-09-21 迁到新任务框架（StockPlatform.Tasks/DelistedTailTask），本类不再有它的入口。
    // 筛"哪几只缺尾巴、各补哪一段"的判据搬进了 Logic 层的 DelistedTailPlanner。
    // ⚠ CatchUpDelistedTailsAsync 也一并删了——它只有这一个调用方；
    //   【拉取区间数据】里的退市股那半边走的是另一个方法 FetchDelistedForRangeAsync，那个还在。

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
    /// <summary>任务 id → 中文名，只用在日志里。</summary>
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

    // ════════════════════════════════════════════════════════════════════════
    //  补待办那一整块删于 2026-09-21（FillGapTodoAsync / FillValueTodoAsync /
    //  FillValueIssuesAsync / NormalizeGran / ReasonLabel / GranLabel / 两个格式转换）：
    //  K线六项 + ETF不复权都自己补待办了，实现搬到了
    //  StockPlatform.Tasks/BarFetchTaskBase.Backlog.cs 和 .Audit.cs。
    //
    //  搬过去时顺手修了一个错：老代码拿 pending[0].Gran 当整份名单的口径，而 ETF 的空洞
    //  不分口径全记在 StepEtfBars 名下（FullAuditTask.TaskIdOfScope），同一份里可能混着
    //  day 和 day_raw——混着的时候后一半会用错口径去抓、两轮后被错判成"数据源确实没有"。
    //  新实现按每一段自己的 Gran 分组。
    //
    //  ⚠ 判据本体一直在 Logic（ValueIssueFixPlan / ValueIssueRecheck），搬的只是编排；
    //    白名单和体检那侧（SqliteMissingBarRepository / SqliteBarValueAuditor / FullAuditTask）
    //    一行没动。
    // ════════════════════════════════════════════════════════════════════════

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
