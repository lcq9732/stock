using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 一份报告的处理结果。
///
/// ⚠ **这个类型存在的唯一理由，是让"失败"也能落库。**只 yield 指标的话，
///   解析不出来的那些报告就没有痕迹，界面上分不清"没抓""抓失败""这项本来就取不到"。
///   跟 <see cref="SubsidiaryExtractTask"/> 的 <c>ParsedReport</c> 同一个套路。
/// </summary>
public sealed record FetchedReport(
    BankReportFetchState State, IReadOnlyList<BankRegulatoryMetric> Metrics);

/// <summary>
/// 【金融监管指标】（2026-08-29 新增，2026-09-15 迁到新任务框架）。
///
/// ════ 它是什么 ════
/// 银行/券商/保险的三张报表里没有监管指标（不良率、拨备覆盖率、资本充足率、风险覆盖率、
/// 偿付能力充足率…），只能从年报/中报 PDF 里解析。
///
/// ════ 五件事 ════
///   ① 认金融机构 —— 靠特征科目，见 <see cref="FinancialInstitutionRoster"/>
///   ② 本地已有 PDF 全量重解析（**零请求**）—— 解析规则改进后的自愈路径
///   ③ 逐只逐期下载 + 解析，成功失败都落状态
///   ④ 生成「待手工回填清单」
///   ⑤ 全程按"已披露 + 本地已有"跳过，不做无谓的请求
///
/// ════ 为什么 ② 必须在 ③ 之前 ════
/// PDF 留在本地就是为了这个：规则改进后（各家版式差异会不断暴露新问题）不用重新下载
/// 就能用新规则重跑，旧的错值被 INSERT OR REPLACE 覆盖。真实修过的坑：注释角标「（注3）」
/// 没清干净，平安银行的拨备覆盖率被存成 3.0；目录页"七、资本充足率分析 42"的页码被当成资本充足率。
///
/// ════ 拆分后的边界（2026-09-15）════
/// 「列公告 → 抠 PDF 直链 → 下载落盘」在 <see cref="SinaReportIndex"/>（通用数据源，
/// 【子公司名单】也用它）；"拿解析结果当终判、逐个候选试"在 <see cref="BankReportFetcher"/>；
/// 这里只做编排。拆之前三层混在一个类里，下载的成功判据是"解析出了银行指标"——
/// 非金融年报一份都过不了，那条链根本没法复用。
/// </summary>
public sealed class BankRegulatoryTask : FetchTaskBase<FetchedReport>
{
    /// <summary>一批几份。批是截断粒度，PDF 慢，别设大。</summary>
    private const int BatchSize = 4;

    private readonly SqliteBankRegulatoryRepository _repo;
    private readonly SqliteFinancialRepository _finRepo;
    private readonly SqliteEarningsScheduleRepository _schedule;
    private readonly string _reportsDir;
    private readonly string _dbPath;

    /// <summary>
    /// 补抓一小批金融股的财务报表。
    ///
    /// 为什么要它：机构类型靠特征科目认，而那些科目是 v3/v4 才加的。要求先跑完全市场
    /// 【拉取财务报表】（5000+ 只 × 3 张报表）才能用这个任务，等待时间完全不成比例——
    /// 真正需要的只有一百来只。这段抓取的实现在 FetchOrchestrator 里，用委托注进来，
    /// 免得这个任务反过来依赖编排层。
    /// </summary>
    private readonly Func<IReadOnlyList<string>, IProgress<string>?, CancellationToken, Task>? _refetchFinancials;

    /// <summary>本轮识别出的目标，收尾生成手工回填清单要用。</summary>
    private IReadOnlyList<(string Code, FinancialInstitutionKind Kind)> _targets = [];

    private int _ok, _fail, _skip, _metricTotal;
    private readonly List<string> _errors = [];

