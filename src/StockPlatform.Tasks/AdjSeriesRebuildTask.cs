using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 一只票算完的结果。<see cref="WholeRewrite"/> 决定落账方式：整段重算要先删掉旧序列
/// （因子一变全历史都变），增量只是把新的几根追加上去。
/// </summary>
/// <param name="Bars">要写进 <c>day_adj</c> 的行。整段重算是全历史，增量只有新增那几根。</param>
/// <param name="Check">收益率自检，见 <see cref="AdjustFactorCalculator.ReturnCheck"/>。
/// 增量那条路不自检（没重算因子，没什么可验的），给 null。</param>
public sealed record AdjRebuildOutcome(
    string Code,
    IReadOnlyList<Bar> Bars,
    bool WholeRewrite,
    int Applied,
    int Skipped,
    AdjustFactorCalculator.ReturnCheck? Check,
    IReadOnlyList<string> SkipNotes);

/// <summary>
/// 【重算回测序列】——2026-09-10 从 <c>FetchOrchestrator.RunRebuildAdjSeriesAsync</c> 迁过来。
/// <c>day_adj</c> ＝ 不复权价 × 本地算的乘法式复权因子。**纯本地计算，一个请求都不发。**
///
/// 为什么不用数据源的后复权、为什么每条除权记录都要用价格校验，见
/// <see cref="AdjustFactorCalculator"/> 的类注释（那些判据一行没动，只是搬了个地方）。
///
/// ════ 为什么迁 ════
/// 判据是"迁移成本 + 以后的维护成本"（见 doc/full-audit-task-migration-design.md §0）。这一项是
/// 老任务里最容易迁的一类：**依赖只有一个 db 路径**，不碰 manifest、不占数据源、调用点只有一个。
/// 顺带换来三件实在的好处，见下面「Deadline」和 <see cref="OnCompletedAsync"/>。
///
/// ════ 批的粒度 = 一只票 ════
/// 每算完一只 yield 一次，框架存一批。跟【全库数据体检】的"面级落账"不同——那边必须整面替换，
/// 因为名单的语义是"这个面当前的完整缺口清单"，落一半会把没扫的票当成"没有缺口"（安静的错）。
/// 这里每只票的 <c>day_adj</c> 各自独立，算过的就是对的、没算的下一轮判据照样认得出来，
/// 所以按只落账是安全的。
///
/// ════ Deadline 取代了原来的"预估只数" ════
/// 老调用点是 <c>DeadlineToCount(deadline, 0.2 秒/只)</c>——**先估**本轮能算几只，然后不管实际
/// 时间跑完。可实测一只从几毫秒（增量）到几百毫秒（整段重算）都有，那个平均值意义不大：估少了
/// 空窗没用满，估多了直接超时。框架的 <see cref="TaskRunArgs.Deadline"/> 是每批之后看**真实钟点**。
///
/// ════ ⚠ CPU 密集的活必须自己扔线程池 ════
/// 框架的 <c>FetchAsync</c> 是 async 迭代器，跑在调用者线程上；而这里每只票要读几千行、算、写回，
/// 4020 只票是几十分钟的同步活。所以每只票都包在 <c>Task.Run</c> 里——老代码是整个方法包一层
/// <c>Task.Run</c>，迁过来时这一层不能丢，丢了就是 UI 卡死几十分钟。
///
/// ════ ⚠ 没有 orchestrator 那把 _dbLock 了 ════
/// 老代码每次读写都套 <c>lock (_dbLock)</c>（orchestrator 的私有实例锁），迁出来就没有了。
/// 这不是"反正串行所以无所谓"——占用表里**本地项不占数据源、永远可并发**
/// （见 <c>FetchAction.EffectiveSources</c>），所以这一项完全可能跟正在写 <c>day_raw</c> 的
/// 【个股日K】同时跑。撑住它靠的是另外两件事：
///   ① 库是 WAL（见 <c>SqliteSchema.EnsureSchema</c>），读写互不阻塞；
///   ② 写事务的粒度是**一只票一次**（毫秒级），撞上 SQLITE_BUSY 时
///      Microsoft.Data.Sqlite 默认有 30 秒的重试窗口，够等。
/// 所以**别把落账合并成大事务**——那会把"毫秒级冲突"变成"分钟级互等"。
/// </summary>
public sealed class AdjSeriesRebuildTask(string dbPath) : FetchTaskBase<AdjRebuildOutcome>
{
    public override FetchActionId Id => FetchActionId.RebuildAdjSeries;

