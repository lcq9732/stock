using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【全库数据体检】——2026-09-09 从 <c>FetchOrchestrator.RunStepFullAuditAsync</c> 迁过来。
///
/// ════ 为什么破了"老任务不迁"那条规矩 ════
/// 两条同时成立（判据见 doc/full-audit-task-migration-design.md §0）：它正要长大（要加 6 条值体检
/// 判据），而且它恰好卡在框架能给的两件事上——**流式落账**（原来是扫完一次性 Save，三遍扫描
/// 半小时，停在 29 分钟等于全白跑）和 **MaxItems/Deadline**（半小时的重活最该能空闲窗口分段跑）。
///
/// ════ 批的粒度 = 一个"面" ════
/// 面＝标的类型 × 口径。每扫完一个面 yield 一次，落账时**只替换这个面的旧记录**、别的面原样保留。
/// 不按"每 500 只票"落账是因为 <see cref="Manifest.MissingBars"/> 的语义是"这个面当前的完整缺口
/// 清单"：按 500 只落账的话，中途停下来时这个面的名单只剩跑过的那部分，其余票被当成"没有缺口"
/// ——比全丢更糟（安静的错）。
///
/// ════ 停牌怎么办 ════
/// 停牌那几天在数据上跟漏抓一模一样（日历里有、这只票没有），查是分不开的。所以体检只负责**报**，
/// 补不到的由【重新拉取失败】在补过两轮之后写进白名单、往后跳过。要推翻这些结论就用
/// <see cref="FetchMode.Thorough"/>（原界面上的「彻底体检」勾）。
/// </summary>
public sealed class FullAuditTask : FetchTaskBase<AuditFinding>
{
    public override FetchActionId Id => FetchActionId.StepFullAudit;

    /// <summary>一次查多少只票的空洞。太大一次 join 上千万行；太小则来回开连接。</summary>
    private const int BatchSize = 500;

    /// <summary>一个交易日过去多久才算"数据源确实该有了"。T+1 发布 + 盘后逐步更新，留 2 天很宽松。</summary>
    private const int SettleDays = 2;

    /// <summary>尾巴落后超过这么多个交易日的不算"漏抓"：长期停牌的在市股票一停就是几个月。</summary>
    private const int TailSuspectLimit = 10;

    /// <summary>日期列表最多列几个，超了截断。</summary>
    private const int MaxListedDays = 8;

    /// <summary>面标识里"类型"和"口径"的分隔符。</summary>
    private const string ScopeSep = "|";

    /// <summary>
    /// 体检要扫的"标的类型 × 口径"矩阵里**能联网补**的那几个面。
    ///
    /// 为什么必须扫这么多面：2026-09-02 把【拉取全部】拆成独立任务之后，后复权、不复权、ETF、
    /// 指数各自成了一项——独立就意味着可以被漏排、可以单独失败，只体检前复权的话没人会发现。
    /// 回测吃的 day_adj 由 day_raw 推出来，不复权缺一天回测就错一天。
    ///
    /// 哪些面不在这里：板块指数（本地合成）、day_adj（本地重算）——缺了要重新合成/重算、不是去抓，
    /// 归 <see cref="LocalOnlyScopes"/> 只报数；退市股不体检（数据源不再更新，报了也补不到，
    /// 只会补满两轮堆进白名单变成噪声，缺的尾巴由【退市股收尾】负责）。
    /// </summary>
    private static readonly (string Type, string Gran, string Label)[] FetchableScopes =
    [
        (SqliteStockMetaUpsert.TypeStock, Granularity.Day,    "个股·前复权"),
        (SqliteStockMetaUpsert.TypeStock, Granularity.DayHfq, "个股·后复权"),
        (SqliteStockMetaUpsert.TypeStock, Granularity.DayRaw, "个股·不复权"),
        (SqliteStockMetaUpsert.TypeEtf,   Granularity.Day,    "ETF"),
        (SqliteStockMetaUpsert.TypeIndex, Granularity.Day,    "指数"),
    ];

    private readonly string _dbPath;
    private readonly IManifestStore _manifestStore;
    private readonly IDailyFetchNoDataRepository? _dailyNoData;