    public BankRegulatoryTask(
        SqliteBankRegulatoryRepository repo,
        SqliteFinancialRepository finRepo,
        SqliteEarningsScheduleRepository schedule,
        FetchPaths paths,
        Func<IReadOnlyList<string>, IProgress<string>?, CancellationToken, Task>? refetchFinancials = null)
    {
        _repo = repo;
        _finRepo = finRepo;
        _schedule = schedule;
        _reportsDir = paths.ReportsDir;
        _dbPath = paths.CurrentDb;
        _refetchFinancials = refetchFinancials;
    }

    public override FetchActionId Id => FetchActionId.BankRegulatory;

    protected override async IAsyncEnumerable<IReadOnlyList<FetchedReport>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _repo.EnsureSchema();
        bool refetchAll = args.Mode == FetchMode.FirstBackfill;

        // ⚠ **首个 await 之前的活全部推到线程池**。异步迭代器在第一次 yield 之前是同步跑在
        //   调用方线程上的，而调用方是 UI 线程（FetchTaskRegistry.RunAsync 只是 await，
        //   没有 Task.Run）。这里有两段重活：读全市场财务快照（1400 万行）和重解析
        //   375 份 PDF（16 分钟）——不推出去界面会整段假死。
        //
        //   2026-09-15 真机上栽过一次：老实现在 MainViewModel 那个 case 里包了 Task.Run，
        //   迁到新框架时连那行注释一起删掉了，点下去界面直接冻住。
        //   骨架不该替任务猜哪段重，**谁有重活谁自己推**（SubsidiaryExtractTask 同例）。

        // ── ① 认金融机构（必要时先补抓它们的财务）──────────────────────────────
        var latest = await Task.Run(_finRepo.GetLatestSnapshotByCode, ct);
        var fetchState = await Task.Run(_finRepo.GetFetchStateByCode, ct);
        var needFinancial = FinancialInstitutionRoster.NeedFinancialRefetch(
            latest,
            code => fetchState.TryGetValue(code, out var st) ? st.KeysVersion : 0,
            FinancialKeys.Version);

        if (needFinancial.Count > 0 && _refetchFinancials != null)
        {
            Report($"检测到 {needFinancial.Count} 只金融股的科目集低于 v{FinancialKeys.Version}"
                 + "（缺银行/券商/保险的特征科目，认不出机构类型），"
                 + "先补抓它们的财务报表——只抓这一批，不用等全市场。", phase: "前置");
            await _refetchFinancials(needFinancial, new Progress<string>(s => Report(s)), ct);
            latest = _finRepo.GetLatestSnapshotByCode();   // 重新读，这次才认得出银行
        }

        _targets = FinancialInstitutionRoster.Classify(latest);
        if (_targets.Count == 0)
        {
            Report("⚠ 没有识别出任何银行/券商/保险。若本地库从没抓过财务报表，"
                 + "请先跑一次【拉取财务报表】再回来点这个。");
            _errors.Add("没有识别出任何银行/券商/保险");
            yield break;
        }

        int nBank = _targets.Count(t => t.Kind == FinancialInstitutionKind.Bank);
        int nBroker = _targets.Count(t => t.Kind == FinancialInstitutionKind.Broker);
        int nInsurer = _targets.Count(t => t.Kind == FinancialInstitutionKind.Insurer);
        Report($"识别出 银行 {nBank} 家、券商 {nBroker} 家、保险 {nInsurer} 家，"
             + "开始抓取监管指标（只抓年报和中报）...", 0, _targets.Count, "抓取");

        // ── ② 本地已有 PDF 全量重解析（零请求）────────────────────────────────
        await Task.Run(() => ReparseCached(latest, ct), ct);

        var done = refetchAll
            ? []
            : await Task.Run(_repo.GetSucceeded, ct);

        // 「这家这一期披露了没有」——没披露就别去翻公告列表了。
        // 原来是无条件为每一家发请求查列表，而这一段限流很紧（约 17 请求/分钟），
        // 87 家跑一轮要个把小时。披露季前期绝大多数机构根本还没出报告，那一小时全是空转。
        // ⚠ 查不到披露记录的照常查——兜底方向只能是"多查"，不能因为查不到就漏掉一家。
        var disclosed = refetchAll
            ? new Dictionary<string, DateTime>(StringComparer.Ordinal)
            : await Task.Run(() => _schedule.GetLatestDisclosedPeriodByCode(DateTime.Today), ct);

