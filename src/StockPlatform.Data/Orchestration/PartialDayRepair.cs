namespace StockPlatform.Data.Orchestration;

using System.Diagnostics;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

/// <summary>
/// 补**残缺日**的编排（2026-09-17 从 <c>FetchOrchestrator.FillPartialDaysAsync</c> 抽出来）。
/// 残缺日＝那天有行、但不全（某个交易所整天没有，或行数明显偏少），见 doc/partial-day-repair-design.md。
///
/// ════ 为什么抽出来 ════
/// 判据（<see cref="SqliteDailyTableAuditor.DailyTables"/> 的 spec、
/// <see cref="SqliteDailyTableAuditor.CheckDays"/>）本来就已经是共用的，只有**编排**还锁在
/// 编排器里。而这段编排里没有一行是任务特定的——唯一的变量是"怎么重抓一天"那个委托。
///
/// 抽出来之后，老任务（走 <c>DailyRefetcherFor</c>）和新框架的任务
/// （<c>FetchTaskBase</c> 子类，<c>FillBacklog</c> 模式）共用同一份，
/// 不会出现"体检说齐了、重试那边还挂着单子"的分叉——跟
/// <c>DayCompletenessTask</c> 迁移时把判据和落账都收进
/// <see cref="SqliteDayCompletenessAuditor"/> 是同一条理由。
///
/// ⚠ **复查绝对不能用 <c>COUNT > 0</c>**：残缺日本来就有行（2026-08-21 有 1,998 行沪市），
/// 拿"有没有行"去复查，补没补上都会被判成"已补齐"、从待办里静默划掉。所以复查走
/// <see cref="SqliteDailyTableAuditor.CheckDays"/>——跟体检**同一套判据**，补齐了才划掉。
/// </summary>
/// <param name="dbPath">当前库路径（复查要查它）。</param>
/// <param name="manifestStore">待办和"确认就这些"名单都记在 manifest 里。</param>
/// <param name="manifestLock">
/// manifest 读改写的互斥锁，**可选**。编排器有自己的 <c>_dbLock</c>，传进来才是同一把、
/// 才拦得住并发；新框架的任务没有（<c>DayCompletenessTask</c> 也是裸的 Load→Apply→Save），
/// 传 null 即可。
/// </param>
public sealed class PartialDayRepair(
    string dbPath,
    IManifestStore manifestStore,
    object? manifestLock = null)
{
    /// <summary>补两轮还是不齐，就判定"数据源那天就是只有这些"，写进白名单、以后体检跳过。</summary>
    public const int MaxTries = 2;

    /// <summary>
    /// 把某一项欠着的残缺日补一遍。
    /// </summary>
    /// <param name="taskId">归属任务（<c>FetchActionId</c> 的枚举名）。</param>
    /// <param name="refetch">
    /// 重抓一整天、返回写入行数。null＝这一项没有"按天重抓"的入口（只能人工处理，会报一句）。
    /// </param>
    /// <returns>没活干（这一项没进日频体检、或没有待办）时返回 null。</returns>
    public async Task<PartialDayRepairResult?> RunAsync(
        string taskId,
        Func<DateOnly, Task<int>>? refetch,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var spec = SqliteDailyTableAuditor.DailyTables.FirstOrDefault(s => s.OwnerTaskId == taskId);
        if (spec == null) return null;

        List<RetryTarget> pending = Locked(() =>
            manifestStore.Load().Todo(taskId, RetryTodoKind.PartialDay)?.Targets.ToList() ?? new());
        var days = pending.Where(p => p.Day.HasValue).Select(p => p.Day!.Value.Date)
                          .Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0) return null;

        if (refetch == null)
        {
            progress?.Report($"⚠ {spec.Label}：有 {days.Count} 天残缺，但这一项没有\"按天重抓\"的入口，只能人工处理。");
            return new PartialDayRepairResult(spec.Label, days.Count, 0, 0, 0, 0, NoRefetcher: true, Done: null);
        }

        var sw = Stopwatch.StartNew();
        progress?.Report($"补{spec.Label}残缺日：{days.Count} 天"
            + $"（{string.Join("、", days.Select(d => d.ToString("yyyy-MM-dd")))}）"
            + "——这些天本地有数据但不全，**整天重抓**。怎么落库由各项自己定："
            + "两融走主键去重合并（已有的行不动），龙虎榜/席位/大宗是整日替换（那天删了重写，"
            + "所以抓不全时它们宁可整天不落库）。");