    private readonly SqliteBarRepository _bars;
    private readonly SqliteMissingBarRepository _audit;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <summary>汇总行，按产生顺序攒着，<see cref="OnCompletedAsync"/> 一次性输出。</summary>
    private readonly List<string> _summary = [];

    /// <summary>面 → 这个面的全部代码。落账时用它界定"要替换哪些旧记录"（见类注释）。</summary>
    private readonly Dictionary<string, List<string>> _scopeCodes = new(StringComparer.Ordinal);

    private int _totalRanges, _totalDays;

    public FullAuditTask(
        string dbPath, IManifestStore manifestStore, IDailyFetchNoDataRepository? dailyNoData = null)
    {
        _dbPath = dbPath;
        _manifestStore = manifestStore;
        _dailyNoData = dailyNoData;
        _bars = new SqliteBarRepository(dbPath);
        _audit = new SqliteMissingBarRepository(dbPath);
    }

    protected override async IAsyncEnumerable<IReadOnlyList<AuditFinding>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        bool thorough = args.Mode == FetchMode.Thorough;
        if (thorough) await Task.Run(ClearConfirmations, ct);

        var instruments = await Task.Run(() => SqliteStockMetaUpsert.GetAllInstruments(_dbPath), ct);
        if (instruments.Count == 0)
        {
            Report("本地还没有标的名册，没什么可体检的。");
            yield break;
        }

