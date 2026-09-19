using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【资金净流入】（2026-09-18 从编排器迁到新框架）。见 doc/netinflow-task-design.md。
///
/// ════ 为什么迁 ════
/// 两融迁完之后，它是**唯一**"配了 OwnerTaskId（会产生残缺日待办）、却没有按天重抓入口"的
/// 日频项——每轮只在日志里喊一句"只能人工处理"。迁完之后
/// <c>FetchOrchestrator.FillPartialDaysAsync</c> 那条分支就没人走了，整个方法一起删掉。
///
/// ════ 跟前面四项最要紧的差别：它是**逐股**接口 ════
/// 龙虎榜/席位/大宗/两融都是"一天一个请求拿回全市场"，所以按天重抓很便宜。这一项反过来：
///   · 数据源一次请求返回**整只票的全部历史**，窗口在客户端裁——"补几天跟补一天一样贵"；
///   · 反过来，**补某一天 ＝ 全市场 5,500 个请求 ≈ 1.75 小时**。
/// 所以残缺日走的是 <see cref="PartialDayRepair.RunBatchAsync"/>（一轮把欠着的天一起补），
/// 不是逐天那条路——十天就跑十轮的话纯属浪费。
///
/// ════ 一批 ＝ 一组 <see cref="DefaultBatchSize"/> 只（组内并发） ════
/// 照 <c>DividendTask</c> 的先例。这样 <c>MaxItems</c>（本轮抓几批）和 <c>Deadline</c>
/// （到点收尾）都落在批边界上——全市场 1.75 小时的活**第一次能分批跑、能中途停**
/// （迁移前停了就是整轮白费）。
///
/// ⚠ 最容易在重构里丢掉的一条：<b>当天那一行要重抓</b>（<see cref="IncrementalWindowCalculator.IsConfirmedFinal"/>）。
/// 盘中抓到的行是不完整的，判成"已有"就被永久固化了——跟 project_intraday_bar_confirmation 同一类坑。
/// </summary>
public sealed class NetInflowTask(
    INetInflowFetcher fetcher,
    IManifestStore manifestStore,
    FetchPaths paths,
    int batchSize = NetInflowTask.DefaultBatchSize) : FetchTaskBase<NetInflowTask.CodeResult>
{
    public override FetchActionId Id => FetchActionId.StepNetInflow;

    /// <summary>三类待办（失败名单 / 整天缺失 / 残缺日）都由本任务自己补。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>一批几只。组内并发、批间串行——批边界就是 MaxItems/Deadline 的落点。</summary>
    public const int DefaultBatchSize = 30;

    /// <summary>没有水位线的票（从没抓过）初始回看多少天。</summary>
    public const int InitialLookbackDays = 60;

    /// <summary>
    /// 补"整天缺失"时，失败只数超过这个比例就判定**被限流**——一个 Tries 都不加。
    ///
    /// 不这么挡的话，限流期间跑两轮就会把真·漏抓日全部判成"数据源确实没有"、永久静音。
    /// </summary>
    public const double ThrottledFailRatio = 0.5;

    /// <summary>补满这么多轮仍拿不到，就判定"数据源确实没有这天"。</summary>
    public const int MaxTries = 2;

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    /// <summary>一只票的抓取结果。失败的也是一条——失败名单要按"这轮碰过"来算。</summary>
    public sealed record CodeResult(string Code, List<NetInflow> Rows, bool Ok);

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _attempted = [];
    private readonly ConcurrentBag<string> _failed = [];

    private int _planned, _done, _rows;
    private string? _skipped;
    private FetchMode _mode;
    private string? _backlogLine;

    private SqliteNetInflowRepository Repo => _repo ??= new SqliteNetInflowRepository(paths.CurrentDb);
    private SqliteNetInflowRepository? _repo;

    protected override async IAsyncEnumerable<IReadOnlyList<CodeResult>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        Repo.EnsureSchema();
        _errors.Clear();
        _attempted.Clear();
        _failed.Clear();
        _planned = _done = _rows = 0;
        _skipped = _backlogLine = null;
        _mode = args.Mode;
        _sw.Restart();

        // ── 【只补待办】：三类待办，两种补法 ────────────────────────────
        // ① failed（逐只失败）＝ 跟增量同一个动作，只是目标从名单来；
        // ② missing_days（整天缺失）+ partial_day（残缺日）＝ 都得"全市场逐只再跑一轮"
        //    （数据源一次返回全历史，补哪天都一样贵），所以合并成一轮抓。
        //    ⚠ 但**复查判据不能合并**：整天缺失看"那天有没有行"，残缺日必须走体检同一套判据
        //    （行数偏少也算没补上）。两者的"确认没有"名单也是两份。
        if (args.Mode == FetchMode.FillBacklog)
        {
            await foreach (var batch in FillBacklogAsync(ct)) yield return batch;
            yield break;
        }

        // 排期要逐只查水位线，都是同步 IO。骨架不替子类推线程池，首个 await 之前干这些会冻住界面。
        var plan = await Task.Run(() => Plan(args), ct);
        _planned = plan.Count;
        if (plan.Count == 0)
        {
            _skipped = "资金净流入：所有标的都已经抓到最新了";
            Report($"{_skipped}，本轮不用抓。");
            yield break;
        }

        Report($"资金净流入：{Describe(args.Mode)}，共 {plan.Count} 只"
             + $"（每批 {_batchSize} 只并发，逐只查询、一次返回整只票的历史）...");

        foreach (var batch in plan.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var got = await FetchBatchAsync(batch, ct);
            _done += batch.Length;
            // ⚠ 这里**不报写入行数**：骨架是"先 yield、再 SaveBatchAsync"，
            //   _rows 要等这一批存完才涨，在这儿读永远差一批（第一批会显示"写入 0 行"）。
            //   行数留给收尾那句汇总。
            //
            // ⚠ 日志每 300 只一行，心跳**每批**一次（2026-09-19 修）：一批 30 只实测约 32 秒，
            //   300 只要 5 分半——比默认静默上限（5 分钟）还长，只在日志那个间隔出声的话，
            //   任务一路正常抓着也会被静默看门狗判成卡死掐断（09-18、09-19 各被掐一次，
            //   分别死在第 180、360 行）。见 QuietWatchdog.IBeatOnlySink。
            ReportProgress($"资金净流入：已抓 {_done} 只", _done, plan.Count);
            yield return got;
        }
    }

    /// <summary>
    /// 报一步进展：**每批都喂看门狗**，日志每 <see cref="LogEveryBatches"/> 批才写一行。
    ///
    /// 全市场一轮 186 批、1.75 小时，每批写一行日志太吵；可只按日志间隔出声又会被
    /// 5 分钟静默上限误判成卡死（这一项 2026-09-18、09-19 就是这么被掐的）。
    /// 分开之后日志密度不变、心跳变密十倍。见 <see cref="QuietWatchdog.IBeatOnlySink"/>。
    /// </summary>
    private void ReportProgress(string text, int done, int total)
    {
        if (done % (_batchSize * LogEveryBatches) == 0 || done >= total) Report(text, done, total);
        else ReportQuiet(text, done, total);
    }

    /// <summary>日志每多少批写一行（心跳不受它影响，每批都有）。</summary>
    private const int LogEveryBatches = 10;

    /// <summary>抓一批（组内并发）。单只失败不拖垮整批——进失败名单，下轮再来。</summary>
    private async Task<List<CodeResult>> FetchBatchAsync(IReadOnlyList<(string Code, DateTime Start, DateTime End)> batch,
                                                        CancellationToken ct)
    {
        var tasks = batch.Select(async t =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rows = await fetcher.FetchAsync(t.Code, t.Start, t.End, ct);
                return new CodeResult(t.Code, rows, Ok: true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _failed.Add(t.Code);
                if (_errors.Count < 20) _errors.Add($"资金净流入 {t.Code}：{ex.Message}");
                return new CodeResult(t.Code, [], Ok: false);
            }
        });
        var got = (await Task.WhenAll(tasks)).ToList();
        lock (_attempted) _attempted.AddRange(batch.Select(b => b.Code));
        return got;
    }

    /// <summary>
    /// 落一批。
    ///
    /// ⚠ **"今天"那一行要覆盖写**：它可能是第二次抓到（盘中一次、收盘后一次），
    /// InsertOrIgnore 会把盘中那份永久留住。更早的日期永远是第一次见到的新事实，去重即可。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<CodeResult> batch, CancellationToken ct)
    {
        var today = DateTime.Today;
        foreach (var r in batch)
        {
            if (r.Rows.Count == 0) continue;
            var todays = r.Rows.Where(x => x.PeriodStart.Date == today).ToList();
            var older = r.Rows.Where(x => x.PeriodStart.Date != today).ToList();
            if (older.Count > 0) Repo.InsertOrIgnore(older);
            if (todays.Count > 0) Repo.Upsert(todays);
            _rows += r.Rows.Count;
        }
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo();
        Report($"资金净流入中断。已落库的 {_rows} 行有效；"
             + (_mode == FetchMode.FillBacklog
                ? "没补完的仍在待办里，下次再点一次即可。"
                : "下轮按各自的水位线接着走即可（已抓到的票会被跳过）。"));
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        SaveFailedTodo();

        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

        var summary = _backlogLine
            ?? $"资金净流入：{_done} 只写入 {_rows} 行"
               + (_failed.Count > 0 ? $"，{_failed.Count} 只失败（已记进待办）" : "")
               + $"，用时 {ElapsedText.Format(_sw.Elapsed)}。";
        Report(summary);

        // 碰过的票全失败 → 整项失败（多半是被限流或断网）。只要有一只成了就算完成。
        if (_attempted.Count > 0 && _failed.Count == _attempted.Count)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }

    // ── 排期 ──────────────────────────────────────────────────────

    /// <summary>
    /// 本轮抓哪些票、每只抓哪一段。
    ///
    /// 每只票**各有各的水位线**（这张表按只累积），所以窗口是逐只算的：
    ///   · 从没抓过 → 往前 <see cref="InitialLookbackDays"/> 天；
    ///   · 水位线早于终点 → 从水位线的下一天续抓；
    ///   · 水位线就是终点那天 → 看它是不是**收盘后**抓的：是就跳过，不是就重抓那天（见类注释 ⚠）。
    /// </summary>
    private List<(string Code, DateTime Start, DateTime End)> Plan(TaskRunArgs args)
    {
        var codes = LocalStockCodes();
        var end = args.Mode == FetchMode.SpecificDay
            ? args.Day?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today
            : DateTime.Today;
        bool exactDayOnly = args.Mode == FetchMode.SpecificDay;
        return PlanFor(codes, end, exactDayOnly);
    }

    private List<(string Code, DateTime Start, DateTime End)> PlanFor(
        IReadOnlyList<string> codes, DateTime end, bool exactDayOnly)
    {
        var list = new List<(string, DateTime, DateTime)>();
        foreach (var code in codes)
        {
            DateTime start;
            if (exactDayOnly)
            {
                // 人点名要某一天：只有"那天已经有、且是收盘后抓的"才跳过。
                var existing = Repo.Query(code, end, end).FirstOrDefault();
                start = existing != null && IncrementalWindowCalculator.IsConfirmedFinal(existing.FetchedAt, end)
                    ? end.AddDays(1) : end;
            }
            else
            {
                var info = Repo.GetLatestRowInfo(code);
                if (info == null) start = end.AddDays(-InitialLookbackDays);
                else if (info.Value.PeriodStart.Date < end.Date) start = info.Value.PeriodStart.AddDays(1);
                else start = IncrementalWindowCalculator.IsConfirmedFinal(info.Value.FetchedAt, end)
                    ? end.AddDays(1) : end;
            }
            if (start.Date <= end.Date) list.Add((code, start, end));
        }
        return list;
    }

    private List<string> LocalStockCodes()
    {
        var codes = SqliteStockMetaUpsert.GetAll(paths.CurrentDb).Select(s => s.Code).ToList();
        if (codes.Count == 0)
            throw new InvalidOperationException(
                "本地还没有股票名册，这一步没有可抓的标的——请先跑一次【股票名册与流通市值】");
        return codes;
    }

    // ── 只补待办 ──────────────────────────────────────────────────

    /// <summary>
    /// 三类待办：失败名单按水位线重抓；整天缺失和残缺日合并成**一轮全市场**（见类注释）。
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<CodeResult>> FillBacklogAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var manifest = await Task.Run(manifestStore.Load, ct);
        var failedCodes = (manifest.Todo(RetryTaskIds.NetInflow, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var missingDays = (manifest.Todo(RetryTaskIds.NetInflow, RetryTodoKind.MissingDays)?.Targets ?? [])
            .Where(t => t.Day.HasValue).Select(t => t.Day!.Value.Date).Distinct().OrderBy(d => d).ToList();
        var partialDays = new PartialDayRepair(paths.CurrentDb, manifestStore)
            .DaysOf(RetryTaskIds.NetInflow);

        if (failedCodes.Count == 0 && missingDays.Count == 0 && partialDays.Count == 0)
        {
            _skipped = "资金净流入没有欠着的待办";
            Report($"{_skipped}。");
            yield break;
        }

        var lines = new List<string>();

        // ── ① 失败名单：跟增量同一个动作，只是目标从名单来 ──
        if (failedCodes.Count > 0)
        {
            var plan = await Task.Run(() => PlanFor(failedCodes, DateTime.Today, exactDayOnly: false), ct);
            Report($"资金净流入：重抓上次失败的 {failedCodes.Count} 只（其中 {plan.Count} 只还缺数据）...");
            foreach (var batch in plan.Chunk(_batchSize))
            {
                ct.ThrowIfCancellationRequested();
                var got = await FetchBatchAsync(batch, ct);
                _done += batch.Length;
                // 2026-09-19：这一段以前**整段不报任何进展**——名单一长（几百只、十几分钟）
                // 就会被静默看门狗判成卡死掐断，而且掐在哪儿日志里一个字都没有。
                ReportProgress($"资金净流入·补失败名单：已抓 {_done}/{plan.Count} 只", _done, plan.Count);
                yield return got;
            }
            lines.Add($"失败名单 {failedCodes.Count} 只");
        }

        // ── ② 整天缺失 + ③ 残缺日：合并成一轮全市场 ──
        if (missingDays.Count > 0 || partialDays.Count > 0)
        {
            var days = missingDays.Concat(partialDays.Select(d => d.ToDateTime(TimeOnly.MinValue)))
                                  .Distinct().OrderBy(d => d).ToList();
            var (rows, throttled) = await FillDaysAsync(days, ct);

            if (missingDays.Count > 0)
                lines.Add(ReviewMissingDays(missingDays, throttled));
            if (partialDays.Count > 0)
            {
                // 残缺日的复查/Tries/确认名单跟别的项**共用** PartialDayRepair——
                // 这里只把"怎么重抓"喂进去，而且是**一轮补完所有天**那个形状。
                var r = await new PartialDayRepair(paths.CurrentDb, manifestStore)
                    .RunBatchAsync(RetryTaskIds.NetInflow,
                                   _ => Task.FromResult((rows, throttled)),   // 上面那一轮已经抓过了
                                   ProgressSink, ct);
                if (r?.Done is { } line) lines.Add(line);
            }
        }

        _backlogLine = $"资金净流入·补待办：{string.Join("、", lines)}，"
                     + $"写入 {_rows} 行，用时 {ElapsedText.Format(_sw.Elapsed)}。";
    }

    /// <summary>
    /// 全市场逐只抓一轮，覆盖 <paramref name="days"/> 这几天。
    /// 返回 (写入行数, 是否判定被限流)。
    ///
    /// 只抓 [最早那天, 最晚那天] 这个窗口——数据源一次返回整只票全历史，窗口在客户端裁，
    /// 所以补几天跟补一天一样贵，一轮就够。
    /// </summary>
    private async Task<(int Rows, bool Throttled)> FillDaysAsync(List<DateTime> days, CancellationToken ct)
    {
        var codes = LocalStockCodes();
        var wanted = days.ToHashSet();
        var from = days[0];
        var to = days[^1];

        Report($"资金净流入：补 {days.Count} 天（{string.Join("、", days.Select(d => d.ToString("yyyy-MM-dd")))}），"
             + $"全市场逐只查 {codes.Count} 只——数据源一次返回整只票全部历史，"
             + "所以补几天跟补一天一样贵，一轮就够。⚠ 这一段约 1.75 小时，"
             + "不想现在补可以点\"停止\"，名单留着下次跑。");

        int before = _rows, failed = 0, processed = 0;
        foreach (var batch in codes.Chunk(_batchSize))
        {
            ct.ThrowIfCancellationRequested();
            var tasks = batch.Select(async code =>
            {
                try
                {
                    var got = await fetcher.FetchAsync(code, from, to, ct);
                    return new CodeResult(code, got.Where(r => wanted.Contains(r.PeriodStart.Date)).ToList(), true);
                }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref failed); return new CodeResult(code, [], false); }
            });
            var got = await Task.WhenAll(tasks);
            await SaveBatchAsync(got, ct);          // 这一路不走骨架的流式落库，自己存
            processed += batch.Length;
            // 同主循环：日志每 300 只一行、心跳每批一次（这一路更长，整轮 1.75 小时）。
            ReportProgress($"资金净流入·补缺失日：已处理 {processed}/{codes.Count} 只、写入 {_rows - before} 行"
                         + (failed > 0 ? $"（失败 {failed} 只）" : ""), processed, codes.Count);
        }

        // 大面积失败＝被限流，不是"数据源没有"：一个 Tries 都不加（见 ThrottledFailRatio）。
        bool throttled = failed > codes.Count * ThrottledFailRatio;
        if (throttled)
            Report($"⚠ 资金净流入：本轮 {failed}/{codes.Count} 只请求失败，判定是被限流而不是数据源没有"
                 + "——名单和重试计数原样留着，等会儿再跑一次。");
        return (_rows - before, throttled);
    }

    /// <summary>
    /// "整天缺失"那类的复查：**看那天现在有没有行**（跟残缺日不同，那个要走体检同判据）。
    /// 补满 <see cref="MaxTries"/> 轮仍拿不到就判定"数据源确实没有"，写进确认名单、以后不再报。
    /// </summary>
    private string ReviewMissingDays(List<DateTime> days, bool throttled)
    {
        if (throttled) return $"缺失日 {days.Count} 天（被限流，未计数）";

        var counts = Repo.CountRowsByDay(days);
        var stillEmpty = days.Where(d => counts.GetValueOrDefault(d) == 0).ToList();
        int filled = days.Count - stillEmpty.Count, confirmedNow = 0;

        var manifest = manifestStore.Load();
        var pending = (manifest.Todo(RetryTaskIds.NetInflow, RetryTodoKind.MissingDays)?.Targets ?? []).ToList();
        var triesByDay = pending.Where(p => p.Day.HasValue)
                                .GroupBy(p => p.Day!.Value.Date)
                                .ToDictionary(g => g.Key, g => g.Max(p => p.Tries));
        var confirmed = manifest.ConfirmedNetInflowDays.Select(d => d.Date).ToHashSet();
        var next = new List<RetryTarget>();
        foreach (var d in stillEmpty)
        {
            int tries = triesByDay.GetValueOrDefault(d) + 1;
            if (tries >= MaxTries) { confirmed.Add(d); confirmedNow++; }
            else next.Add(new RetryTarget { Day = d, Tries = tries });
        }
        manifest.SetTodo(RetryTaskIds.NetInflow, RetryTodoKind.MissingDays, next);
        manifest.ConfirmedNetInflowDays = confirmed.OrderBy(d => d).ToList();
        manifestStore.Save(manifest);

        Report($"资金净流入缺失日：补上 {filled}/{days.Count} 天"
             + (confirmedNow > 0
                 ? $"；{confirmedNow} 天补满 {MaxTries} 轮仍拿不到，已判定数据源确实没有、以后体检不再报"
                 : ""));
        return $"缺失日 {days.Count} 天";
    }

    // ── 失败名单 ──────────────────────────────────────────────────

    /// <summary>
    /// 本轮碰过的票里，这次没失败的一律移出名单；这次失败的加回去。
    /// **没被本轮碰到的保持原样**——这条语义别简化成"清空重写"。
    /// </summary>
    private void SaveFailedTodo()
    {
        if (_attempted.Count == 0) return;
        var manifest = manifestStore.Load();
        var current = (manifest.Todo(RetryTaskIds.NetInflow, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).ToList();
        var stillFailed = new HashSet<string>(current, StringComparer.Ordinal);
        stillFailed.ExceptWith(_attempted);
        stillFailed.UnionWith(_failed);
        manifest.SetTodo(RetryTaskIds.NetInflow, RetryTodoKind.Failed,
                         stillFailed.OrderBy(c => c, StringComparer.Ordinal)
                                    .Select(c => new RetryTarget { Code = c }).ToList());
        manifestStore.Save(manifest);
    }

    private static string Describe(FetchMode mode) => mode switch
    {
        FetchMode.SpecificDay => "只抓指定的那一天",
        _ => $"增量（每只按自己的水位线续抓；从没抓过的回看 {InitialLookbackDays} 天）",
    };
}