        int rows = 0, failCount = 0;
        foreach (var d in days)
        {
            ct.ThrowIfCancellationRequested();
            try { rows += await refetch(DateOnly.FromDateTime(d)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failCount++;
                progress?.Report($"⚠ {spec.Label} {d:yyyy-MM-dd} 重抓失败：{ex.Message}");
            }
        }

        // 整批都失败多半是被限流/断网，不是"数据源没有"——名单和计数原样留着，别白耗一轮 Tries。
        return Finish(spec, taskId, days, pending, rows, failCount,
                      throttled: failCount == days.Count, sw, progress);
    }

    /// <summary>
    /// **一次补掉所有欠着的天**（2026-09-18）——给"按天重抓很贵、但一轮能覆盖所有天"的项用。
    ///
    /// 资金净流入就是这样：数据源一次请求返回整只票的**全部历史**，窗口在客户端裁，
    /// 所以"补一天"和"补十天"都是同一轮全市场逐只抓（约 1.75 小时）。
    /// 拿 <see cref="RunAsync"/> 那条逐天路径去跑，十天就是十轮——纯浪费。
    ///
    /// 复查、Tries、"确认就这些"名单跟逐天那条**完全共用**（见 <see cref="Finish"/>）：
    /// 这些才是最不能各写一份的部分。
    /// </summary>
    /// <param name="refetchAll">
    /// 一次把这些天全补上，返回 (写入行数, 是否判定被限流)。
    /// **限流那个信号由调用方给**——判据因项而异（资金流是"过半只数失败"），
    /// 但后果一样：一个 Tries 都不加，名单原样留着。
    /// </param>
    public async Task<PartialDayRepairResult?> RunBatchAsync(
        string taskId,
        Func<IReadOnlyList<DateTime>, Task<(int Rows, bool Throttled)>> refetchAll,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var spec = SqliteDailyTableAuditor.DailyTables.FirstOrDefault(s => s.OwnerTaskId == taskId);
        if (spec == null) return null;

        List<RetryTarget> pending = Locked(() =>
            manifestStore.Load().Todo(taskId, RetryTodoKind.PartialDay)?.Targets.ToList() ?? new());
        var days = pending.Where(p => p.Day.HasValue).Select(p => p.Day!.Value.Date)
                          .Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0) return null;

        var sw = Stopwatch.StartNew();
        progress?.Report($"补{spec.Label}残缺日：{days.Count} 天"
            + $"（{string.Join("、", days.Select(d => d.ToString("yyyy-MM-dd")))}）"
            + "——这些天本地有数据但不全。这一项按天重抓很贵，所以一轮把这些天一起补。");

        int rows; bool throttled;
        try { (rows, throttled) = await refetchAll(days); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress?.Report($"⚠ {spec.Label}残缺日重抓失败：{ex.Message}——名单和重试计数原样留着。");
            return new PartialDayRepairResult(
                spec.Label, days.Count, 0, 0, days.Count, 0,
                NoRefetcher: false, Done: $"{spec.Label}残缺 {days.Count} 天（未计数）");
        }

