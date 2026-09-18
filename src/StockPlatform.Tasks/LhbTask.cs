using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
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
    IDailyFetchNoDataRepository? noDataRepository = null) : FetchTaskBase<LhbRow>
{
    public override FetchActionId Id => FetchActionId.StepLhb;

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

    private LhbDayWriter Writer => new(dbPath, repository);

    protected override async IAsyncEnumerable<IReadOnlyList<LhbRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _seen.Clear();
        _targets = [];
        _rows = _batches = _noData = 0;
        _skipped = null;
        _explicitDay = args.Mode == FetchMode.SpecificDay;
        _sw.Restart();

        // 定"抓哪一段"要查库（水位线、交易日历、空日名单），都是同步 IO。骨架不替子类推到
        // 线程池，在首个 await 之前干这些会冻住界面。
        var (start, end, targets, confirmed) = await Task.Run(() => Plan(args), ct);
        if (targets.Count == 0)
        {
            _skipped = args.Mode == FetchMode.FillBacklog
                ? "龙虎榜没有欠着的残缺日"
                : "龙虎榜这一段里没有交易日，本轮不用抓";
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
            if (done % 10 == 0 || done == targets.Count)
                Report($"龙虎榜：{d:yyyy-MM-dd}", done, targets.Count);
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
        Report($"龙虎榜中断。已落库的 {_batches} 批是完整的（各自整天替换、各自一个事务），"
             + "下次从水位线接着走即可。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

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
            // 【只补待办】到不了这儿——本任务的 HandlesBacklog 是 false（2026-09-18 起按这个
            // 属性分派，见 IFetchTask.HandlesBacklog）：界面那一层和【重新拉取失败股票】都会把
            // 它截给 FetchOrchestrator.RunFillBacklogAsync（残缺日走 PartialDayRepair，
            // 按天重抓的动作共用 LhbDayWriter）。这里返回空是兜底，不是主路径。
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