    /// <summary>跳过原因最多列几条（原来也是 30）。</summary>
    private const int MaxSkipNotes = 30;

    private readonly SqliteBarRepository _bars = new(dbPath);
    private readonly SqliteDividendRepository _dividends = new(dbPath);

    // ── 汇总计数：FetchAsync 里累加，OnCompletedAsync 里出那一行报告 ──
    private int _todo, _written, _incremental, _rebuilt, _applied, _skipped;
    private long _checkedDays, _minorDrift, _realErrors;
    private readonly List<string> _skipNotes = [];

    protected override async IAsyncEnumerable<IReadOnlyList<AdjRebuildOutcome>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _bars.EnsureSchema();
        bool whole = args.Mode.HasFlag(FetchMode.FirstBackfill);

        // 判据（五条，见 SqliteAdjSeriesAuditor.BuildPlan）跟界面上那个"待重算 N 只"是同一份。
        var auditor = new SqliteAdjSeriesAuditor(dbPath);
        SqliteAdjSeriesAuditor.Plan plan;
        if (whole)
        {
            Report("全量重算：忽略五条判据，有不复权日线的票**全部**整段重来"
                 + "（改过复权算法之后用这个，日常别用——一轮几十分钟）...");
            plan = new SqliteAdjSeriesAuditor.Plan(
                await Task.Run(auditor.AllCodesWithRawBars, ct),
                // 全量重算不走增量那条路，所以这三份中间结果用不上，给空的
                new Dictionary<string, DateTime>(), new Dictionary<string, DateTime>(), []);
        }
        else
        {
            Report("正在统计哪些股票的回测序列要重算（要扫一遍全库的日线索引，通常几十秒，请稍等）…");
            plan = await Task.Run(auditor.BuildPlan, ct);
            if (plan.StaleEventCount > 0)
                Report($"其中 {plan.StaleEventCount} 只是因为分红/配股记录有更新——"
                     + "复权因子是拿这些事件算的，事件一变整条序列都得重算。");
        }

        _todo = plan.Codes.Count;
        if (_todo == 0)
        {
            Report("回测序列已经跟不复权一样新了，这一轮没什么可做。");
            yield break;
        }
        Report($"重算回测序列：{_todo} 只待算（本地计算，不联网）...", done: 0, total: _todo);

        // 全库配股一次读进内存（2026-09-01）：全市场配股记录总共几千条，比在循环里逐只查
        // 5500 次便宜得多。没有 RightsIssue 表（老库还没抓过分红）时拿到空字典，行为跟以前一致。
        var rightsByCode = await Task.Run(LoadRights, ct);
        if (rightsByCode.Count > 0)
            Report($"已载入 {rightsByCode.Count} 只股票的配股记录（配股是第四类除权，不还原会多出假阴线）。");

        // 本轮重算的时间戳，整批共用——写进 day_adj 的 fetched_at，代表"这条序列是什么时候算的"。
        // CodesWithStaleEvents 拿它跟除权事件的 fetched_at 比，来决定要不要重算（2026-09-04 修）。
        var stamp = DateTime.Now;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 按**时间**节流而不是按个数：增量的票几毫秒就过、整段重算的要几百毫秒，同样 500 只
        // 快的 3 秒慢的两分半，静默时长完全不可控（实测哑到 2 分 24 秒）。见 ProgressThrottle。
        var tick = new ProgressThrottle(new ReportSink(Report));
        int seen = 0;

        foreach (var code in plan.Codes)
        {
            ct.ThrowIfCancellationRequested();
            AdjRebuildOutcome? one = null;
            try
            {
                one = await Task.Run(() => ComputeOne(code, plan, rightsByCode, stamp, whole), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Report($"  ⚠ {code} 重算回测序列失败：{ex.Message}"); }

            seen++;
            int at = seen;
            tick.Report(() => $"  处理中 {at}/{_todo}（增量 {_incremental} 只、整段重算 {_rebuilt} 只），"
                            + $"用时 {ElapsedText.Format(sw.Elapsed)}");

            // ⚠ 算不出来的（不复权不足两根、或者上面抛了）产出**空批**，框架会跳过 SaveBatchAsync
            //   ——不占 MaxItems 额度、不计入 items。语义正好：那些票本来就无从算起。
            yield return one is null ? Array.Empty<AdjRebuildOutcome>() : [one];
        }

        Report($"  处理完 {seen}/{_todo}，用时 {ElapsedText.Format(sw.Elapsed)}");
    }

