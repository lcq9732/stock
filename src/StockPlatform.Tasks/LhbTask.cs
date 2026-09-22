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
/// 【龙虎榜】主表（2026-09-17 迁到新任务框架）。见 doc/lhb-seat-task-design.md §8。
///
/// ════ 它**没有**席位表那个病 ════
/// 同一张榜的两个表，一个脏一个干净，差别在三处，这张表三道闸早就都在：
///   · 排序键 <c>TRADE_DATE,SECURITY_CODE,EXPLANATION</c> —— **已唯一**，深分页不会跨页错位；
///   · 落库走 <c>ReplaceDays</c> —— **已是整天替换**，重抓不会堆副本；
///   · 主键末列是 <c>reason</c>（业务字段），不是位次。
/// 所以这次是**纯框架搬家**，不改抓取口径、不动数据。
///
/// ════ 唯一一处行为变化：四个入口的落库口径统一了 ════
/// 搬之前这张表有四个入口、三套口径：日常增量走"派生对应值 + 整天替换"，
/// 而"只抓某一天""整段回补""补残缺日"三条都走 <c>InsertOrIgnore</c> 且**不派生**——
/// 它们写进去的新行 <c>deviation</c> 永远是空的。现在四条共用
/// <see cref="LhbDayWriter"/>，统一到已经正确的那条。
///
/// ════ 一批＝一个月片 ════
/// 保留按月切片（**不**跟着席位表改按日）：请求数差一个量级——全量回补月片约 580 个请求、
/// 十几分钟，逐日要 5300 个、近两小时；日常增量回看 31 个交易日，月片 2~3 个请求，逐日 31 个。
/// 整天替换的粒度不受切片粒度影响：<c>ReplaceDays</c> 按行里的日期整天替换，
/// 一个月片覆盖的就是那个月的所有天。
///
/// 新浪源（<c>fetcher-settings.json</c> 的 <c>LhbSource</c> 可切回去）没有月片接口，
/// 退回逐日抓——那时一批就是一天。
/// </summary>
public sealed class LhbTask(
    string dbPath,
    ILhbRepository repository,
    ILhbProvider provider,
    ITradingDayRepository tradingDays,
    IManifestStore? manifestStore = null,
    IDailyFetchNoDataRepository? noDataRepository = null) : FetchTaskBase<LhbRow>
{
    public override FetchActionId Id => FetchActionId.StepLhb;

    /// <summary>
    /// 残缺日待办自己补（2026-09-18 收口，见 doc/fill-backlog-to-tasks-design.md）。
    ///
    /// 09-17 迁移时这一条还是 false——待办编排留在 <c>FetchOrchestrator</c> 那边。
    /// 能搬过来的前提是**龙虎榜的待办只有"残缺日"这一类**（唯一来源是日频体检的
    /// <c>OwnerTaskId</c>）：转交之后 orchestrator 那条路对本项不再跑，真有别的类别
    /// 就会**静默补不上**。这个前提由 <c>RetryDispatchTests</c> 的守卫钉着。
    ///
    /// ⚠ 没注入 <c>manifestStore</c> 就补不了（待办在 manifest 里），所以这里跟着它走——
    /// 声明 true 却没有仓库的话，分派会把待办交过来然后什么都不做。
    /// </summary>
    public override bool HandlesBacklog => manifestStore != null;

    /// <summary>
    /// 东财源的增量回看窗口（交易日）。照 <c>d30_chg</c> 定的——上榜后 30 日涨跌幅要等
    /// 30 个交易日才有值，窗口短了那一列就永远空着。月片下拉长窗口几乎不增加请求数。
    /// </summary>
    private const int LaggingLookbackTradingDays = 31;

    /// <summary>新浪源的回看窗口：它没有滞后字段，5 天只为覆盖"盘后陆续公布"。</summary>
    private const int PlainLookbackTradingDays = 5;

    /// <summary>太近的日子不定案——盘后可能还没发布完，这时记进"确认没有"会把它永久钉死。</summary>
    private const int ConfirmAfterDays = 3;

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>本轮真抓到行的交易日——判"哪些天数据源确实没有"要用它。</summary>
    private readonly HashSet<DateOnly> _seen = [];
    /// <summary>本轮计划覆盖的交易日；空＝没法判空日（整段回补时日历缺失等），那就不判。</summary>
    private List<DateOnly> _targets = [];
    private int _rows, _batches, _noData;
    private bool _explicitDay;
    private string? _skipped;

    /// <summary>【只补待办】那一路的结果。null＝这轮不是补待办、或没有欠着的天。</summary>
    private PartialDayRepairResult? _backlog;
    /// <summary>本轮是不是【只补待办】——<see cref="OnStoppedAsync"/> 拿不到 args。</summary>
    private bool _fillBacklog;

    private LhbDayWriter Writer => new(dbPath, repository);

    protected override async IAsyncEnumerable<IReadOnlyList<LhbRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        // 数据源的状态播报（限流退避/重试）转成日志——不订阅的话被退避时一个字都没有
        using var statusSub = ForwardStatus(h => provider.OnStatus += h, h => provider.OnStatus -= h);
        _errors.Clear();
        _seen.Clear();
        _targets = [];
        _rows = _batches = _noData = 0;
        _skipped = null;
        _backlog = null;
        _explicitDay = args.Mode == FetchMode.SpecificDay;
        _fillBacklog = args.Mode == FetchMode.FillBacklog;
        _sw.Restart();

        // ── 【只补待办】走完全不同的一条路：目标从待办清单来，不从水位线来 ──
        // 编排整个在 PartialDayRepair 里（逐日重抓 → 用体检同一套判据复查 → Tries →
        // 满 MaxTries 判定"数据源那天就这些"）。**不要**把残缺日当普通的天塞进 Plan
        // 走下面的流式路径：那等于把复查判据和 Tries 抄一遍，正是抽出那个类要避免的事。
        //
        // 它自己一天一天循环、每天开头检查取消，所以停止停在**天的边界**上。
        if (_fillBacklog && manifestStore is { } store)
        {
            // 包 Task.Run：它开头读 manifest、查体检 spec 都是同步 IO，骨架不替子类推线程池。
            _backlog = await Task.Run(() => new PartialDayRepair(dbPath, store)
                .RunAsync(RetryTaskIds.Lhb,
                          d => new LhbDayWriter(dbPath, repository, provider).RefetchAsync(d, ct),
                          ProgressSink, ct), ct);
            yield break;
        }

        // 定"抓哪一段"要查库（水位线、交易日历、空日名单），都是同步 IO。骨架不替子类推到
        // 线程池，在首个 await 之前干这些会冻住界面。
        var (start, end, targets, confirmed) = await Task.Run(() => Plan(args), ct);
        if (targets.Count == 0)
        {
            _skipped = "龙虎榜这一段里没有交易日，本轮不用抓";
            Report($"{_skipped}。");
            yield break;
        }
        _targets = targets;

        // ── 东财：按月切片，一片一批 ─────────────────────────────
        if (!_explicitDay && provider is ILhbRangeProvider ranged)
        {
            Report($"龙虎榜：{Describe(args.Mode)}，{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}"
                 + $"（{targets.Count} 个交易日），按月切片抓...");

            await foreach (var slice in ranged.FetchSlicesAsync(start, end, ct))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var r in slice.Rows) _seen.Add(DateOnly.FromDateTime(r.TradeDate));
                // ⚠ 文本里不要再写一遍进度数字：TaskProgress.ToString() 已经会拼 "（done/total）"
                Report($"龙虎榜 {slice.Name}：本月 {slice.Rows.Count} 行", slice.Index, slice.Total);
                if (slice.Rows.Count > 0) yield return slice.Rows;
            }
            yield break;
        }

        // ── 新浪 / 指定某一天：逐日 ───────────────────────────────
        Report($"龙虎榜：{Describe(args.Mode)}，共 {targets.Count} 天"
             + $"（{targets[0]:yyyy-MM-dd} ~ {targets[^1]:yyyy-MM-dd}）...");

        int done = 0;
        foreach (var d in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Deadline is { } dl && DateTime.Now >= dl)
            {
                Report($"到收尾时间了，本轮抓到 {d.AddDays(-1):yyyy-MM-dd} 为止，剩下的下轮接着来。");
                yield break;
            }
            // "确认没有数据"的日子不再发请求；人点名要某一天时**绕过**这个名单（要重查它）
            if (!_explicitDay && confirmed.Contains(d)) continue;

            List<LhbRow> rows;
            try
            {
                rows = await provider.GetDailyAsync(d, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var msg = $"龙虎榜 {d:yyyy-MM-dd} 抓取失败：{ex.Message}";
                Report($"⚠ {msg}");
                _errors.Add(msg);
                continue;
            }

            done++;
            if (rows.Count == 0) continue;
            _seen.Add(d);
            // ⚠ 心跳**每片**一次、日志仍按上面的间隔（2026-09-19）：只按日志间隔出声的话，
            //   单位一慢就顶上静默看门狗的 5 分钟上限，一路正常跑也会被判成卡死
            //   （【资金净流入】09-18/09-19 就是这么被掐的，见 QuietWatchdog.IBeatOnlySink）。
            if (done % 10 == 0 || done == targets.Count)
                Report($"龙虎榜：{d:yyyy-MM-dd}", done, targets.Count);
            else
                ReportQuiet($"龙虎榜：{d:yyyy-MM-dd}", done, targets.Count);
            yield return rows;
        }
    }

    /// <summary>一批＝一个月片（或一天）：派生对应值，再按行里的日期整天替换。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<LhbRow> batch, CancellationToken ct)
    {
        _rows += Writer.Write(batch);
        _batches++;
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        // 【只补待办】中断：待办清单本身就是进度（复查在每一轮末尾，中途停的话这一轮一天
        // 都没划掉，下轮原样重来——整天替换是幂等的）。跟水位线无关。
        if (_fillBacklog)
        {
            Report("龙虎榜·补待办中断。已重抓的天都是整天替换、各自一个事务，"
                 + "没补完的仍在待办里，下次再点一次即可。");
            return Task.CompletedTask;
        }

        Report($"龙虎榜中断。已落库的 {_batches} 批是完整的（各自整天替换、各自一个事务），"
             + "下次从水位线接着走即可。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 【只补待办】那一路：一行都不经过 SaveBatchAsync（编排和落库都在 PartialDayRepair
        // 里），所以结局要单独翻译——走下面那套会报"0 个交易日、写入 0 行"。
        if (args.Mode == FetchMode.FillBacklog)
        {
            if (_backlog is not { } r)
                // 「没活可干」不是「没开工」——写成 Skipped 会让计划引擎立刻再排一次、空转
                // （2026-09-21 统一改过来，见 TaskRunResult.Skipped 的注释）。
                return Task.FromResult<TaskRunResult?>(
                    new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, "龙虎榜没有欠着的残缺日"));

            var line = $"龙虎榜残缺日：{r.Days} 天里补上 {r.Fixed} 天、写入 {r.Rows} 行"
                     + (r.Failed > 0 ? $"，{r.Failed} 天重抓失败" : "")
                     + (r.ConfirmedNow > 0 ? $"，{r.ConfirmedNow} 天补满仍不齐、已判定数据源就这些" : "")
                     + "。";
            // 一天都没补成 → 整项失败（多半是限流或断网）。名单和 Tries 已经被
            // PartialDayRepair 原样留着了，下轮还会来。
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

        ConfirmNoDataDays();

        var summary = $"龙虎榜：{_targets.Count} 个交易日、写入 {_rows} 行"
                    + (_noData > 0 ? $"，其中 {_noData} 天数据源本来就没有" : "")
                    + (_errors.Count > 0 ? $"，{_errors.Count} 天抓取失败" : "")
                    + $"，用时 {Fmt(_sw.Elapsed)}。本地共 {repository.CountRows(DateOnly.MinValue, DateOnly.MaxValue)} 行。";
        Report(summary);

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }

    /// <summary>
    /// 目标日里一行都没回来的＝那天确实没人上榜，记进空日名单、往后不再为它发请求。
    ///
    /// ⚠ 只记**够旧**的（<see cref="ConfirmAfterDays"/> 天以前）：盘后可能还没发布完，
    /// 太新的记进去就把它永久钉死了。人点名抓某一天时整段跳过——那次本来就是要重查。
    /// </summary>
    private void ConfirmNoDataDays()
    {
        if (noDataRepository == null || _explicitDay || _targets.Count == 0) return;
        var cutoff = DateOnly.FromDateTime(DateTime.Today).AddDays(-ConfirmAfterDays);
        foreach (var d in _targets)
        {
            if (_seen.Contains(d)) continue;
            _noData++;
            if (d <= cutoff) noDataRepository.Confirm(IDailyFetchNoDataRepository.LhbDataset, d);
        }
    }

    // ── 抓哪一段 ──────────────────────────────────────────────────

    /// <summary>
    /// 按模式定这一轮抓哪一段。返回 (起, 止, 这段里的交易日, 已确认没有数据的日子)。
    ///
    /// **一律走交易日历**而不是逐个自然日试：日历自己缺哪段就瞎哪段，所以拿不到日历时**不猜**
    /// ——退回按工作日铺，宁可多发请求也不静默漏抓（project_trading_calendar_pitfall）。
    /// </summary>
    private (DateTime Start, DateTime End, List<DateOnly> Targets, HashSet<DateOnly> Confirmed) Plan(
        TaskRunArgs args)
    {
        var today = DateTime.Today;
        var confirmed = noDataRepository?.GetConfirmed(IDailyFetchNoDataRepository.LhbDataset) ?? [];

        if (args.Mode == FetchMode.SpecificDay)
        {
            var d = args.Day ?? DateOnly.FromDateTime(today);
            return (d.ToDateTime(TimeOnly.MinValue), d.ToDateTime(TimeOnly.MinValue), [d], confirmed);
        }

        if (args.Mode == FetchMode.FillBacklog)
        {
            // 【只补待办】正常在 FetchAsync 开头就分流走了（PartialDayRepair 那一路）。
            // 只有**没注入 manifestStore** 时才会落到这儿——那种实例的 HandlesBacklog
            // 也是 false，分派根本不会把待办交过来。返回空是兜底，不是主路径。
            return (today, today, [], confirmed);
        }

        DateOnly start;
        if (args.Mode == FetchMode.FirstBackfill)
        {
            // 整段回补不跳过"已有的天"：整天替换是幂等的，而且这正是把历史上那些
            // 只抓到一半的天补齐的机会。580 个请求、十几分钟。
            start = provider.EarliestAvailable;
        }
        else
        {
            int lookback = provider is ILhbRangeProvider
                ? LaggingLookbackTradingDays
                : PlainLookbackTradingDays;
            var cal = tradingDays.GetBetween(
                    DateOnly.FromDateTime(today.AddDays(-lookback * 7 / 5 - 10)), DateOnly.FromDateTime(today))
                .OrderBy(d => d).ToList();
            start = cal.Count >= lookback
                ? cal[^lookback]
                : DateOnly.FromDateTime(today.AddDays(-lookback * 7 / 5 - 2));
        }
        if (start < provider.EarliestAvailable) start = provider.EarliestAvailable;

        var end = DateOnly.FromDateTime(today);

        // 整段回补可以带年份区间（2026-09-22）——【拉取区间数据】分派过来时就带着。
        //
        // ⚠ 不带年份时的行为**一个字不改**（上面那段注释说的"不跳过已有的天"仍然成立，
        //   580 个请求、十几分钟，一次性）。加这一下是因为：忽略年份的话，
        //   一句「补 2024」会让它从 2004 年重跑到今天——**一次性的成本变成了每轮的成本**。
        //   实测：同样的区间连跑两轮，每轮都重写 5803 个交易日、23208 行。
        if (args.Mode.HasFlag(FetchMode.FirstBackfill)
            && (args.YearStart is not null || args.YearEnd is not null))
        {
            var (ns, ne) = BackfillWindowRule.Narrow(
                start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue),
                args.YearStart, args.YearEnd, today);
            start = DateOnly.FromDateTime(ns);
            end = DateOnly.FromDateTime(ne);
            if (start > end)
            {
                Report($"龙虎榜：指定的年份区间整段早于数据源起点 {provider.EarliestAvailable:yyyy-MM-dd}，"
                     + "这几年源上根本没有，跳过（不是漏抓）。");
                return (today, today, [], confirmed);
            }
            Report($"龙虎榜**整段回补**收窄到 {start:yyyy-MM-dd}~{end:yyyy-MM-dd}（按指定的年份区间）。");
        }
        var days = tradingDays.GetBetween(start, end).OrderBy(d => d).ToList();
        if (days.Count == 0)
        {
            Report("⚠ 本地交易日历在这一段里是空的，退回按工作日铺——会多发节假日的空请求，"
                 + "但不会漏抓。跑一次【交易日历】就能恢复。");
            for (var d = start; d <= end; d = d.AddDays(1))
                if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
        }

        if (args.MaxItems is { } max && provider is not ILhbRangeProvider && days.Count > max)
            days = days.Take(max).ToList();

        return (start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue), days, confirmed);
    }

    private static string Describe(FetchMode mode) => mode switch
    {
        FetchMode.FirstBackfill => "整段回补（数据源最早那天至今）",
        FetchMode.SpecificDay => "只抓指定的那一天（绕过\"确认没有\"名单）",
        _ => "增量（回看一个月补上榜后 N 日涨跌幅那几列）",
    };

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:F1} 秒";
}