        return Finish(spec, taskId, days, pending, rows, throttled ? days.Count : 0, throttled, sw, progress);
    }

    /// <summary>
    /// 重抓之后的收尾：复查 → Tries → "确认就这些"名单 → 汇总。两条重抓路径共用这一份。
    ///
    /// ⚠ **复查绝不能退化成 <c>COUNT &gt; 0</c>**：残缺日本来就有行，拿"有没有行"去复查，
    /// 补没补上都会被判成"已补齐"、从待办里静默划掉。所以走
    /// <see cref="SqliteDailyTableAuditor.CheckDays"/>——跟体检同一套判据。
    /// </summary>
    private PartialDayRepairResult Finish(
        SqliteDailyTableAuditor.Spec spec, string taskId, List<DateTime> days,
        List<RetryTarget> pending, int rows, int failCount, bool throttled,
        Stopwatch sw, IProgress<string>? progress)
    {
        if (throttled)
        {
            progress?.Report($"{spec.Label}残缺日：这一轮全都没补成，判定是连不上/被限流而不是数据源没有，"
                           + "名单和重试计数原样留着，等会儿再跑一次。");
            return new PartialDayRepairResult(
                spec.Label, days.Count, 0, rows, failCount, 0,
                NoRefetcher: false, Done: $"{spec.Label}残缺 {days.Count} 天（未计数）");
        }

        // 复查：跟体检同一套判据（不是 COUNT>0，理由见上面的 ⚠）
        var auditor = new SqliteDailyTableAuditor(dbPath);
        var stillBad = auditor.CheckDays(spec, MarketIndexCatalog.ShanghaiCompositeSymbol, days)
                              .Select(p => p.Day.Date).ToHashSet();
        int fixedDays = days.Count - stillBad.Count;

        int confirmedNow = 0;
        Locked(() =>
        {
            var manifest = manifestStore.Load();
            var confirmed = manifest.ConfirmedPartialDays.TryGetValue(taskId, out var cd)
                ? cd.Select(x => x.Date).ToHashSet() : new HashSet<DateTime>();
            var triesByDay = pending.Where(p => p.Day.HasValue)
                                    .GroupBy(p => p.Day!.Value.Date)
                                    .ToDictionary(g => g.Key, g => g.Max(p => p.Tries));
            var next = new List<RetryTarget>();
            foreach (var d in stillBad.OrderBy(x => x))
            {
                int tries = triesByDay.GetValueOrDefault(d) + 1;
                if (tries >= MaxTries) { confirmed.Add(d); confirmedNow++; }
                else next.Add(new RetryTarget { Day = d, Tries = tries });
            }
            manifest.SetTodo(taskId, RetryTodoKind.PartialDay, next);
            manifest.ConfirmedPartialDays[taskId] = confirmed.OrderBy(x => x).ToList();
            manifestStore.Save(manifest);
            return 0;
        });

        progress?.Report($"{spec.Label}残缺日补齐完成：补上 {fixedDays}/{days.Count} 天、写入 {rows} 行"
            + (failCount > 0 ? $"，{failCount} 天重抓失败" : "")
            + (confirmedNow > 0
                ? $"；{confirmedNow} 天补满 {MaxTries} 轮仍不齐，已判定数据源那天就是只有这些、以后体检不再报"
                : "")
            + $"，用时 {ElapsedText.Format(sw.Elapsed)}。");

        return new PartialDayRepairResult(
            spec.Label, days.Count, fixedDays, rows, failCount, confirmedNow,
            NoRefetcher: false, Done: $"{spec.Label}残缺 {days.Count} 天");
    }

    /// <summary>某一项已知的残缺日——整段回补要拿它从 have 里扣掉，否则那些天会被当成"已有"永远跳过。</summary>
    public HashSet<DateOnly> DaysOf(string taskId) => Locked(() =>
        (manifestStore.Load().Todo(taskId, RetryTodoKind.PartialDay)?.Targets ?? [])
            .Where(t => t.Day.HasValue)
            .Select(t => DateOnly.FromDateTime(t.Day!.Value.Date))
            .ToHashSet());

    private T Locked<T>(Func<T> f)
    {
        if (manifestLock == null) return f();
        lock (manifestLock) return f();
    }
}

/// <summary>
/// 一轮残缺日补齐的结果。
/// </summary>
/// <param name="Label">这张表的显示名（"大宗交易(东财)"…），报信息用。</param>
/// <param name="Days">这一轮处理了几天。</param>
/// <param name="Fixed">补齐了几天。</param>
/// <param name="Rows">重抓写入了多少行。</param>
/// <param name="Failed">重抓失败几天。</param>
/// <param name="ConfirmedNow">本轮满 <see cref="PartialDayRepair.MaxTries"/> 判定"数据源就这些"的天数。</param>
/// <param name="NoRefetcher">这一项没有"按天重抓"的入口——什么都没做。</param>
/// <param name="Done">给汇总列表用的一句话；null＝不记一笔。</param>
public sealed record PartialDayRepairResult(
    string Label,
    int Days,
    int Fixed,
    int Rows,
    int Failed,
    int ConfirmedNow,
    bool NoRefetcher,
    string? Done);
