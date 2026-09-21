using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【融资余额】（2026-09-18 从编排器迁到新框架）。见 doc/margin-task-design.md。
///
/// ════ 为什么迁 ════
/// 它是 <c>FetchOrchestrator.DailyRefetcherFor</c> 里**最后一个** case——迁完那个方法整个消失，
/// "待办归谁补"对所有日频项就只剩一个答案：归它自己（上一步见 doc/fill-backlog-to-tasks-design.md）。
/// 顺带把只剩它一个用户的 <c>RunStepBackfillDailyOneAsync</c> 和一个没有任何调用方的
/// <c>RunFetchMarginAsync</c> 一起清掉。
///
/// ════ 跟龙虎榜/席位/大宗那三项最要紧的差别 ════
/// 这张表是 <b>InsertOrIgnore 合并</b>，不是整日替换。于是那三张表最要命的
/// "抓不全就整天不落库"在这里**反过来**：两所分批发布，抓到半天也该写进去，
/// 主键去重、下轮补另一半。所以这里**没有** count 校验，残缺日也是靠"再抓一次、合并"修好的。
///
/// ════ 两条最容易在重构里被"简化"掉的规矩 ════
/// ① <b>最近 5 个交易日无条件重抓</b>（<see cref="ForceRefetchDays"/>）——它看起来像多余的
///    重复劳动。丢了它，两所分批发布的残缺会被**永久固化**："有行就跳过"，那半天再也补不回来。
/// ② <b>空日定案要等 3 天</b>（<see cref="DailyNoDataGate"/>）——两所是 T+1，当天拿到 0 行多半
///    只是还没发。写成"0 行就定案"的话那天会被永久钉死，往后一个请求都不再发。
///
/// 一批＝**一天**，于是 <c>MaxItems</c>（本轮最多抓几天）和 <c>Deadline</c>（到点收尾）
/// 是骨架白送的——整段回补约 3900 个交易日，最需要的正是这两件事。
/// </summary>
public sealed class MarginTask(
    IMarginRepository repository,
    IMarginProvider provider,
    ITradingDayRepository tradingDays,
    IManifestStore manifestStore,
    FetchPaths paths,
    IDailyFetchNoDataRepository? noDataRepository = null) : FetchTaskBase<MarginTask.MarginDay>
{
    public override FetchActionId Id => FetchActionId.StepMargin;

    /// <summary>
    /// 残缺日待办自己补（见 doc/fill-backlog-to-tasks-design.md）。
    /// 两融的待办只有"残缺日"这一类，来源是日频体检的 <c>OwnerTaskId</c>。
    /// </summary>
    public override bool HandlesBacklog => true;

    /// <summary>
    /// 增量往前回看的交易日数。
    ///
    /// 两所 T+1 发布，且盘后是**陆续**出的，所以不能只抓当天。回看 10 天里绝大多数会被
    /// "本地已有"闸挡掉，日常代价基本为零（正常只会真去抓 1 天）。
    /// </summary>
    private const int LookbackTradingDays = 10;

    /// <summary>最近这么多个交易日**无条件重抓**（哪怕本地已有）。理由见类注释①。</summary>
    private const int ForceRefetchDays = 5;

    /// <summary>抓一天的结果。空的那天也要走一遭——"确认没有数据"名单要知道"抓了但没有"。</summary>
    public sealed record MarginDay(DateOnly Day, IReadOnlyList<MarginDetailRow> Rows);

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>已确认没有数据的日子（本轮开始时读的，落库时要按它判增删）。</summary>
    private HashSet<DateOnly> _confirmed = [];

    private int _wrote, _okDays, _emptyDays, _newConfirmed, _revoked;
    /// <summary>真发出过请求的天数——判"整轮全败"要用它，不能用计划的天数：
    /// 到点收尾或被取消时一天都没发，那不是失败。</summary>
    private int _attempted;
    private int _planned;
    private string? _skipped;
    private FetchMode _mode;

    /// <summary>【只补待办】那一路的结果。null＝这轮不是补待办、或没有欠着的天。</summary>
    private PartialDayRepairResult? _backlog;

    protected override async IAsyncEnumerable<IReadOnlyList<MarginDay>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _confirmed = [];
        _wrote = _okDays = _emptyDays = _newConfirmed = _revoked = _attempted = _planned = 0;
        _skipped = null;
        _backlog = null;
        _mode = args.Mode;
        _sw.Restart();

        // ── 【只补待办】走完全不同的一条路：目标从待办清单来，不从水位线来 ──
        // 编排整个在 PartialDayRepair 里（逐日重抓 → 用体检同一套判据复查 → Tries →
        // 满 MaxTries 判定"数据源那天就这些"），跟龙虎榜/席位/大宗三项同一个形状。
        if (args.Mode == FetchMode.FillBacklog)
        {
            // 包 Task.Run：它开头读 manifest、查体检 spec 都是同步 IO，骨架不替子类推线程池。
            _backlog = await Task.Run(() => new PartialDayRepair(paths.CurrentDb, manifestStore)
                .RunAsync(RetryTaskIds.Margin,
                          d => new MarginDayWriter(provider, repository).RefetchAsync(d, ct),
                          ProgressSink, ct), ct);
            yield break;
        }

        // 定"抓哪些天"要查库（水位线、交易日历、空日名单、残缺日待办），都是同步 IO。
        // 骨架不替子类推到线程池，在首个 await 之前干这些会冻住界面。
        var (days, confirmed) = await Task.Run(() => Plan(args), ct);
        _confirmed = confirmed;
        _planned = days.Count;
        if (days.Count == 0)
        {
            _skipped = "融资余额这一段里没有要抓的交易日";
            Report($"{_skipped}，本轮不用抓。");
            yield break;
        }

        Report($"融资余额：{Describe(args.Mode)}，共 {days.Count} 天"
             + $"（{days[0]:yyyy-MM-dd} ~ {days[^1]:yyyy-MM-dd}），每天 1 个请求...");

        int done = 0;
        foreach (var day in days)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Deadline is { } dl && DateTime.Now >= dl)
            {
                Report($"到收尾时间了，本轮抓到 {day.AddDays(-1):yyyy-MM-dd} 为止，剩下的下轮接着来。");
                yield break;
            }

            List<MarginDetailRow>? rows = null;
            _attempted++;
            try
            {
                rows = await provider.GetDetailAsync(day, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单天失败不拖垮整轮：记一笔继续往下抓。全轮都失败的话 OnCompletedAsync 会整项判失败。
                var msg = $"融资余额 {day:yyyy-MM-dd}：{ex.Message}";
                Report($"⚠ {msg}");
                _errors.Add(msg);
            }

            done++;
            if (rows == null) continue;

            // ⚠ 文本里**不要**再写一遍进度数字：TaskProgress.ToString() 已经会拼 "（done/total）"。
            // ⚠ 心跳**每天**一次、日志仍按上面的间隔（2026-09-19）：只按日志间隔出声的话，
            //   单位一慢就顶上静默看门狗的 5 分钟上限，一路正常跑也会被判成卡死
            //   （【资金净流入】09-18/09-19 就是这么被掐的，见 QuietWatchdog.IBeatOnlySink）。
            if (rows.Count > 0 || done % 20 == 0 || done == days.Count)
                Report($"融资余额 {day:yyyy-MM-dd}：{rows.Count} 条", done, days.Count);
            else
                ReportQuiet($"融资余额 {day:yyyy-MM-dd}：{rows.Count} 条", done, days.Count);

            yield return [new MarginDay(day, rows)];
        }
    }

    /// <summary>
    /// 一批＝一天：合并落库（已有的行不动），再按判据动"确认没有数据"名单。
    ///
    /// ⚠ 名单的增删判据走 <see cref="DailyNoDataGate"/>——**别在这里手写**"0 行就 Confirm"：
    /// 两所 T+1，当天的空多半只是还没发，定了案那天就被永久钉死了。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<MarginDay> batch, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var d in batch)
        {
            if (d.Rows.Count > 0)
            {
                repository.InsertOrIgnore(d.Rows);
                _wrote += d.Rows.Count;
                _okDays++;
            }
            else _emptyDays++;

            if (noDataRepository == null) continue;
            switch (DailyNoDataGate.Evaluate(d.Rows.Count, d.Day, today, _confirmed.Contains(d.Day)))
            {
                case NoDataAction.Confirm:
                    noDataRepository.Confirm(IDailyFetchNoDataRepository.MarginDataset, d.Day);
                    _confirmed.Add(d.Day);
                    _newConfirmed++;
                    break;
                case NoDataAction.Revoke:
                    noDataRepository.Remove(IDailyFetchNoDataRepository.MarginDataset, d.Day);
                    _confirmed.Remove(d.Day);
                    _revoked++;
                    break;
            }
        }
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        if (_mode == FetchMode.FillBacklog)
        {
            Report("融资余额·补待办中断。已重抓的天都落库了（合并写入，已有的行不动），"
                 + "没补完的仍在待办里，下次再点一次即可。");
            return Task.CompletedTask;
        }

        // 跟龙虎榜那几张表不同：这里**中断也不丢进度**。整段回补每轮都重新排期，而
        // "本地已有"闸会把这轮写进去的天挡掉——下轮自然从断点往后走。
        Report($"融资余额中断。已落库的 {_okDays} 天有效（合并写入、幂等），"
             + "下轮重新排期时这些天会被\"本地已有\"闸跳过，等于接着走。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 【只补待办】那一路：一行都不经过 SaveBatchAsync（编排和落库都在 PartialDayRepair 里）。
        if (args.Mode == FetchMode.FillBacklog)
        {
            if (_backlog is not { } r)
                // 「没活可干」不是「没开工」——写成 Skipped 会让计划引擎立刻再排一次、空转
                // （2026-09-21 统一改过来，见 TaskRunResult.Skipped 的注释）。
                return Task.FromResult<TaskRunResult?>(
                    new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, "融资余额没有欠着的残缺日"));

            var line = $"融资余额残缺日：{r.Days} 天里补上 {r.Fixed} 天、写入 {r.Rows} 行"
                     + (r.Failed > 0 ? $"，{r.Failed} 天重抓失败" : "")
                     + (r.ConfirmedNow > 0 ? $"，{r.ConfirmedNow} 天补满仍不齐、已判定数据源就这些" : "")
                     + "。";
            return Task.FromResult<TaskRunResult?>(
                r.Failed > 0 && r.Failed == r.Days
                    ? new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, line)
                    : new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, line));
        }

        // ⚠ 这是「没活可干」，不是「没开工」（2026-09-21 统一改过来）——Skipped 的语义是
        //    "这轮被挡住了、今天恢复了还该再来"，于是计划引擎立刻再排一次，而条件根本不会变，
        //    空转到被"连着 5 轮瞬间跑完"那道护栏拦下。见 TaskRunResult.Skipped 的注释。
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, why));

        var summary = $"融资余额：{_okDays} 天写入 {_wrote} 条"
                    + (_emptyDays > 0 ? $"，{_emptyDays} 天数据源还没有（两所 T+1，明天这轮会补上）" : "")
                    + (_newConfirmed > 0 ? $"，新确认 {_newConfirmed} 天确实没有数据（往后不再重试）" : "")
                    + (_revoked > 0 ? $"，{_revoked} 天数据源后来补上了、已撤销结论" : "")
                    + (_errors.Count > 0 ? $"，{_errors.Count} 天抓取失败" : "")
                    + $"，用时 {Fmt(_sw.Elapsed)}。";
        Report(summary);

        // 发出去的每一天都没抓成 → 整项失败（多半是被限流或断网，不是"数据源没有"）。
        // ⚠ 判据是**发过请求的**天数，不是计划的天数：到点收尾时一天都没发，那不是失败。
        if (_attempted > 0 && _okDays == 0 && _emptyDays == 0)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }

    // ── 抓哪些天 ──────────────────────────────────────────────────

    /// <summary>
    /// 按模式定这一轮抓哪些天，并把"已确认没有数据"的名单一并读出来（落库时要按它判增删）。
    ///
    /// 四道闸走共用的 <see cref="DailyBackfillGate"/>——那套判据错一处就是静默漏数据
    /// （把"日历不知道"当成"不是交易日"会跳过整段该抓的日子），所以它是纯函数、单独有测试。
    /// </summary>
    private (List<DateOnly> Days, HashSet<DateOnly> Confirmed) Plan(TaskRunArgs args)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var confirmed = noDataRepository?.GetConfirmed(IDailyFetchNoDataRepository.MarginDataset)
                        ?? [];

        // 人点名要某一天：绕开所有闸——那次本来就是要重查（本地已有也好、名单里说没有也好）。
        if (args.Mode == FetchMode.SpecificDay)
            return ([args.Day ?? today], confirmed);

        var calendar = LoadCalendar();

        DateOnly start, end;
        HashSet<DateOnly> have;
        if (args.Mode == FetchMode.FirstBackfill)
        {
            start = provider.EarliestAvailable;
            end = today;
            // ⚠ 扣掉已知残缺日：GetTradeDates 只看"这天有没有行"，2026-08-21 有 1,998 行沪市
            //   就被算作"已有"，深市那一半永远补不回来。
            var partial = new PartialDayRepair(paths.CurrentDb, manifestStore).DaysOf(RetryTaskIds.Margin);
            have = repository.GetTradeDates().Except(partial).ToHashSet();
        }
        else
        {
            end = args.Day ?? today;
            // 按自然日往前退，够覆盖 LookbackTradingDays 个交易日即可（退 2 倍天数足够，
            // 非交易日由日历闸挡掉、不白发请求）。
            start = end.AddDays(-LookbackTradingDays * 2);
            have = repository.GetTradeDates();
        }

        if (start < provider.EarliestAvailable)
        {
            Report($"融资余额：起点上提到 {provider.EarliestAvailable:yyyy-MM-dd}"
                 + "——该日之前两融业务还不存在，不是漏抓。");
            start = provider.EarliestAvailable;
        }
        if (start > end) return ([], confirmed);

        // 最近几个交易日无条件重抓（闸④）。日历还没建时退化成"最近 7 个自然日"，宁可多抓几天。
        var recent = calendar != null
            ? calendar.LastTradingDays(end.ToDateTime(TimeOnly.MinValue), ForceRefetchDays)
                      .Select(DateOnly.FromDateTime).ToHashSet()
            : Enumerable.Range(0, 7).Select(i => end.AddDays(-i)).ToHashSet();

        var days = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
            if (DailyBackfillGate.Evaluate(d, calendar, confirmed, have, recent) == DailySkipReason.None)
                days.Add(d);
        return (days, confirmed);
    }

    /// <summary>
    /// 本地归纳的交易日历。拿不到时返回 null——<see cref="DailyBackfillGate"/> 会退回
    /// "只跳周末"，宁可多发几个请求也不静默漏抓（project_trading_calendar_pitfall 栽过一次）。
    /// </summary>
    private TradingCalendar? LoadCalendar()
    {
        try
        {
            var days = tradingDays.GetAll();
            if (days.Count > 0) return new TradingCalendar(days);
            Report("⚠ 本地交易日历还是空的，这一轮只能按\"跳过周末\"来（节假日会白发请求）"
                 + "——跑一次【交易日历】就好了。");
        }
        catch (Exception ex)
        {
            Report($"⚠ 读交易日历失败（{ex.Message}），这一轮按\"跳过周末\"来。");
        }
        return null;
    }

    private static string Describe(FetchMode mode) => mode switch
    {
        FetchMode.FirstBackfill => "整段回补（2010-03-31 至今，跳过本地已有的）",
        FetchMode.SpecificDay => "只抓指定的那一天",
        _ => $"增量（往前回看 {LookbackTradingDays} 个交易日，最近 {ForceRefetchDays} 个无条件重抓）",
    };

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:F1} 秒";
}