        var byType = instruments
            .GroupBy(i => i.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Code).ToList(), StringComparer.Ordinal);
        List<string> CodesOf(string type) => byType.TryGetValue(type, out var l) ? l : [];

        var cutoff = DateTime.Today.AddDays(-SettleDays);

        Report($"开始全库体检：{FetchableScopes.Length} 个可联网补的面（"
             + string.Join("、", FetchableScopes.Select(x => x.Label))
             + $"），逐只对照交易日历找日线空洞（{cutoff:yyyy-MM-dd} 之后的日子不算，"
             + "数据源可能还没更新完）…");

        foreach (var (type, gran, label) in FetchableScopes)
        {
            ct.ThrowIfCancellationRequested();
            var codes = CodesOf(type);
            if (codes.Count == 0)
            {
                _summary.Add($"　{label}：本地一只都没有，跳过");
                continue;
            }

            var scope = type + ScopeSep + gran;
            _scopeCodes[scope] = codes;
            yield return await Task.Run(
                () => ScanScope(scope, codes, gran, cutoff, thorough, label, ct), ct);
        }

        yield return await Task.Run(() => Notes(LocalOnlyScopes(CodesOf, cutoff, thorough, ct)), ct);
        yield return await Task.Run(() => Notes(CoverageShape(CodesOf, cutoff, ct)), ct);
        yield return await Task.Run(() => Notes(DailyTables(cutoff, thorough, ct)), ct);

        int delisted = CodesOf(SqliteStockMetaUpsert.TypeDelisted).Count;
        if (delisted > 0)
            _summary.Add($"　退市股 {delisted} 只：不体检（数据源不再更新，报了也补不到；"
                       + "缺的最后几天由【退市股收尾】负责）");
    }

    protected override Task SaveBatchAsync(IReadOnlyList<AuditFinding> batch, CancellationToken ct)
    {
        foreach (var f in batch.Where(f => f.Kind == AuditFindingKind.Note && f.Note != null))
            _summary.Add(f.Note!);

        var scope = batch.FirstOrDefault(f => f.Scope != null)?.Scope;
        if (scope != null)
            CommitScope(scope, batch.Where(f => f.Kind != AuditFindingKind.Note).ToList());

        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        Report($"全库体检完成（用时 {FormatElapsed(stats.Elapsed)}）：\n"
            + string.Join("\n", _summary) + "\n"
            + (_totalRanges == 0
                ? "　可联网补的面没有发现空洞。"
                : $"　合计 {_totalRanges} 段、{_totalDays} 个交易日的日线缺失，已记入待补名单。\n"
                  + "　下一步：跑一次【重新拉取失败】去补。补得到的自动划掉；"
                  + "连补两轮拿不到的会被判定为\"数据源确实没有\"（多半是停牌），写进白名单、以后体检不再报。\n"
                  + "　⚠ 第一次体检查出的量通常很大（十年下来的停牌天数都在里面），补一轮可能要几小时。"));

        return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(nothingToDo: _totalRanges == 0));
    }

    /// <summary>老 manifest 里的记录没有口径字段（2026-09-04 之前只体检前复权），一律按前复权算。</summary>
    private static string NormalizeGran(string? gran) =>
        string.IsNullOrEmpty(gran) ? Granularity.Day : gran;

    /// <summary>
    /// 把一个面的结果落进 manifest。**只动这个面**：旧记录里凡是"代码属于本面 且 口径等于本面口径"
    /// 的一律换成本轮结果，其余原样保留。
    ///
    /// ⚠ 两个容易写错的地方，写错都很安静：
    ///   ① 不能整体替换 MissingBars——同一个口径有三个面（个股/ETF/指数都用 day），
    ///      整体替换等于扫完个股就把 ETF 和指数的旧记录删了；
    ///   ② Tries 必须从旧记录继承——把补过两轮的计数清零，就永远收敛不到"数据源确实没有"。
    /// </summary>
    private void CommitScope(string scope, List<AuditFinding> gaps)
    {
        var gran = scope.Split(ScopeSep)[1];
        var codes = _scopeCodes.TryGetValue(scope, out var l)
            ? l.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var manifest = _manifestStore.Load();
        var triesByKey = new Dictionary<(string, string), int>();
        foreach (var m in manifest.MissingBars)
            triesByKey[(m.Code, NormalizeGran(m.Granularity))] = m.Tries;

        var kept = manifest.MissingBars
            .Where(m => !(codes.Contains(m.Code) && NormalizeGran(m.Granularity) == gran))
            .ToList();

        var fresh = gaps
            .Where(f => f.Code != null)
            .Select(f => new MissingBarRange
            {
                Code = f.Code!, Granularity = gran,
                From = f.From, To = f.To, Days = f.Days,
                Tries = triesByKey.GetValueOrDefault((f.Code!, gran)),
            })
            .ToList();

        manifest.MissingBars = kept.Concat(fresh)
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ThenBy(r => r.Granularity, StringComparer.Ordinal)
            .ToList();
        _manifestStore.Save(manifest);

        _totalRanges += fresh.Count;
        _totalDays += fresh.Sum(r => r.Days);
    }

    /// <summary>
    /// 扫一个面（一批标的 × 一个口径）。返回"空洞 + 一条汇总行"——**汇总行必须带上**，
    /// 否则一个空洞都没有的面会 yield 空批，而框架对空批不调 SaveBatchAsync，
    /// 那个面的旧记录就永远清不掉。
    /// </summary>
    private List<AuditFinding> ScanScope(
        string scope, List<string> codes, string gran, DateTime cutoff,
        bool thorough, string label, CancellationToken ct)
    {
        var found = new List<AuditFinding>();
        int scanned = 0, withGaps = 0, days = 0;

        // 「整只票一根都没有这个口径」FindGaps 是查不出来的——它只看每只票自己 [最早,最晚] 区间内的
        // 洞，一根都没有的票根本进不了那张区间表。这类问题跟"缺几天"完全是两回事（多半是那一项
        // 从来没排进计划、或者一直在失败），所以单独数出来提醒，**不进待补名单**：
        // 整段回补该走【拉取区间数据】，几千只×十年塞进逐段重试里跑不完。
        var have = _bars.GetLatestPeriodStartByCode(gran);
        int none = codes.Count(c => !have.ContainsKey(c));

        foreach (var batch in codes.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var gaps = _audit.FindGaps(batch, gran,
                MarketIndexCatalog.ShanghaiCompositeSymbol, ignoreConfirmed: thorough);

            foreach (var (code, gapDays) in gaps)
            {
                var settled = gapDays.Where(d => d.Date <= cutoff).ToList();
                if (settled.Count == 0) continue;
                // 空洞不连续时取包络：一次请求覆盖整段，比逐日请求划算得多
                found.Add(new AuditFinding(AuditFindingKind.Gap, scope, code,
                    settled[0], settled[^1], settled.Count));
                withGaps++;
                days += settled.Count;
            }

            scanned += batch.Length;
            // 进度数字交给 done/total 两个参数（框架会渲染成"（3/5）"），文本里别再写一遍——
            // 2026-09-09 实机验证时就是这样重复成了"…1/1，已发现…（1/1）"。
            Report($"体检 {label}：已发现 {withGaps} 只有空洞（已用时 {FormatElapsed(_sw.Elapsed)}）",
                scanned, codes.Count, label);
        }

        found.Add(new AuditFinding(AuditFindingKind.Note, scope, Note:
            $"　{label}：{codes.Count} 只，{withGaps} 只有空洞、共 {days} 个交易日"
            + (none > 0 ? $"；另有 {none} 只**一根都没有**（该口径从没抓过，要整段回补）" : "")));
        return found;
    }

    /// <summary>
    /// 那两个**补不靠网络**的面：板块指数是本地合成的、day_adj 是本地重算的。它们缺了不该去发请求
    /// ——把这种空洞塞进待补名单，只会让【重新拉取失败】对着本地合成出来的代码空抓两轮，
    /// 然后错误地判定"数据源确实没有"、写进白名单。所以只查、只报，并直接说该跑哪一项。
    /// </summary>
    private List<string> LocalOnlyScopes(
        Func<string, List<string>> codesOf, DateTime cutoff, bool thorough, CancellationToken ct)
    {
        var lines = new List<string>();

        var boards = codesOf(SqliteStockMetaUpsert.TypeBoard);
        if (boards.Count > 0)
        {
            Report($"体检 板块指数：{boards.Count} 个（本地合成，只报不补）…");
            int withGaps = 0, days = 0;
            foreach (var batch in boards.Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (_, gapDays) in _audit.FindGaps(batch, Granularity.Day,
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

        ct.ThrowIfCancellationRequested();
        Report("体检 回测序列(day_adj)：对比不复权的进度…");
        int pending = new SqliteAdjSeriesAuditor(_dbPath).PendingCount();
        var stocks = codesOf(SqliteStockMetaUpsert.TypeStock);
        int adjWithGaps = 0, adjDays = 0;
        foreach (var batch in stocks.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (_, gapDays) in _audit.FindGaps(batch, Granularity.DayAdj,
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

    /// <summary>
    /// **覆盖形状体检**——查 <c>FindGaps</c> 天生看不见的两种形状：起点比该有的晚一大截、
    /// 尾巴停在几天前。判据是纯函数，在 <see cref="CoverageShapeAuditor"/> 里单独测。
    ///
    /// 尾巴分两档：**全局**（某个口径所有票里最新那根都落后了 → 这一项最近根本没跑成，几乎零误报）；
    /// **个别票**（只有几只落后 → 多半是那几只当天没抓到，但长期停牌的也长这样，所以只数落后在
    /// <see cref="TailSuspectLimit"/> 个交易日以内的）。
    /// </summary>
    private List<string> CoverageShape(
        Func<string, List<string>> codesOf, DateTime cutoff, CancellationToken ct)
    {
        var lines = new List<string>();
        Report("体检 覆盖形状：起点/尾巴跟交易日历对照…");

        var calendar = _bars.Query(MarketIndexCatalog.ShanghaiCompositeSymbol, Granularity.Day)
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
                : earliestCache[gran] = _bars.GetEarliestPeriodStartByCode(gran);
        Dictionary<string, DateTime> Latest(string gran) =>
            latestCache.TryGetValue(gran, out var v) ? v
                : latestCache[gran] = _bars.GetLatestPeriodStartByCode(gran);

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
        foreach (var (type, gran, label) in FetchableScopes)
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
                continue;
            }

            var tails = CoverageShapeAuditor.FindLateTails(
                calendar, latest, Only(Earliest(gran), codes))
                .Where(g => g.TradingDays <= TailSuspectLimit)
                .ToList();
            if (tails.Count == 0) continue;

            var worst = tails.OrderByDescending(g => g.TradingDays).Take(3)
                .Select(g => $"{g.Code} 停在 {g.Actual:MM-dd}");
            lines.Add($"　{label}：{tails.Count} 只的最新一根停在 {TailSuspectLimit} 个交易日以内的过去"
                    + $"（{string.Join("、", worst)}…）——多半是临时停牌，长于这个的不计（那是长期停牌）");
        }

        return lines;
    }

    /// <summary>K线之外的日频表（资金流/融资余额/龙虎榜/东财三张）按交易日核对覆盖。</summary>
    private List<string> DailyTables(DateTime cutoff, bool thorough, CancellationToken ct)
    {
        var lines = new List<string>();
        var auditor = new SqliteDailyTableAuditor(_dbPath);

        foreach (var spec in SqliteDailyTableAuditor.DailyTables)
        {
            ct.ThrowIfCancellationRequested();
            Report($"体检 {spec.Label}：按交易日核对覆盖…");

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

            // 资金净流入的空日进待补名单，交给【重新拉取失败】一轮补掉
            var queued = spec.Table == "NetInflow" ? QueueMissingNetInflowDays(r.EmptyDays, thorough) : 0;

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
    /// <see cref="FetchMode.Thorough"/>：把三张"确认没有"的结论名单一起作废。
    /// **按名单分别报**——一勾就把它们一起废掉，代价得让人看得见：清掉多少条，下一轮回补就要
    /// 多发多少个请求。
    /// </summary>
    private void ClearConfirmations()
    {
        int had = _audit.ConfirmedCount();
        _audit.ClearConfirmed();
        Report($"彻底体检：已清空「确认没有」白名单（原有 {had} 条），全部重查。");

        // 区间回补的"数据源没有更早数据"水位是同一类结论（只是按段记、不是按天），同样作废：
        // 数据源当时抽风、后来补上了往年历史的话，只有这里能给它回头路。
        var floors = new SqliteBarProbeFloorRepository(_dbPath);
        int hadFloors = floors.Count();
        if (hadFloors > 0)
        {
            floors.Clear();
            Report($"彻底体检：已清空「数据源没有更早数据」水位（原有 {hadFloors} 条）——"
                 + "下一次【拉取区间数据】会重新探一遍那些票的往年历史。");
        }

        if (_dailyNoData != null)
        {
            foreach (var ds in new[] { IDailyFetchNoDataRepository.LhbDataset, IDailyFetchNoDataRepository.MarginDataset })
            {
                int n = _dailyNoData.Clear(ds);
                if (n > 0)
                    Report($"彻底体检：已清空【{ds}】的「确认没有数据」名单（{n} 天）——"
                         + $"下一次回补会重新试这 {n} 天，也就是多发 {n} 个请求。");
            }
        }
    }

    /// <summary>
    /// 把资金净流入的空日写进待补名单。已经在名单里的保留原有 Tries——跟K线空洞一个道理，
    /// 别把补过一轮的计数清零，否则永远收敛不到"数据源确实没有"。
    /// </summary>
    private int QueueMissingNetInflowDays(List<DateTime> emptyDays, bool thorough)
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

    /// <summary>只报数那些面的输出。空列表也要给一条，保证批非空——见 <see cref="ScanScope"/> 的注释。</summary>
    private static List<AuditFinding> Notes(List<string> lines) =>
        lines.Count == 0
            ? [new AuditFinding(AuditFindingKind.Note)]
            : lines.Select(l => new AuditFinding(AuditFindingKind.Note, Note: l)).ToList();

    /// <summary>只留这一批代码的那些项——GetEarliestPeriodStartByCode 是按口径查全库的，
    /// 里面混着 ETF、指数和板块指数的代码。</summary>
    private static Dictionary<string, DateTime> Only(
        Dictionary<string, DateTime> byCode, HashSet<string> codes) =>
        byCode.Where(kv => codes.Contains(kv.Key))
              .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>日期列表转成人读的一行，超过 <see cref="MaxListedDays"/> 个就截断。</summary>
    private static string FormatDays(List<DateTime> days) =>
        days.Count <= MaxListedDays
            ? string.Join("、", days.Select(d => d.ToString("MM-dd")))
            : string.Join("、", days.Take(MaxListedDays).Select(d => d.ToString("MM-dd")))
              + $"… 等 {days.Count} 天";

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
}
