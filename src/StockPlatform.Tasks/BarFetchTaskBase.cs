using System.Collections.Concurrent;
using System.Diagnostics;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// K线类任务的共同骨架（2026-09-21，见 doc/bar-tasks-migration-design.md）——
/// 个股三口径、指数、ETF、退市股收尾六项都长在它上面。
///
/// ════ 它做什么、不做什么 ════
/// 做的是**每项都一样**的那几件：抓一只、按判据落库、数四个计数、维护失败名单、
/// 按批报进度和心跳。判据本身一条都不在这儿——写入决策在
/// <see cref="BarWritePlanner"/>、水位线在 <see cref="IncrementalWindowCalculator"/>、
/// 失败名单在 <see cref="FailedTodoRule"/>，全在 Logic 层（分层原则见
/// doc/solution-class-map.md §0.1）。这里只是"串流程"。
///
/// ════ 一批＝一组标的 ════
/// 骨架的 <see cref="TaskRunArgs.MaxItems"/>（本轮跑几批）和 <see cref="TaskRunArgs.Deadline"/>
/// （空窗到点收尾）都落在**批边界**上。迁移前是 `Task.WhenAll` 把 5500 只一次性丢给限流器，
/// 中途停＝这一轮白跑一段、Deadline 完全不起作用。
///
/// 批内并发交给限流器兜着（腾讯 3 并发 / 1 秒间隔）——批大小不是并发度，是**收尾粒度**。
/// </summary>
public abstract partial class BarFetchTaskBase(FetchPaths paths, BarSourceHolder sourceHolder)
    : FetchTaskBase<BarFetchTaskBase.CodeBars>
{
    /// <summary>一只标的抓回来的结果。失败的也是一条——失败名单要按"这轮碰过"来算。</summary>
    /// <param name="Code">标的代码（指数/ETF 是带前缀的 8 位符号）。</param>
    /// <param name="Granularity">哪个口径。一只票在同一批里可能有多条（退市股收尾要补三个口径）。</param>
    /// <param name="Bars">抓回来的行。</param>
    /// <param name="DriftCheck">要不要做复权基准漂移比对（只有前复权那一路开）。</param>
    /// <param name="Overwrite">
    /// 「覆盖重写」——整段以数据源当前基准为准，不比对、不看库里已有什么
    /// （<see cref="BarWritePlanner.Plan"/> 的 overwrite 那一路）。
    ///
    /// 只有【重取前复权】用它：那一项的全部意义就是抹掉分批入库留下的复权基准接缝，
    /// 走默认那条路的话已有的行会被判成"值也对得上"而原样跳过，等于白抓一轮。
    /// 另外五个口径一律留默认 false——它们的基准不随分红变，覆盖只会把确认过的行重新写一遍。
    ///
    /// ⚠ 跟 <paramref name="DriftCheck"/> 互斥：覆盖本来就是"全都以新基准为准"，
    /// 再比对一遍毫无意义，判据那边也是先看 overwrite 就直接返回。
    /// </param>
    /// <param name="RequestEnd">
    /// 这次请求的终点。空响应要落成水位时用得上（见 <see cref="RecordProbeFloors"/>）——
    /// "探到空"只证明"这个终点及其之前那段没有"，脱离终点这个结论就没意义了。
    /// </param>
    public sealed record CodeBars(string Code, string Granularity, List<Bar> Bars,
                                  bool DriftCheck = false, bool Overwrite = false,
                                  DateTime RequestEnd = default);

    /// <summary>一批几只。批边界＝可以干净收尾的点，不是并发度。</summary>
    public const int DefaultBatchSize = 30;

    /// <summary>日志每多少批写一行（心跳不受它影响，每批都有）。</summary>
    protected const int LogEveryBatches = 10;

    protected FetchPaths Paths => paths;

    /// <summary>这一轮用哪个源。**每轮开跑时读一次**——配置重载后它会变，见 <see cref="BarSourceHolder"/>。</summary>
    protected NamedBarSource Source => sourceHolder.Current;

    /// <summary>这个任务的失败名单记在哪个 taskId 下（<see cref="RetryTaskIds"/> 里的常量）。</summary>
    protected abstract string TaskId { get; }

    /// <summary>本地库。**读**随便用；写一律在 <see cref="SqliteWriteGate.Local"/> 里。</summary>
    protected SqliteBarRepository Bars => _bars ??= new SqliteBarRepository(paths.CurrentDb);
    private SqliteBarRepository? _bars;

    // ── 本轮的账 ──────────────────────────────────────────────
    protected readonly Stopwatch Sw = new();
    protected readonly List<string> Errors = [];
    private readonly ConcurrentBag<string> _attempted = [];
    private readonly ConcurrentBag<string> _failed = [];
    private readonly ConcurrentBag<string> _drifted = [];
    private int _skipped, _withNewData, _empty, _rows;

    protected int FailedCount => _failed.Count;

    /// <summary>本轮失败的标的（去重）。【退市股收尾】要用它决定给谁打"已尝试过"的标记。</summary>
    protected IReadOnlySet<string> FailedCodes => _failed.ToHashSet(StringComparer.Ordinal);
    protected int RowsWritten => _rows;

    /// <summary>本地已是最新、一个请求都没发的记一笔（跟老路的 FetchStats.Skip 同义）。</summary>
    protected void CountSkipped(int n = 1) => Interlocked.Add(ref _skipped, n);

    /// <summary>
    /// 「这一轮碰过它，只是不用发请求」——**补失败名单时必须记**（2026-09-21 实机验证时发现）。
    ///
    /// 失败名单的规矩是"这轮碰过且没失败就移出"（<see cref="FailedTodoRule"/>）。
    /// 一只票上次失败、这轮已经被别的路补到最新，于是窗口是空的、不发请求——
    /// 要是不记一笔"碰过"，它就**永远留在失败名单里**（老编排层是把整份名单都算作碰过的，
    /// 所以没这个问题）。
    /// </summary>
    protected void MarkAttempted(string code) => _attempted.Add(code);

    /// <summary>"本项汇总"那一行。措辞跟老路一字不差，好让迁移前后的日志能直接对着看。</summary>
    protected string Summarize() =>
        $"跳过 {Volatile.Read(ref _skipped)} 只（本地已是最新，未发起请求）、"
        + $"抓到新数据 {Volatile.Read(ref _withNewData)} 只、"
        + $"请求成功但无新数据 {Volatile.Read(ref _empty)} 只（比如请求的日期不是交易日）、"
        + $"失败 {_failed.Count} 只";

    // ── 抓 ────────────────────────────────────────────────────

    /// <summary>
    /// 抓一只。**不抛**：单只失败进失败名单、下轮再来，不拖垮整批
    /// （取消除外，那个要一路抛到骨架去）。
    /// </summary>
    protected async Task<CodeBars?> FetchOneAsync(
        string code, string granularity, DateTime start, DateTime end,
        bool driftCheck, CancellationToken ct, bool overwrite = false)
    {
        ct.ThrowIfCancellationRequested();
        _attempted.Add(code);

        // 确实要发请求了，才把窗口向前放宽以取得比对样本——数据源一页固定返回 640 根，
        // 放宽不多花请求（见 BarWritePlanner.DriftCheckLookbackDays）。
        var fetchStart = driftCheck
            ? new[] { start, end.AddDays(-BarWritePlanner.DriftCheckLookbackDays) }.Min()
            : start;

        try
        {
            var (_, bars) = await Source.Fetcher.FetchAsync(code, granularity, fetchStart, end, ct);
            if (bars.Count > 0) Interlocked.Increment(ref _withNewData);
            else Interlocked.Increment(ref _empty);
            return new CodeBars(code, granularity, bars, driftCheck, overwrite, end);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _failed.Add(code);
            lock (Errors) if (Errors.Count < 50) Errors.Add($"{code}: [{Source.Name}] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 抓一批（组内并发，真正的闸是数据源自己的限流器）。
    /// </summary>
    /// <param name="overwrite">
    /// 整批都走「覆盖重写」（见 <see cref="CodeBars.Overwrite"/>）。作用于整批而不是每项，
    /// 是因为这件事按**任务**固定：一个任务的一路要么全覆盖要么全不覆盖，
    /// 放进元组只会让另外五个子类跟着改签名。
    /// </param>
    protected async Task<List<CodeBars>> FetchBatchAsync(
        IEnumerable<(string Code, string Granularity, DateTime Start, DateTime End, bool DriftCheck)> batch,
        CancellationToken ct, bool overwrite = false)
    {
        var got = await Task.WhenAll(batch.Select(t =>
            FetchOneAsync(t.Code, t.Granularity, t.Start, t.End, t.DriftCheck, ct, overwrite)));
        return got.Where(x => x != null).Select(x => x!).ToList();
    }

    // ── 存 ────────────────────────────────────────────────────

    /// <summary>
    /// 落一批：读比对样本 → <see cref="BarWritePlanner"/> 判 → 按结论写。
    /// 跟老编排层走的是**同一份判据**，所以两条路不会分叉。
    ///
    /// 整段在 <see cref="SqliteWriteGate.Local"/> 里：这是个"读一段→算→写回去"的组合，
    /// 只给写加锁挡不住中间那道缝。推线程池是因为它整段是同步 IO（骨架不替子类推）。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<CodeBars> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            var today = DateTime.Today;
            RecordProbeFloors(batch);
            foreach (var item in batch)
            {
                if (item.Bars.Count == 0) continue;
                ct.ThrowIfCancellationRequested();
                lock (SqliteWriteGate.Local)
                {
                    // 覆盖那一路不需要比对样本，也别去查（那是一次多余的区间查询）。
                    var plan = BarWritePlanner.Plan(
                        item.Bars, today,
                        item.Overwrite ? EmptyCloses : StoredCloses(item),
                        overwrite: item.Overwrite, driftCheck: item.DriftCheck);

                    if (plan.ToInsert.Count > 0) Bars.InsertOrRefreshUnconfirmed(plan.ToInsert);
                    if (plan.ToOverwrite.Count > 0) SqliteBarUpsert.Upsert(paths.CurrentDb, plan.ToOverwrite);
                    if (plan.Drifted) _drifted.Add(item.Code);
                    if (plan.Today.Count > 0) SqliteBarUpsert.Upsert(paths.CurrentDb, plan.Today);

                    Interlocked.Add(ref _rows,
                        plan.ToInsert.Count + plan.ToOverwrite.Count + plan.Today.Count);
                }
            }
        }, ct);

    /// <summary>
    /// 把这一批里"请求成功、但返回 0 行"的结论落成永久水位（<c>BarProbeFloor</c>，2026-09-22 重建）。
    ///
    /// ════ 为什么要有 ════
    /// 没有它，"那些年还没上市"的票每轮都要重新试一遍——2026-09-07 实测一万六千个请求、
    /// 四个半小时、写入为零。这张表原来由老编排层的 <c>RecordProbeFloors</c> 写，
    /// 那条路 2026-09-22 随【拉取区间数据】整段删掉之后，表就只读不写了。
    ///
    /// ════ 为什么落在每批、而不是收尾 ════
    /// 老实现是**每个阶段跑完**才写，中途停就整段丢——下一轮这些票重新发一遍空请求。
    /// 这次跟着批走，停在哪里前面的结论都算数。
    ///
    /// ════ 安全前提在判据里，不在这儿 ════
    /// 记高了会让这只票的历史**永久跳过**而且不报错，所以两道前提
    /// （本地得有这只票的K线、请求终点必须早于本地最早一根）都在
    /// <see cref="ProbeFloorPlanner"/> 里，有单测钉着。这里只负责喂它正确的输入：
    /// ⚠ 每只**现查一次自己的最早日**，不用整轮缓存的快照——这一轮前面的批可能刚补进更早的历史，
    /// 拿旧快照会把水位记高。单只查询走主键索引，便宜。
    /// </summary>
    private void RecordProbeFloors(IReadOnlyList<CodeBars> batch)
    {
        var empties = batch.Where(b => b.Bars.Count == 0 && b.RequestEnd != default).ToList();
        if (empties.Count == 0) return;

        var floorRepo = new SqliteBarProbeFloorRepository(paths.CurrentDb);
        foreach (var byGran in empties.GroupBy(b => b.Granularity, StringComparer.Ordinal))
        {
            var earliest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var code in byGran.Select(b => b.Code).Distinct(StringComparer.Ordinal))
                if (Bars.GetEarliestPeriodStart(code, byGran.Key) is { } e) earliest[code] = e;

            var floors = ProbeFloorPlanner.Plan(
                byGran.Select(b => (b.Code, b.RequestEnd)), earliest);
            if (floors.Count > 0)
                lock (SqliteWriteGate.Local) floorRepo.Record(floors, byGran.Key);
        }
    }

    private static readonly Dictionary<DateTime, double> EmptyCloses = [];

    /// <summary>抓回来这一段里、除今天以外那几天在库里已有的收盘价（比对样本）。</summary>
    private IReadOnlyDictionary<DateTime, double> StoredCloses(CodeBars item)
    {
        var today = DateTime.Today;
        DateTime? from = null, to = null;
        foreach (var b in item.Bars)
        {
            if (b.PeriodStart.Date == today) continue;
            if (from is null || b.PeriodStart < from) from = b.PeriodStart;
            if (to is null || b.PeriodStart > to) to = b.PeriodStart;
        }
        if (from is null) return new Dictionary<DateTime, double>();

        var stored = new Dictionary<DateTime, double>();
        foreach (var b in Bars.Query(item.Code, item.Granularity, from.Value, to!.Value))
            stored[b.PeriodStart.Date] = b.Close;
        return stored;
    }

    // ── 水位线 ────────────────────────────────────────────────

    /// <summary>这只标的按水位线该从哪天抓起（<see cref="IncrementalWindowCalculator"/> 的规则）。</summary>
    protected DateTime IncrementalStart(string code, string granularity, DateTime end, int lookbackYears)
    {
        lock (SqliteWriteGate.Local)
            return IncrementalWindowCalculator.IncrementalStart(
                Bars.GetLatestBarInfo(code, granularity), end, lookbackYears);
    }

    /// <summary>
    /// 整段回补的窗口，按调用方给的年份区间**收窄**（2026-09-22，判据见
    /// <see cref="BackfillWindowRule"/>）。
    ///
    /// 「整段」有多长本来由各任务自己定（指数是 A股开市首日、不复权是前复权的最早那天），
    /// 这里只把它收小到 <see cref="TaskRunArgs.YearStart"/>~<see cref="TaskRunArgs.YearEnd"/>，
    /// **永不放宽**——否则数据源上根本没有的那些年会变成白发的空请求。
    /// 两个年份都没填就原样返回，所以这个调用可以无条件加在整段回补那一路上。
    /// </summary>
    protected static (DateTime Start, DateTime End) NarrowToYears(
        DateTime start, DateTime end, TaskRunArgs args)
        => BackfillWindowRule.Narrow(start, end, args.YearStart, args.YearEnd, DateTime.Today);

    /// <summary>
    /// 整段回补时**逐只算缺口**（2026-09-22）。判据是 <see cref="YearGapCalculator.For"/>，
    /// 老编排层那条路用的是同一个——所以两边不会分叉。
    ///
    /// 它比"所有票同一个窗口"多做三件事，每件都省掉大量必然落空的请求：
    ///   ① **已探明的水位**（<c>BarProbeFloor</c>）：上一轮真发过请求、成功、返回 0 行的那段不再重试。
    ///      没有它的话"那些年还没上市"的票每轮都要重新试一遍——2026-09-07 实测一万六千个请求、
    ///      四个半小时、写入为零。
    ///   ② **本地已经覆盖到的**整只跳过，只补缺的那一头。
    ///   ③ **缺口里一个交易日都没有**就跳过（区间起点写 01-01、而首个交易日是 01-04 那种）。
    ///      2026-07-30 实测：不判这一下，一次区间重跑光在这上面烧掉 19 分钟、1150 个请求。
    ///
    /// <paramref name="ignoreFloor"/>＝「覆盖重抓」那一路：它的语义是"不看本地已有什么、整段重写"，
    /// 所以连水位也不看——否则抹接缝的活会被"这段已经探明没有"给跳过。
    /// </summary>
    protected List<(string Code, DateTime Start, DateTime End)> PlanGaps(
        IReadOnlyList<string> codes, string granularity,
        DateTime windowStart, DateTime windowEnd, bool ignoreFloor, out int skipped)
        => PlanGaps(codes, granularity, _ => (windowStart, windowEnd), ignoreFloor, out skipped);

    /// <summary>
    /// 同上，但**每只的窗口不一样**（不复权那路是"它自己的前复权最早那天 ~ 今天"）。
    ///
    /// ⚠ 顺序要紧：**先把窗口定下来（含年份收窄），再在这个窗口里判缺口**。
    /// 反过来做——先在一个大窗口里算缺口、再收窄——算出来的缺口是"区间之外的那部分"，
    /// 收窄之后仍然非空，于是每轮都重新计划、抓回来一行都写不进去。
    /// 2026-09-22 实测踩过：填 2024 重跑，5995 只全部重抓、写入 0 行。
    /// </summary>
    protected List<(string Code, DateTime Start, DateTime End)> PlanGaps(
        IReadOnlyList<string> codes, string granularity,
        Func<string, (DateTime Start, DateTime End)> windowOf, bool ignoreFloor, out int skipped)
    {
        var earliest = Bars.GetEarliestPeriodStartByCode(granularity);
        var floor = ignoreFloor
            ? null
            : new SqliteBarProbeFloorRepository(Paths.CurrentDb).GetAll(granularity);
        var calendar = LocalTradingCalendar();

        var plan = new List<(string, DateTime, DateTime)>(codes.Count);
        foreach (var code in codes)
        {
            var (ws, we) = windowOf(code);
            if (ws.Date > we.Date) continue;              // 这只在指定区间里没有窗口
            var (s, e) = ignoreFloor
                ? (ws, we)
                : YearGapCalculator.For(code, earliest, ws, we, calendar, floor);
            if (s.Date <= e.Date) plan.Add((code, s, e));
        }
        skipped = codes.Count - plan.Count;
        return plan;
    }

    /// <summary>
    /// 本地已知的交易日历，取不到就返回 null（判据会退回"一律放行去抓"）。
    ///
    /// ⚠ 用**不复权**的日期全集当日历、上证指数只是兜底：日历自己缺哪段就会瞎哪段，
    /// 2026-09-06 就是拿只有 2016 年之后的上证指数 day 去断言 1990~2015 没开过市，
    /// 把 2360 只最该补历史的老股静默跳过了（见 <see cref="TradingCalendar"/>）。
    /// </summary>
    private TradingCalendar? LocalTradingCalendar()
    {
        try
        {
            var days = Bars.GetDistinctPeriodStarts(Granularity.DayRaw);
            if (days.Count == 0)
                days = Bars.Query(MarketIndexCatalog.All[0].Symbol, Granularity.Day)
                           .Select(b => b.PeriodStart.Date).ToList();
            return days.Count > 0 ? new TradingCalendar(days) : null;
        }
        catch { return null; }
    }

    /// <summary>这只标的本地最早那根在哪天（整段回补跑完报覆盖范围用）。</summary>
    protected DateTime? EarliestLocal(string code, string granularity)
    {
        lock (SqliteWriteGate.Local) return Bars.GetEarliestPeriodStart(code, granularity);
    }

    // ── 收尾 ──────────────────────────────────────────────────

    /// <summary>
    /// 把失败名单写回 manifest。规则是"只动本轮碰过的"——见 <see cref="FailedTodoRule"/>。
    /// 正常跑完和被停止**都要写**：停止时已经抓过的那些票不该继续挂在名单上。
    /// </summary>
    protected void SaveFailedTodo(IManifestStore manifestStore)
    {
        var attempted = _attempted.Distinct(StringComparer.Ordinal).ToList();
        if (attempted.Count == 0) return;
        lock (SqliteWriteGate.Local)
        {
            var manifest = manifestStore.Load();
            FailedTodoRule.SetFailed(manifest, TaskId, attempted,
                                     _failed.Distinct(StringComparer.Ordinal).ToList());
            manifestStore.Save(manifest);
        }
    }

    /// <summary>本轮发现基准漂移的票（前复权那一路才会有）。</summary>
    protected IReadOnlyList<string> DriftedCodes => _drifted.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>碰过的票全失败 → 整项判失败（多半是被限流或断网）。只要有一只成了就算完成。</summary>
    protected bool AllAttemptedFailed =>
        !_attempted.IsEmpty && _failed.Distinct(StringComparer.Ordinal).Count()
                               == _attempted.Distinct(StringComparer.Ordinal).Count();


    // ── 名册与漂移名单 ────────────────────────────────────────

    /// <summary>
    /// 本地已知的**个股**代码（不含指数/ETF/板块/退市股）。
    /// 空库时抛异常——那说明名册还没跑过，逐只抓的项这一轮根本没有可抓的标的。
    /// </summary>
    protected List<string> LocalStockCodes()
    {
        var codes = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(s => s.Code).ToList();
        if (codes.Count == 0)
            throw new InvalidOperationException(
                "本地还没有股票名册，这一步没有可抓的标的——请先跑一次【股票名册与流通市值】");
        return codes;
    }

    /// <summary>
    /// 把本轮发现的复权基准漂移记进【重取前复权】的待办名单。
    ///
    /// 这里只**记名单**、不当场重抓：漂移在抓取时已经就地覆盖了手上那一页，
    /// 更早的历史交给计划里的【重取前复权】在空闲时慢慢补。哪些真需要重取的判据在
    /// <see cref="QfqRepairPlanner"/>（短历史的已经整段修完了，排进去纯属白跑）。
    /// </summary>
    protected void RecordDriftedForRepair(IManifestStore manifestStore)
    {
        var drifted = DriftedCodes;
        if (drifted.Count == 0) return;

        var earliestByCode = Bars.GetEarliestPeriodStartByCode(Granularity.Day);
        var targets = QfqRepairPlanner.SelectForRepair(drifted, earliestByCode, DateTime.Today);
        if (targets.Count == 0)
        {
            Report($"复权基准：{drifted.Count} 只在抓取时已就地覆盖完毕，无需重取更早历史。");
            return;
        }

        int pending;
        lock (SqliteWriteGate.Local)
        {
            var manifest = manifestStore.Load();
            var set = new HashSet<string>(manifest.PendingQfqRepairCodes, StringComparer.Ordinal);
            foreach (var c in targets) set.Add(c);
            manifest.PendingQfqRepairCodes = set.OrderBy(c => c, StringComparer.Ordinal).ToList();
            pending = manifest.PendingQfqRepairCodes.Count;
            manifestStore.Save(manifest);
        }
        Report($"复权基准：{targets.Count} 只股票除权了，更早的历史要按新基准重取，已记入待重取名单"
             + $"（共 {pending} 只）——计划里的【重取前复权】会在空闲时慢慢补，也可以手动点它的【执行】。");
    }

    /// <summary>每轮开跑先清账（任务实例虽然一轮一个，但别指望这一点）。</summary>
    protected void ResetRun()
    {
        Errors.Clear();
        _attempted.Clear();
        _failed.Clear();
        _drifted.Clear();
        _skipped = _withNewData = _empty = _rows = 0;
        Sw.Restart();
    }

    /// <summary>
    /// 订阅数据源的状态播报并转成日志（限流退避、模拟源那句"未发任何请求"都走这条）。
    /// 用 <c>using</c> 保证退订——fetcher 实例是注册时建的、跨轮复用，漏退订就是重复日志加内存泄漏。
    /// </summary>
    protected IDisposable ForwardSourceStatus()
    {
        var fetcher = Source.Fetcher;
        void Forward(string m) => Report(m);
        fetcher.OnStatus += Forward;
        return new Unsubscriber(() => fetcher.OnStatus -= Forward);
    }

    private sealed class Unsubscriber(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>
    /// 报一步进展：**每批都喂看门狗**，日志每 <see cref="LogEveryBatches"/> 批才写一行。
    /// 不这么分开的话只有两个坏选择——日志刷屏，或者被 5 分钟静默上限误判成卡死
    /// （【资金净流入】2026-09-18/19 各被掐过一次）。
    /// </summary>
    protected void ReportBatch(int batchIndex, string text, int done, int total)
    {
        if (batchIndex % LogEveryBatches == 0 || done >= total) Report(text, done, total);
        else ReportQuiet(text, done, total);
    }
}