        // ⚠ **本地文件缺了的票不许跳过**（2026-09-16 补）。
        //
        //   下面那道"最新一期已有就整只票跳过"的优化本身是对的（省掉大量空转请求），
        //   但它假定"最新一期在 = 这只票齐了"。老期次的文件要是没了，它就永远补不回来——
        //   实测 11 个期次这么挂着，最早的半个月没人管：
        //     000166 2024/2025 年报、601601 中国太保 2024/2025、601998 中信银行 2024/2025…
        //   那批是被重解析的 LooksLikeReport 误删的（那道判据 2026-09-16 已取消），
        //   但只要"删了不补"这条路还在，将来任何原因造成的缺失都会变成永久空洞。
        var needRefill = await Task.Run(() =>
            _repo.GetUnsuccessful()
                 .Where(x => !File.Exists(SinaReportIndex.PathOf(_reportsDir, x.Code, x.ReportDate)))
                 .Select(x => x.Code)
                 .ToHashSet(StringComparer.Ordinal), ct);
        if (needRefill.Count > 0)
            Report($"{needRefill.Count} 只票在本地缺报告文件，这一轮不跳过它们（会去补）。", phase: "抓取");

        // 限流分两套。页面侧参数参照 financialProvider 那段血泪教训（3并发/1秒 → HTTP 456、
        // 整轮零成功）取保守值：单并发 + 3 秒 + 每 40 个歇 45 秒 ≈ 17 请求/分钟。
        // 文件侧打的是静态服务器、配额独立，但单个 PDF 几 MB，也不并发。
        var index = new SinaReportIndex(
            pageLimiter: new RateLimiter(
                maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(3),
                batchSize: 40, restDuration: TimeSpan.FromSeconds(45)),
            fileLimiter: new RateLimiter(
                maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2),
                batchSize: 30, restDuration: TimeSpan.FromSeconds(30)));
        var fetcher = new BankReportFetcher(index, _reportsDir);

        void Forward(string s) => Report(s);
        fetcher.OnStatus += Forward;
        try
        {
            var batch = new List<FetchedReport>(BatchSize);
            for (int i = 0; i < _targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (code, kind) = _targets[i];
                string kindName = kind switch
                {
                    FinancialInstitutionKind.Bank => "银行",
                    FinancialInstitutionKind.Broker => "券商",
                    _ => "保险",
                };

                // 已披露的最新一期本地已经拿到了 → 这一轮它没有新东西，一个请求都不用发。
                // ⚠ 但**本地缺文件的票不能跳**，否则那些期次永远补不回来（见上面 needRefill）。
                if (disclosed.TryGetValue(code, out var latestDisclosed)
                    && done.Contains((code, latestDisclosed))
                    && !needRefill.Contains(code))
                {
                    _skip++;
                    continue;
                }

                Report($"[{i + 1}/{_targets.Count}] {code}（{kindName}）查找年报/中报...",
                       i + 1, _targets.Count, "抓取");

                // 每一期拿**全部候选**（标题只做粗筛+排序），下面逐个试到解析出指标为止
                SortedDictionary<DateTime, List<SinaReportIndex.ReportRef>> byDate;
                try { byDate = await fetcher.ListCandidatesAsync(code, maxPerKind: 2, ct); }
                catch (Exception ex)
                {
                    _fail++;
                    _errors.Add($"{code} 取公告列表失败：{ex.Message}");
                    continue;
                }

                foreach (var (period, candidates) in byDate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (done.Contains((code, period))) { _skip++; continue; }
                    var r = candidates[0];

                    var (state, metrics) = await fetcher.FetchBestAsync(
                        candidates, kind, s => Report(s), ct);

                    if (state.Status == "ok")
                    {
                        _ok++; _metricTotal += state.MetricCount;
                        Report($"    {r.ReportDate:yyyy-MM-dd} {r.Title} → {state.MetricCount} 个指标");
                    }
                    else
                    {
                        _fail++;
                        Report($"    ⚠ {r.ReportDate:yyyy-MM-dd} {state.Status}：{state.Message}");
                    }

                    // 成功失败都进批——静默跳过会让界面分不清"没抓"和"抓失败"
                    batch.Add(new FetchedReport(state, metrics));
                    if (batch.Count < BatchSize) continue;
                    yield return batch.ToList();
                    batch.Clear();
                }
            }
            if (batch.Count > 0) yield return batch;
        }
        finally { fetcher.OnStatus -= Forward; }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<FetchedReport> batch, CancellationToken ct)
        // 同样推出去：写库是同步的，而骨架的 await foreach 续体可能回到 UI 线程。
        => Task.Run(() =>
        {
            foreach (var r in batch)
            {
                if (r.Metrics.Count > 0) _repo.Upsert(r.Metrics);
                _repo.UpsertState(r.State);
            }
        }, ct);

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var summary = $"金融监管指标：成功 {_ok} 份（共 {_metricTotal} 个指标）、"
                    + $"失败 {_fail} 份、跳过已有 {_skip} 份。PDF 缓存在 {_reportsDir}。";
        Report(summary);

        // ── ④ 生成「待手工回填清单」 ───────────────────────────────────────
        // PDF 解析做不到 100%（个别年报的字体 PdfPig 和 pdftotext 都读不动），与其让体检表
        // 一直显示"待接入"、让人对着三个字发呆，不如直接给一份能照着干活的表：
        // 哪家、哪一期、缺哪几个数、翻年报的哪一章能找到。填完用【导入手工数据】写回来，
        // 之后重解析也不会覆盖（来源标 manual）。
        if (_targets.Count > 0)
        {
            try
            {
                Report("正在生成待手工回填清单（要逐份核对财报里到底披露了哪些指标，请稍候）...",
                       phase: "收尾");
                // 生成清单要逐份翻 PDF，也是重活，同样别留在调用方线程上
                var listPath = await Task.Run(() => ManualFillWorklist.Generate(
                    _dbPath, _reportsDir, _reportsDir, _targets, s => Report(s)), ct);
                if (listPath != null)
                {
                    var missing = File.ReadAllLines(listPath).Length - 1;
                    Report($"⚠ 有 {missing} 项指标需要你过一遍，已生成清单：{listPath}"
                         + "（用 Excel 打开，看倒数第二列「OCR识别值」："
                         + "**有值的**是数字被转曲、只能靠 OCR 认出来的，对着 PDF 核一眼——"
                         + "认对了就别动，认错了才在最后一列填正确值；"
                         + "**空着的**是压根没解析出来的，请在最后一列填上。"
                         + "填完点【导入手工数据】写回，之后重新解析不会覆盖你确认过的值）。"
                         + "清单里**只列财报确实披露的项**——公司本身没有的指标"
                         + "（比如纯寿险公司没有综合成本率）不会让你去找。");
                }
                else Report("所有机构的核心监管指标都已齐全，也没有待核对的 OCR 值。");
            }
            catch (Exception ex) { _errors.Add($"生成手工回填清单失败：{ex.Message}"); }
        }

        return _errors.Count > 0
            ? new TaskRunResult(TaskState.Completed, _errors, Progress: summary)
            : TaskRunResult.Ok(nothingToDo: _ok == 0 && _fail == 0, progress: summary);
    }

    /// <summary>
    /// 本地已有 PDF 全量重解析（零请求）。逻辑在 <see cref="BankReportReparser"/>——
    /// 【重解析已有PDF】那个单项入口跑的是同一份，抄两份迟早会不一致。
    /// </summary>
    private void ReparseCached(IReadOnlyDictionary<string, FinancialSnapshot> latest,
                               CancellationToken ct)
        => new BankReportReparser(_repo, _reportsDir).Run(latest, s => Report(s), ct);
}
