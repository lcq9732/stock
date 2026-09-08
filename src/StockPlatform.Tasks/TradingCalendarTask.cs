using System.Runtime.CompilerServices;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>日历里的一天：哪天 + 这个结论是哪来的。</summary>
public sealed record TradingDayEntry(DateOnly Day, string Source);

/// <summary>
/// 【交易日历】（2026-09-08）——**第一个按新形状写的任务**（见 <see cref="IFetchTask"/> 的类注释）。
///
/// ════ 它解决什么 ════
/// 在此之前全库判交易日有两种土办法：逐日回补只跳周末（每个节假日每轮都白发一次请求——龙虎榜
/// 从 2002 年补一轮就是几百个），或者临时从 Bar 表 DISTINCT 归纳（23GB 库扫几十秒，每个任务
/// 起手扫一遍）。这一项把日历落进 TradingDay 表，往后按交易日取数的地方直接读表。
///
/// ════ 两个来源，按年份分工 ════
///   · 2005-01 起　　＝ 深交所官网（<c>SzseTradingCalendarProvider</c>），一月一个请求；
///   · 2004-12 及以前 ＝ 本地全市场K线归纳（深交所接口对那段返回空，实测逐月二分确认）。
///     这段是**死历史、永不再变**，建一次就固定，之后每轮增量都不会再扫 Bar。
///
/// ════ 每轮发几个请求 ════
/// 首次：264 个（2005-01 ~ 2026-12）＋ 一次全库 DISTINCT 扫描。
/// 日常：2 个（本月 + 下月）。11 月起改成一路拉到次年 12 月（约 14 个），因为交易所年底才发布
/// 下一年的日历——实测 2026-09-08 时 2027 全年还是空的，拉到空不算错。
/// </summary>
public sealed class TradingCalendarTask(
    ITradingDayRepository repository,
    ITradingCalendarProvider provider,
    ILocalTradingDaySource localSource) : FetchTaskBase<TradingDayEntry>
{
    /// <summary>官方源给得到的最早月份；这天之前的日子只能靠本地归纳。</summary>
    private DateOnly SzseStart => provider.EarliestMonth;

    public override FetchActionId Id => FetchActionId.StepTradingCalendar;

    /// <summary>归纳那一步顺手留下的全市场K线日期，只在**这一轮真的归纳过**时非空，供收尾对账。</summary>
    private List<DateTime>? _localSnapshot;

    protected override async IAsyncEnumerable<IReadOnlyList<TradingDayEntry>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            var (min, max) = repository.GetRange();
            bool rebuild = args.Mode == FetchMode.FirstBackfill;

            // ── ① 2004 及以前那段：只在**表还空着**或**要求重建**时归纳，日常增量一次都不扫 ──
            //    判据不能用"日历最早那天还在深交所起点之后"（第一版是这么写的）：本地K线本来就
            //    没有 2005 年以前那段时，归纳出来是空的、表里最早的还是 2005，于是**每轮都会重扫
            //    一遍全库**——23GB 上几分钟，纯浪费。2026-09-08 在 Debug 库上就是这个表现。
            //    代价是：日后补了早年K线，得手动用「首次整段回补」跑一次才会补进日历（目录说明里写了）。
            bool needLocal = rebuild || min == null;
            if (!needLocal && DateOnly.FromDateTime(min!.Value) >= SzseStart)
                Report("日历里 2005 年以前那段是空的（本地当时没有那么早的K线）——" +
                       "补过历史K线之后，用「首次整段回补」模式跑一次这一项就能补上。");
            if (needLocal)
            {
                Report("归纳 2004 年及以前的交易日（扫全市场日K，23GB 库上要几十秒，只此一次）...",
                       phase: "本地归纳");
                var days = localSource.GetDistinctDays();
                _localSnapshot = days;
                var early = days.Select(DateOnly.FromDateTime)
                                .Where(d => d < SzseStart)
                                .Distinct().OrderBy(d => d)
                                .Select(d => new TradingDayEntry(d, ITradingDayRepository.LocalSource))
                                .ToList();
                if (early.Count > 0)
                {
                    Report($"本地归纳出 {early.Count} 个交易日（{early[0].Day:yyyy-MM-dd} ~ {early[^1].Day:yyyy-MM-dd}）",
                           phase: "本地归纳");
                    yield return early;
                }
                else
                {
                    Report("本地没有 2005 年以前的K线，早年那段日历暂时空着——" +
                           "补过历史K线之后用「首次整段回补」模式再跑一次这一项即可。", phase: "本地归纳");
                }
            }

            // ── ② 2005-01 起：深交所官方，按月 ──
            //    起点回退到"日历最大日所在月的 1 号"整月重抓：当月是边长边拉的，
            //    停在月中的话那个月剩下的日子就再也补不上了（跟龙虎榜席位按月切片同一个道理）。
            var today = DateOnly.FromDateTime(DateTime.Today);
            DateOnly start;
            if (rebuild || max == null) start = SzseStart;
            else
            {
                var maxDay = DateOnly.FromDateTime(max.Value);
                // 取"日历最大日所在月"和"本月"里靠前的那个：一旦拉到了未来月份（11 月起会拉下一年），
                // 日历最大日就跑到今天前面去了，只按它算起点的话**当月再也不会重拉**——
                // 而交易所偶尔会调整当年的休市安排，当月每天刷一次才跟得上。
                var byMax = maxDay < SzseStart ? SzseStart : MonthStart(maxDay);
                var byToday = MonthStart(today);
                start = byMax < byToday ? byMax : byToday;
                if (start < SzseStart) start = SzseStart;
            }
            // 终点：正常是下个月；11 月起一路拉到次年 12 月，把下一年的日历一次性收进来
            var end = today.Month >= 11 ? new DateOnly(today.Year + 1, 12, 1) : MonthStart(today).AddMonths(1);

            int months = (end.Year - start.Year) * 12 + end.Month - start.Month + 1;
            Report($"拉取深交所交易日历 {start:yyyy-MM} ~ {end:yyyy-MM}（{months} 个月）", 0, months, "官方日历");

            int done = 0;
            for (var m = start; m <= end; m = m.AddMonths(1))
            {
                ct.ThrowIfCancellationRequested();
                var days = await provider.GetMonthAsync(m.Year, m.Month, ct);
                done++;
                if (days.Count == 0)
                {
                    // 空月＝交易所还没发布（或早于覆盖起点），不是错误
                    Report($"{m:yyyy-MM}：交易所还没有这个月的日历", done, months, "官方日历");
                    continue;
                }
                Report($"{m:yyyy-MM}：{days.Count} 个交易日", done, months, "官方日历");
                yield return days.Select(d => new TradingDayEntry(d, ITradingDayRepository.SzseSource)).ToList();
            }
        }
        finally { provider.OnStatus -= Forward; }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<TradingDayEntry> batch, CancellationToken ct)
    {
        repository.Upsert(batch.Select(e => (e.Day, e.Source)));
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var (min, max) = repository.GetRange();
        int total = repository.Count();
        var summary = min == null
            ? "交易日历还是空的"
            : $"交易日历 {min:yyyy-MM-dd} ~ {max:yyyy-MM-dd} 共 {total} 天" +
              $"（官方 {repository.Count(ITradingDayRepository.SzseSource)}、" +
              $"归纳 {repository.Count(ITradingDayRepository.LocalSource)}）";

        // 只有这一轮真的归纳过（首次/重建）才有本地快照，顺手做一次对账；日常增量不做，免得扫库
        if (_localSnapshot != null) Reconcile(_localSnapshot);

        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }

    /// <summary>
    /// 官方日历 vs 本地K线归纳，在 2005 年起的重叠区间逐日比对——换数据源要先证明等价，这是项目规矩。
    ///
    /// 两边不一致本身就有价值，不是噪音：
    ///   · 官方说是交易日、本地全市场一根K线都没有 → **本地那天整天漏抓**，正好是白捡的体检信号；
    ///   · 本地有K线、官方说不是交易日 → 更蹊跷（多半是本地某只票的日期串了），值得单看。
    /// </summary>
    private void Reconcile(List<DateTime> localDays)
    {
        var official = repository.GetBetween(SzseStart, DateOnly.FromDateTime(DateTime.Today));
        if (official.Count == 0) return;

        var local = localDays.Select(DateOnly.FromDateTime).Where(d => d >= SzseStart).ToHashSet();
        // 本地那一侧是空的就**没法对账**，绝不能报"通过"——2026-09-08 首跑时就撞见过：Debug 库里
        // 一根K线都没有，两边都空于是判成"逐日一致"，日志上看像验过了，其实什么都没验。
        if (local.Count == 0)
        {
            Report("本地没有 2005 年以后的K线，这一轮无从对账（先跑一次【个股日K·前复权】再来）", phase: "对账");
            return;
        }
        // 只比对两边都覆盖得到的那一段，否则"本地K线还没抓到今天"会被算成一堆差异
        var upper = local.Max();

        var missingLocally = official.Where(d => d <= upper && !local.Contains(d)).OrderBy(d => d).ToList();
        var extraLocally = local.Where(d => !official.Contains(d)).OrderBy(d => d).ToList();

        if (missingLocally.Count == 0 && extraLocally.Count == 0)
        {
            Report($"对账通过：{SzseStart:yyyy-MM-dd}~{upper:yyyy-MM-dd} 官方日历与本地K线逐日一致（{official.Count} 天）",
                   phase: "对账");
            return;
        }
        if (missingLocally.Count > 0)
            Report($"⚠ 对账：{missingLocally.Count} 个官方交易日本地一根K线都没有（整天漏抓），" +
                   $"最早几天：{string.Join("、", missingLocally.Take(5).Select(d => d.ToString("yyyy-MM-dd")))}",
                   phase: "对账");
        if (extraLocally.Count > 0)
            Report($"⚠ 对账：{extraLocally.Count} 天本地有K线但官方日历里不是交易日，" +
                   $"最早几天：{string.Join("、", extraLocally.Take(5).Select(d => d.ToString("yyyy-MM-dd")))}",
                   phase: "对账");
    }

    private static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);
}
