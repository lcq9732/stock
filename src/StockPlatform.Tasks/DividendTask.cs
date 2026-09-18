using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取分红送配】（2026-09-18 从 <c>FetchOrchestrator.RunFetchDividendAsync</c> 迁过来）。
/// 见 doc/dividend-task-design.md。
///
/// ════ 为什么迁 ════
/// 老实现每轮都取全量名单（<c>stock</c> + <c>delisted</c>，约 5800 只）从头抓，没有任何
/// "已抓过就跳过"；失败名单写在 <c>await Task.WhenAll</c> **之后**，取消直接冒泡，走不到那一步。
/// 于是 2026-09-18 凌晨那轮撞上新浪限流、早上手工停止之后：抓到的数据在库里，
/// 但"抓到哪了"和"哪些失败了"一个字都没留下，重新执行＝从头再来一遍 5800 个请求。
///
/// ════ 一批 30 只 ════
/// 一只票一个请求（分红表 <c>sharebonus_1</c> 和配股表 <c>sharebonus_2</c> 在同一页，
/// 一次拿两份——分开抓等于白白多一倍请求）。限流器是 3 并发/1 秒，所以一批 30 只≈10 秒、
/// 全量 5800 只≈194 批≈32 分钟，跟老实现的吞吐一致：**并发没有变，只是把落库切成了批**。
/// 好处是骨架的三件事全部到位——停止只丢在途的那一批、<see cref="TaskRunArgs.MaxItems"/>
/// 按批截断、每批一条进度（远小于看门狗的 5 分钟）。
///
/// ⚠ 失败的那只也要进批（<see cref="DividendOutcome.Ok"/>＝false）：
/// <see cref="SaveBatchAsync"/> 要靠它记失败状态，扔掉就又回到"取消即失忆"。
///
/// ════ 水位线在 DividendFetchState，不在 Dividend 表 ════
/// 没分红的票一行都不写，"没抓过"和"抓过、确实没分红"在 Dividend 表里长得一模一样
/// （全市场约三千只是后者）。见 <see cref="DividendFetchState"/>。
///
/// ════ 限流：连续三批全军覆没就收工 ════
/// <see cref="RateLimiter"/> 管的是"这一个请求发不发得出去"；任务这一层还要判"今天还值不值得跑"。
/// 判定成立时返回 <see cref="TaskRunResult.Skipped"/> 而**不是** Failed——Skipped 的语义是
/// "这一轮根本没开工"，计划项的 <c>AlreadyRanOn</c> 不认它，限流过去之后今天还能再来；
/// 记成完成的话今天就不会再跑了。
/// </summary>
public sealed class DividendTask(
    FetchPaths paths,
    IDividendProvider provider,
    IDividendRepository repository,
    IManifestStore manifestStore,
    IDividendNoticeIndex? noticeIndex = null,
    int batchSize = DividendTask.DefaultBatchSize) : FetchTaskBase<DividendTask.DividendOutcome>
{
    /// <summary>一批多少只。30 只≈10 秒（3 并发/1 秒），停止最多丢这么多只的在途结果。</summary>
    public const int DefaultBatchSize = 30;

    /// <summary>本实例的批大小。可配只为了测试——测"中断之后接着跑"得让一轮里有好几批。</summary>
    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    /// <summary>
    /// 抓成功之后多久算旧、该重抓——这是**兜底**周期，不是主判据。
    ///
    /// 2026-09-18 从 25 天放宽到 90 天：有了公告索引（<see cref="IDividendNoticeIndex"/>）之后，
    /// "谁出了新方案"由索引回答，水位线只负责捞索引漏掉的那 1%。90 天＝那 1% 最长滞后多久。
    /// 没配索引时它退化成唯一判据，行为跟 25 天那版一样、只是轮换慢些（有兜底上限摊着）。
    /// 想强刷全市场用【整段回补】模式。
    /// </summary>
    private const int StaleDays = 90;

    /// <summary>
    /// 退市股单独一档。它们不在索引里（东财那张表退市股全空，2026-09-10 实测），
    /// 而分红是**静态历史、不会再变**——没必要跟在市股同频轮换。
    /// 不设成"抓过就永不再抓"，是留一条自愈的路：数据源后来补录了历史，一年内会捞回来。
    /// </summary>
    private const int DelistedStaleDays = 365;

    /// <summary>
    /// 一轮最多抓多少只——把兜底全量摊到每天，别再攒成一次 5902 个请求（限流下几小时）。
    /// 索引命中的排在前面，所以截断先截到兜底那批。
    /// </summary>
    private const int MaxCodesPerRun = 400;

    /// <summary>索引往回看几天。覆盖"预案→实施"的正常间隔（实测样例 29 天）并留一倍余量。</summary>
    private const int IndexLookbackDays = 45;

    /// <summary>连续这么多批**全部失败且沾限流**就收工。3 批＝90 只，够排除偶发波动。</summary>
    private const int DeadBatchesBeforeAbort = 3;

    /// <summary>一只票的抓取结果。失败的也是一条——状态要落库。</summary>
    public sealed record DividendOutcome(
        string Code, List<DividendRow> Dividends, List<RightsIssueRow> Rights,
        bool Ok, string? Error, bool RateLimited);

    public override FetchActionId Id => FetchActionId.FetchDividend;

    /// <summary>待办自己补（2026-09-18）：分红的待办只有"重抓这几只"一类，跟日常抓取是同一个动作。
    /// 见 doc/dividend-task-design.md §9。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>上一轮的状态，抓完按只更新后写回。</summary>
    private Dictionary<string, DividendFetchState> _states = new(StringComparer.Ordinal);

    /// <summary>本轮真的去抓过的（用来把"这次没失败"的移出失败名单）。</summary>
    private readonly List<string> _attempted = [];
    private readonly List<string> _failed = [];

    private int _planned, _done, _withData, _rows, _rights;
    private string? _abortReason;
    private string? _nothingToDoNote;

    protected override async IAsyncEnumerable<IReadOnlyList<DividendOutcome>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _attempted.Clear();
        _failed.Clear();
        _planned = _done = _withData = _rows = _rights = 0;
        _abortReason = _nothingToDoNote = null;

        // 读名单、读状态表都是同步 IO——骨架不替子类推线程池，在首个 await 之前干这些会冻住界面。
        var (codes, states) = await PlanAsync(args, ct);
        _states = states;
        _planned = codes.Count;

        if (codes.Count == 0)
        {
            _nothingToDoNote = args.Mode.HasFlag(FetchMode.FillBacklog)
                ? "分红送配没有欠着的失败股票"
                : $"分红送配都是 {StaleDays} 天以内抓的，本轮没有要抓的（想强刷全市场用【整段回补】）";
            Report(_nothingToDoNote + "。");
            yield break;
        }

        Report($"开始拉取分红送配（含配股、含退市股），本轮 {codes.Count} 只"
             + (args.Mode.HasFlag(FetchMode.FirstBackfill) ? "（整段回补：无视水位线全抓）" : "")
             + $"，一批 {_batchSize} 只、逐只抓、较慢...", 0, codes.Count);

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        int deadBatches = 0;
        try
        {
            for (int i = 0; i < codes.Count; i += _batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = codes.Skip(i).Take(_batchSize).ToList();
                var outcomes = await FetchChunkAsync(chunk, ct);

                _done += outcomes.Count;
                int failedHere = outcomes.Count(o => !o.Ok);
                _withData += outcomes.Count(o => o.Ok && o.Dividends.Count > 0);
                _rows += outcomes.Sum(o => o.Dividends.Count);
                _rights += outcomes.Sum(o => o.Rights.Count);
                Report($"分红送配 {_done}/{codes.Count}（有分红 {_withData} 只、共 {_rows:N0} 条、"
                     + $"配股 {_rights:N0} 条、失败 {_failed.Count + failedHere}）", _done, codes.Count);

                yield return outcomes;

                deadBatches = IsDeadBatch(outcomes) ? deadBatches + 1 : 0;
                if (deadBatches >= DeadBatchesBeforeAbort)
                {
                    _abortReason = $"新浪在限流：连续 {DeadBatchesBeforeAbort} 批（{DeadBatchesBeforeAbort * _batchSize} 只）"
                                 + $"全部失败，本轮停在 {_done}/{codes.Count}。已抓到的都在库里，"
                                 + "下次接着抓没抓过的那些";
                    yield break;
                }
            }
        }
        finally { provider.OnStatus -= Forward; }
    }

    /// <summary>
    /// 一批并发抓。并发度由 <see cref="RateLimiter"/> 控（3 并发/1 秒），这里只负责把一批拢齐。
    /// 单只失败不传染：记成 <c>Ok=false</c> 的一条，本批其余照常。
    /// </summary>
    private async Task<IReadOnlyList<DividendOutcome>> FetchChunkAsync(
        IReadOnlyList<string> chunk, CancellationToken ct)
    {
        var tasks = chunk.Select(async code =>
        {
            try
            {
                var (rows, rights) = await provider.GetAllWithRightsAsync(code, ct);
                return new DividendOutcome(code, rows, rights, true, null, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new DividendOutcome(code, [], [], false, ex.Message, ex is RateLimitedException);
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 本轮抓哪些、按什么顺序。<b>"先没抓过的、再最旧的"</b>——断点续跑因此天然成立：
    /// 上一轮抓到哪，下一轮自然从那里接着走，不需要额外的游标。
    /// </summary>
    private async Task<(List<string> Codes, Dictionary<string, DividendFetchState> States)> PlanAsync(
        TaskRunArgs args, CancellationToken ct)
    {
        // 查库是同步重活（名单 + 状态表），骨架不替子类推线程池，在首个 await 之前干这些会冻界面。
        var (all, delisted, states, backlog) = await Task.Run(() => LoadLocal(args), ct);

        // 【只补待办】：目标从待办清单来，不从水位线来（这是它跟增量的分界）。
        if (args.Mode.HasFlag(FetchMode.FillBacklog)) return (backlog, states);

        if (args.Mode.HasFlag(FetchMode.FirstBackfill))
            return (all.OrderBy(c => c, StringComparer.Ordinal).ToList(), states);

        // ── 问索引：最近谁出了新公告（含预案、进度更新）──
        // 拿不到就退回纯水位线。**绝不能**因为索引没拿到就"本轮没有要抓的"，那是静默漏抓。
        IReadOnlyDictionary<string, DateTime> hits = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (noticeIndex != null)
        {
            void Forward(string m) => Report(m);
            noticeIndex.OnStatus += Forward;
            try
            {
                Report($"先问一句最近 {IndexLookbackDays} 天谁出了分红公告（{noticeIndex.SourceName}）...");
                hits = await noticeIndex.GetRecentAsync(IndexLookbackDays, ct);
                Report($"公告索引：{hits.Count} 只在最近 {IndexLookbackDays} 天有分红公告或进度更新"
                     + "（其中上次抓取早于那条公告的才会真去抓）。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Report($"⚠ 公告索引取不到（{ex.Message}）——本轮退回按水位线抓，"
                     + "不会漏，只是会多发些请求。");
                hits = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            }
            noticeIndex.OnStatus -= Forward;
        }

        var due = SelectDue(all, states, DateTime.Now.AddDays(-StaleDays),
                            hits, delisted, DateTime.Now.AddDays(-DelistedStaleDays));

        if (due.Count > MaxCodesPerRun)
        {
            Report($"本轮到期 {due.Count} 只，按每轮上限先做 {MaxCodesPerRun} 只"
                 + "（索引命中的排在前面，剩下的下轮接着来）。");
            due = due.Take(MaxCodesPerRun).ToList();
        }
        return (due, states);
    }

    /// <summary>纯查库的那一半：名单、退市名单、水位线、待办。抽出来是为了整块推线程池。</summary>
    private (List<string> All, HashSet<string> Delisted,
             Dictionary<string, DividendFetchState> States, List<string> Backlog) LoadLocal(TaskRunArgs args)
    {
        repository.EnsureSchema();

        // 水位线播种（2026-09-18，一次性）——状态表是随这次迁移新建的，空表意味着全市场都算
        // "没抓过"，新版第一轮还是会全量重抓一遍。用 Dividend 表已有的 fetched_at 把水位线
        // 补起来，第一轮就只剩真没抓过的那几十只。是近似值（宁可多抓不会漏抓），
        // 两处不精确写在 IDividendRepository.SeedFetchStatesFromDividends 上。
        int seeded = repository.SeedFetchStatesFromDividends();
        if (seeded > 0)
            Report($"水位线首次播种：按库里 Dividend 的抓取时刻认领 {seeded:N0} 只"
                 + "（这批不是这轮抓的，是把\"以前抓过\"这个事实补进新表）。");

        var states = repository.GetFetchStates();

        var todo = manifestStore.Load().Todo(RetryTaskIds.Dividend, RetryTodoKind.Failed);
        var backlog = (todo?.Targets ?? []).Select(t => t.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal).ToList();

        // 含退市股：2026-09-06 起就是这样（GetAll 只取 type='stock'，319 只 delisted 从来没抓过，
        // 而回测要消除幸存者偏差，最需要的就是退市股的完整复权）。这条不能退回去。
        // 退市名单单独留一份：它们不在公告索引里，水位线也走更长的那一档。
        var delisted = SqliteStockMetaUpsert.GetByTypes(paths.CurrentDb, "delisted")
            .Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        var all = SqliteStockMetaUpsert.GetByTypes(paths.CurrentDb, "stock", "delisted")
            .Select(s => s.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (all, delisted, states, backlog);
    }

    /// <summary>
    /// 增量要抓哪些、按什么顺序（抽成静态是为了能单测：真跑一轮要发几千个请求）。
    ///
    /// 判据 = **索引命中** ∪ **到期**：
    ///   · 索引命中（<paramref name="hits"/>：代码 → 最新公告日）⇒ 抓，**无视到期判据**。
    ///     "命中"的定义是**上次抓取早于那条公告**，不是"在索引里"——后者会让同一只票在
    ///     45 天窗口里天天被重抓；
    ///   · 到期 ⇒ 兜底，捞索引漏掉的那 1%。在市股看 <paramref name="cutoff"/>，
    ///     退市股看 <paramref name="delistedCutoff"/>（更长，它们的分红是静态历史）。
    ///
    /// 顺序：**索引命中的排最前**，其余"先没抓过的、再最旧的"（没抓过的排序键是
    /// <see cref="DateTime.MinValue"/>），同一时刻按代码定序。这样被每轮上限或 Deadline 截断时，
    /// 先保住真有新数据的那些；顺序稳定，断点续跑才不会来回跳。
    /// </summary>
    public static List<string> SelectDue(
        IReadOnlyList<string> all,
        IReadOnlyDictionary<string, DividendFetchState> states,
        DateTime cutoff,
        IReadOnlyDictionary<string, DateTime>? hits = null,
        IReadOnlySet<string>? delisted = null,
        DateTime? delistedCutoff = null)
    {
        // 「命中」不等于"在索引里"，而是**上次抓取早于那条公告**——只看在不在索引里的话，
        // 45 天的固定窗口会让同一只票天天被重抓（稳态每天 900 多个请求）。
        //
        // ⚠ 用"公告日的次日零点"比，不是"公告日"：公告只有日期没有时刻，
        //   写成 notice > lastOk 的话，**当天出的公告永远抓不到**——今天抓过之后，
        //   明天再看仍是"公告日(今天) == 上次抓取日(今天)"，不大于，于是被永久跳过。
        //   取次日零点，代价只是出公告那天之后会多抓一次，换的是绝不漏。
        bool Hit(string c)
        {
            if (hits == null || !hits.TryGetValue(c, out var notice)) return false;
            if (!states.TryGetValue(c, out var st) || st.LastOkAt is not { } ok) return true;
            return ok < notice.Date.AddDays(1);
        }

        DateTime CutoffFor(string c) =>
            delisted?.Contains(c) == true && delistedCutoff is { } dc ? dc : cutoff;

        return all
            .Where(c => Hit(c) || !states.TryGetValue(c, out var st) || st.IsStale(CutoffFor(c)))
            .OrderBy(c => Hit(c) ? 0 : 1)
            .ThenBy(c => states.TryGetValue(c, out var st) && st.LastOkAt.HasValue
                             ? st.LastOkAt.Value : DateTime.MinValue)
            .ThenBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 这一批算不算"全军覆没"——熔断只数这种批。
    ///
    /// 判据是**整批都失败且其中沾限流**。整批失败但没有一条是限流（比如页面改版把解析全打挂了）
    /// 不算：那种该老老实实报失败，收工只会把问题藏起来。
    /// </summary>
    public static bool IsDeadBatch(IReadOnlyList<DividendOutcome> outcomes)
        => outcomes.Count > 0 && outcomes.All(o => !o.Ok) && outcomes.Any(o => o.RateLimited);

    /// <summary>
    /// 落一批：数据 + 状态。
    ///
    /// ⚠ **空结果不写**（<c>rows.Count > 0</c> 这个判断必须在）：<c>ReplaceByCode</c> 是先 DELETE
    /// 再插，限流返回空页面时若照写，会把库里已有的分红整只删光。拉不到新数据时，
    /// 库里上一次的结果仍然有效——跟 CustomerSupplierTask 的消歧那条是同一条铁律。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<DividendOutcome> batch, CancellationToken ct)
    {
        var now = DateTime.Now;
        var states = new List<DividendFetchState>(batch.Count);

        foreach (var o in batch)
        {
            _attempted.Add(o.Code);
            if (!_states.TryGetValue(o.Code, out var st))
                st = new DividendFetchState { Code = o.Code };

            if (o.Ok)
            {
                if (o.Dividends.Count > 0) repository.ReplaceByCode(o.Code, o.Dividends);
                if (o.Rights.Count > 0) SqliteRightsIssueUpsert.ReplaceByCode(paths.CurrentDb, o.Code, o.Rights);
                st.LastOkAt = now;
                st.DividendRows = o.Dividends.Count;
                st.RightsRows = o.Rights.Count;
            }
            else
            {
                // 失败只更新失败那两列：LastOkAt 原样留着，否则一次失败就抹掉"抓过"这个事实。
                _failed.Add(o.Code);
                st.LastFailAt = now;
                st.FailReason = o.Error;
            }

            _states[o.Code] = st;
            states.Add(st);
        }

        repository.SaveFetchStates(states);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 被取消时把失败名单落盘——**这是这次迁移的起因**：老实现把它写在 <c>Task.WhenAll</c>
    /// 之后，取消直接冒泡，于是停一次就什么都不剩。数据本身已经按批落过库了。
    /// ⚠ 这时 ct 已经取消，只做同步的库操作。
    /// </summary>
    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo();
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        SaveFailedTodo();

        if (_abortReason != null)
        {
            Report("⚠ " + _abortReason);
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(_abortReason));
        }

        if (_nothingToDoNote != null)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(nothingToDo: true, progress: _nothingToDoNote));

        int left = _planned - _done;
        var summary = $"分红送配完成：本轮 {_done} 只、有分红 {_withData} 只、共写 {_rows:N0} 条、"
                    + $"配股 {_rights:N0} 条、失败 {_failed.Count} 只"
                    + (left > 0 ? $"；本轮截断，还剩 {left} 只没抓（下轮接着来）" : "");
        Report(summary + (_failed.Count > 0 ? "（失败的可点【重新拉取失败股票】重试）" : ""));
        return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(progress: summary));
    }

    /// <summary>
    /// 更新失败待办。语义跟 <c>FetchOrchestrator.SetFailedTodo</c> 一模一样：
    /// **本轮碰过、这次没失败的移出名单**，本轮新失败的并进去。没碰过的那些原样留着
    /// ——它们只是这轮没排到，不是补好了。
    /// </summary>
    private void SaveFailedTodo()
    {
        if (_attempted.Count == 0) return;
        var manifest = manifestStore.Load();
        var current = manifest.Todo(RetryTaskIds.Dividend, RetryTodoKind.Failed)?.Targets
                              .Select(t => t.Code).ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);
        current.ExceptWith(_attempted);
        current.UnionWith(_failed);
        manifest.SetTodo(RetryTaskIds.Dividend, RetryTodoKind.Failed,
                         current.OrderBy(c => c, StringComparer.Ordinal)
                                .Select(c => new RetryTarget { Code = c }).ToList());
        manifestStore.Save(manifest);
    }
}