    /// <summary>
    /// 落账。**整段重算要先删**：因子一变全历史都变，只 UPSERT 会把"新序列比旧序列短"那一段
    /// 的旧行留在库里（比如前面补了历史之后起点变了）。增量那条路只追加，不能删。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<AdjRebuildOutcome> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            foreach (var o in batch)
            {
                if (o.WholeRewrite) _bars.DeleteByCode(o.Code, Granularity.DayAdj);
                _bars.InsertOrRefreshUnconfirmed(o.Bars);
                _written++;
            }
        }, ct);

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        foreach (var n in _skipNotes) Report("  ⚠ " + n);

        // 「还剩几只」用减法算，**不再重扫一遍全库**（2026-09-10）：老代码这里调
        // GetPendingAdjRebuildCount()，为了打印一个数字又对 1300 万行 GROUP BY 一遍、几十秒。
        // 落了账的票五条判据必然都不成立，所以剩下的就是"没轮到的 + 算不出来的"。
        int left = Math.Max(0, _todo - _written);
        Report($"回测序列更新完成：{_written} 只（其中 {_incremental} 只只追加了新K线、"
             + $"{_rebuilt} 只整段重算），应用除权 {_applied} 次、按价格校验剔除可疑记录 {_skipped} 条"
             + ReturnCheckText()
             + (left > 0 ? $"，待算名单里还有 {left} 只没轮到" : "")
             + $"，用时 {ElapsedText.Format(stats.Elapsed)}。");

        return Task.FromResult<TaskRunResult?>(null);
    }

    // ─────────────────── 一只票 ───────────────────

    /// <summary>纯计算 + 读库，不写库（写在 <see cref="SaveBatchAsync"/>）。跑在线程池上。</summary>
    private AdjRebuildOutcome? ComputeOne(
        string code, SqliteAdjSeriesAuditor.Plan plan,
        Dictionary<string, List<RightsIssueRow>> rightsByCode, DateTime stamp, bool whole)
    {
        var raw = _bars.Query(code, Granularity.DayRaw);
        if (raw.Count < 2) return null;

        // 分红送转 + 配股，一起构成这只票的除权事件序列。
        // 配股是第四类除权（2026-09-01 补上）：漏了它，除权日的真实跳空会被当成真实下跌，
        // 复权序列上凭空多一根阴线——中信证券 2022-01 那次 −5.72%，10配3 的量级到 −15%，
        // 且集中在银行/券商。同一天既送转又配股的，BuildAdjusted 里按 ExDate 分组后自然合并。
        var events = _dividends.GetByCode(code)
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

        if (!whole && TryIncremental(code, raw, events, plan, stamp) is { } appended)
        {
            _incremental++;
            return appended;
        }

        // 整段重算：没算过、或者新增的日子里有除权（因子变了，全历史都要跟着变）
        var adj = AdjustFactorCalculator.BuildAdjusted(code, raw, events, out var report, stamp);
        var check = AdjustFactorCalculator.VerifyReturns(raw, adj, report.AppliedDays);
        _checkedDays += check.Compared;
        _minorDrift += check.MinorDrift;
        _realErrors += check.RealError;
        _applied += report.Applied;
        _skipped += report.Skipped;
        _rebuilt++;
        if (_skipNotes.Count < MaxSkipNotes)
            _skipNotes.AddRange(report.Notes.Take(MaxSkipNotes - _skipNotes.Count));

        return new AdjRebuildOutcome(code, adj, WholeRewrite: true,
                                     report.Applied, report.Skipped, check, report.Notes);
    }

    /// <summary>
    /// 能只补增量就别整段重算——复权因子只在除权日变，没除权的日子把新增那几根乘上现有因子
    /// 追加即可。每个交易日都全量重算的话，5781 只 × 2400 根 = 1400 万行每天读写一遍，纯浪费。
    ///
    /// 三个条件都得成立，缺一个就得整段重来：
    ///   · 尾巴对得上（算过）且**开头也对得上**——raw 要是在前面补长了（补历史），因子基准就变了，
    ///     只追加尾巴会让新旧两段落在不同基准上，接缝处凭空多出一个假跳空；
    ///   · 新增的日子里**没有除权**；
    ///   · 除权事件本身没变过——因子是从最早那天累乘上来的，中间插进一条新记录，
    ///     它之后的每一根都得跟着变。
    /// </summary>
    private AdjRebuildOutcome? TryIncremental(
        string code, List<Bar> raw, List<AdjustFactorCalculator.ExDividend> events,
        SqliteAdjSeriesAuditor.Plan plan, DateTime stamp)
    {
        if (!plan.AdjLatest.TryGetValue(code, out var alRaw)) return null;
        var adjLast = alRaw.Date;

        var newDays = raw.Where(b => b.PeriodStart.Date > adjLast).ToList();
        if (newDays.Count == 0 || newDays.Count >= raw.Count) return null;

        bool exInNewDays = events.Any(e => e.ExDate.Date > adjLast
                                        && e.ExDate.Date <= raw[^1].PeriodStart.Date);
        bool headMatches = plan.AdjEarliest.TryGetValue(code, out var ae0)
                        && ae0.Date <= raw[0].PeriodStart.Date;
        if (exInNewDays || !headMatches || plan.StaleEvents.Contains(code)) return null;

        // 从"最后一根已算好的"反推当前因子，直接乘上去
        var tail = _bars.Query(code, Granularity.DayAdj, adjLast, adjLast);
        var rawAtLast = raw.LastOrDefault(b => b.PeriodStart.Date == adjLast);
        if (tail.Count == 0 || rawAtLast is not { Close: > 0 }) return null;

        double factor = tail[^1].Close / rawAtLast.Close;
        var bars = newDays.Select(b => new Bar
        {
            Code = b.Code, Granularity = Granularity.DayAdj, PeriodStart = b.PeriodStart,
            Open = b.Open * factor, Close = b.Close * factor,
            High = b.High * factor, Low = b.Low * factor,
            Volume = b.Volume, Amount = b.Amount, Turnover = b.Turnover,
            // 跟整段重算一致：盖"算出来的时刻"，不是源K线的抓取时刻。两条路径必须用同一个语义，
            // 否则走过增量的票时间戳偏旧，CodesWithStaleEvents 会把它们误判成"事件比序列新"、反复重算。
            FetchedAt = stamp,
        }).ToList();

        return new AdjRebuildOutcome(code, bars, WholeRewrite: false, 0, 0, null, []);
    }

    // ─────────────────── 零碎 ───────────────────

    private Dictionary<string, List<RightsIssueRow>> LoadRights()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        return SqliteRightsIssueUpsert.LoadAll(conn);
    }

    /// <summary>
    /// 收益率自检那一句话。**两档分开说，别把浮点误差报成警告**（2026-09-10 改）——
    /// 原来不管哪档都喊「⚠ 收益率对不上真实值（算法可能被改坏了）」，而实测每轮都有几百天落在
    /// 0.1% 以内的浮点档（4020 只票、一千多万个交易日里 743 天，占 0.006%）。那句警告每轮都响，
    /// 真出问题时反而没人当回事；而真问题那一侧（factor 没动却差很多）老判据压根没查。
    /// 全过程见 doc/bar-value-audit-design.md §15。
    /// </summary>
    private string ReturnCheckText()
    {
        if (_checkedDays == 0) return "";
        var parts = new List<string>();
        if (_realErrors > 0)
            parts.Add($"⚠ 有 {_realErrors} 天没有除权、收益率却差出 {AdjustFactorCalculator.MinorLimit:P1} 以上"
                    + "（算法可能被改坏了）");
        if (_minorDrift > 0)
            parts.Add($"{_minorDrift} 天有 {AdjustFactorCalculator.MinorLimit:P1} 以内的浮点级偏差"
                    + $"（共比 {_checkedDays:N0} 天，占 {(double)_minorDrift / _checkedDays:P4}，"
                    + "属因子累乘的正常误差）");
        return parts.Count == 0
            ? $"，收益率自检全部通过（共比 {_checkedDays:N0} 天）"
            : "，" + string.Join("；", parts);
    }

    /// <summary>把 <see cref="ProgressThrottle"/> 接到框架的 <c>Report</c> 上。
    /// 不用 <c>Progress&lt;string&gt;</c>：那个是异步 post 的，几十分钟的循环里顺序会乱。</summary>
    private sealed class ReportSink(Action<string, int?, int?, string?> report) : IProgress<string>
    {
        public void Report(string value) => report(value, null, null, null);
    }
}
