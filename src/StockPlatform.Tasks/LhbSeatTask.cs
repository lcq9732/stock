using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取龙虎榜席位】（2026-09-17 从编排器迁到新框架，同时改按日抓）。
/// 见 doc/lhb-seat-task-design.md。
///
/// ════ 为什么非改按日不可 ════
/// 原来按月切片抓，一片 40~60 页，而排序键 <c>TRADE_DATE,SECURITY_CODE</c> **不唯一**
/// （一天一只股有 5~10 行）。东财翻页靠排序定序，键不唯一时同键行在页与页之间的先后不保证，
/// **既会重复又会丢行**：2021-05 月片实测收到 6226 行里重复 14 行、同时丢掉 14 行真数据。
///
/// 重复的那一半还会被永久固化：主键末列 <c>seq</c> 是**位次**，一次抓取多收一行、整组编号就多
/// 一位，上次落库的高位 seq 行没人覆盖得掉。全表 178 万行里这样的副本有 3579 行，
/// 缺的真行一样多，年份铺满 2016~2026。
///
/// ⚠ <b>count 校验抓不住这个病</b>：实测 12 天里库中行数**全部等于**接口自报的 count，
/// 错的是内容（多一份副本、少一行真数据）。这跟大宗那轮不同，别拿"行数对上了"当验收判据。
///
/// 改法是**按日抓 + 整日替换**：5204 个"交易日 × 买卖侧"里 4941 个只有一页，页边界根本不存在；
/// 同样的 2021-05 逐日抓 18 天，收到 6226 行、重复 0 行，且正好含有月片漏掉的那 14 行。
/// 剩下 263 个多页的日侧靠排序键加长兜（见 <c>EastMoneyLhbSeatProvider</c> 的 SortColumns）。
///
/// ════ 一批＝一个交易日（买卖两侧） ════
/// 整日替换要求先收齐一整天，否则 DELETE 完只写进半天，留下的残缺事后完全看不出来。
/// 而这张表一天是**两个接口**，所以"收齐"是买卖两侧都跟各自的 count 对上
/// （<see cref="LhbSeatDay.IsComplete"/>）。只收到一侧就落库＝把另一侧整天删掉。
///
/// 于是 <c>MaxItems</c>＝本轮最多抓几天、<c>Deadline</c>＝到点收尾，两个都是骨架白送的——
/// 而 2600 天的历史回补（约 5540 个请求）最需要的正是这两件事。
/// </summary>
public sealed class LhbSeatTask(
    ILhbSeatRepository repository,
    ILhbSeatDayFetcher provider,
    ITradingDayRepository tradingDays,
    IManifestStore manifestStore) : FetchTaskBase<LhbSeatDay>
{
    public override FetchActionId Id => FetchActionId.FetchLhbSeat;

    /// <summary>东财这两张表最早到 2016-01-04，再往前查是空的。</summary>
    private static readonly DateTime Floor = new(2016, 1, 1);

    /// <summary>
    /// 增量往前回看的天数。
    ///
    /// 不像大宗那样取 30 天：龙虎榜席位**没有"事后才算出来"的滞后字段**——
    /// <c>rise_prob_3day</c> / <c>times_3day</c> 是那个营业部的滚动统计、每天都在变，
    /// 回看多少天都追不平。7 天只为兜住盘后陆续发布和交易所补录，代价 14 个请求。
    /// 整日替换是幂等的，这个数字随时能调大。
    /// </summary>
    private const int LookbackDays = 7;

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>抓全了的天（已落库）。</summary>
    private int _okDays;
    /// <summary>抓不全、跳过没落库的天——这些要记成残缺日待办。</summary>
    private readonly List<DateTime> _incomplete = [];
    /// <summary>接口自报 0 行的天（那天真的没人上榜）。</summary>
    private int _emptyDays;
    private int _rows;
    /// <summary>真发出过请求的天数——判"整轮全败"要用它，不能用计划的天数：
    /// 到点收尾或被取消时一天都没发，那不是失败。</summary>
    private int _attempted;
    private string? _skipped;
    /// <summary>本轮跑的是哪个模式——<see cref="OnStoppedAsync"/> 拿不到 args，
    /// 而"中断之后怎么接着走"恰恰是按模式分岔的。</summary>
    private FetchMode _mode;

    protected override async IAsyncEnumerable<IReadOnlyList<LhbSeatDay>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _incomplete.Clear();
        _okDays = _emptyDays = _rows = _attempted = 0;
        _skipped = null;
        _mode = args.Mode;
        _sw.Restart();

        // 定"抓哪些天"要查库（水位线、交易日历），都是同步 IO。骨架不替子类推到线程池，
        // 在首个 await 之前干这些会冻住界面。
        var days = await Task.Run(() => PlanDays(args), ct);
        if (days.Count == 0)
        {
            _skipped = args.Mode == FetchMode.FillBacklog
                ? "龙虎榜席位没有欠着的残缺日"
                : "龙虎榜席位已经抓到最新交易日了";
            Report($"{_skipped}，本轮不用抓。");
            yield break;
        }

        // 说"天"不说"交易日"：日历为空时退回按自然日铺，那批里是混着周末的。
        Report($"龙虎榜席位：{Describe(args.Mode)}，共 {days.Count} 天"
             + $"（{days[0]:yyyy-MM-dd} ~ {days[^1]:yyyy-MM-dd}），每天买卖两侧各 1~7 页...");

        int done = 0;
        foreach (var day in days)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Deadline is { } dl && DateTime.Now >= dl)
            {
                Report($"到收尾时间了，本轮抓到 {day.AddDays(-1):yyyy-MM-dd} 为止，剩下的下轮接着来。");
                yield break;
            }

            LhbSeatDay? one = null;
            _attempted++;
            try
            {
                one = await provider.FetchLhbSeatsOfDayAsync(day, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单天失败不拖垮整轮：把这天记成残缺日、继续往下抓。全轮都失败的话
                // 下面 OnCompletedAsync 会整项判失败。
                _incomplete.Add(day);
                var msg = $"龙虎榜席位 {day:yyyy-MM-dd} 抓取失败：{ex.Message}";
                Report($"⚠ {msg}");
                _errors.Add(msg);
            }

            done++;
            if (one == null) continue;

            // ⚠ 对不上就**不落库**：说明某页被限流截断了，拿残缺的一天去覆盖完整的一天，
            //   事后完全看不出来（行数判据只会觉得"那天本来就少"）。
            if (!one.IsComplete)
            {
                _incomplete.Add(day);
                var msg = $"龙虎榜席位 {day:yyyy-MM-dd} 只收到 买{one.Rows.Count(r => r.IsBuy)}/卖{one.Rows.Count(r => !r.IsBuy)} 行、"
                        + $"接口自报 买{one.ReportedBuy}/卖{one.ReportedSell} 行，"
                        + "多半是某页被限流截断——这一天不落库，记成残缺日下轮再来。";
                Report($"⚠ {msg}");
                _errors.Add(msg);
                continue;
            }

            // ⚠ 文本里**不要**再写一遍进度数字：TaskProgress.ToString() 已经会拼 "（done/total）"，
            // 写了就成 "（1/1）（1/1）"。
            if (done % 20 == 0 || done == days.Count)
                Report($"龙虎榜席位：{day:yyyy-MM-dd}", done, days.Count);

            yield return [one];
        }
    }

    /// <summary>
    /// 一批＝一个交易日：整天删了重写。空的那天不落库，只记一笔"那天真没有"。
    ///
    /// 走到这儿的批都已经过了 <c>IsComplete</c> 这一关（在 <see cref="FetchAsync"/> 里筛的）——
    /// 没抓全的天压根不会 yield 出来。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<LhbSeatDay> batch, CancellationToken ct)
    {
        foreach (var d in batch)
        {
            if (d.Rows.Count == 0) { _emptyDays++; continue; }
            _rows += repository.ReplaceForDay(d.Day, d.Rows);
            _okDays++;
        }
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        // ⚠ "下次从水位线接着走"只对**增量**成立。「整段回补」压根不看水位线——它每轮都从
        //   数据起点重新排期，所以中断＝**进度不保留**；而改用增量也接不上，那条只从水位线
        //   往前回看 7 天，够不着中间没跑到的那一大段。2026-09-17 这句话真的把人误导过一次。
        var resume = _mode == FetchMode.FirstBackfill
            ? "⚠ 但「整段回补」不看水位线，**进度不保留**：要补齐只能再跑一整遍"
              + "（已修好的天会被再抓一次，结果不变）；改用「增量」补不上——它只从水位线往前"
              + $"回看 {LookbackDays} 天，够不着中间没跑到的那段。"
            : "下次从水位线接着走即可。";
        Report($"龙虎榜席位中断。已落库的 {_okDays} 天是完整的（每天整日替换、各自一个事务）。{resume}");
        SaveIncompleteTodo();
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

        SaveIncompleteTodo();

        var summary = $"龙虎榜席位：{_okDays} 天写入 {_rows} 行"
                    + (_emptyDays > 0 ? $"，{_emptyDays} 天数据源本来就没有" : "")
                    + (_incomplete.Count > 0 ? $"，{_incomplete.Count} 天没抓全（已记进待办）" : "")
                    + $"，用时 {Fmt(_sw.Elapsed)}。本地共 {repository.Count()} 行。";
        Report(summary);

        // 发出去的每一天都没抓成 → 整项失败（多半是被限流或断网，不是"数据源没有"）。
        // ⚠ 判据是**发过请求的**天数，不是计划的天数：到点收尾时一天都没发，那不是失败。
        // 只要有一天成了就算完成：剩下的已经躺在待办里，下轮会来。
        if (_attempted > 0 && _okDays == 0 && _emptyDays == 0)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }

    // ── 抓哪些天 ──────────────────────────────────────────────────

    /// <summary>
    /// 按模式定这一轮抓哪些交易日。
    ///
    /// **一律走交易日历**（<see cref="ITradingDayRepository"/>）而不是逐个自然日试：
    /// 2016 年至今约 2600 个交易日，混着周末节假日就是 3900 天，多出来的全是空请求。
    /// 日历自己缺哪段就瞎哪段，所以拿不到日历时**不猜**——退回按自然日，宁可多发请求
    /// 也不静默漏抓（这个坑在 project_trading_calendar_pitfall 里栽过一次）。
    /// </summary>
    private List<DateTime> PlanDays(TaskRunArgs args)
    {
        var today = DateTime.Today;

        if (args.Mode == FetchMode.SpecificDay)
        {
            var d = args.Day?.ToDateTime(TimeOnly.MinValue) ?? today;
            return d.Date < Floor ? [] : [d.Date];
        }

        if (args.Mode == FetchMode.FillBacklog)
        {
            // 【只补待办】到不了这儿——本任务的 HandlesBacklog 是 false（2026-09-18 起按这个
            // 属性分派，见 IFetchTask.HandlesBacklog）：界面那一层和【重新拉取失败股票】都会把
            // 它截给 FetchOrchestrator.RunFillBacklogAsync，由那边统一编排各类待办
            // （席位只有"残缺日"一种，走 PartialDayRepair，按天重抓的动作共用
            // LhbSeatDayWriter）。这里返回空是兜底，不是主路径。
            return [];
        }

        DateTime start;
        if (args.Mode == FetchMode.FirstBackfill)
        {
            start = Floor;
        }
        else
        {
            var mark = repository.GetLatestTradeDate();
            // 没有水位线＝这张表还空着，按整段回补走——首次不该只抓 7 天。
            start = mark is { } m ? m.AddDays(-LookbackDays) : Floor;
            if (start < Floor) start = Floor;
        }

        var cal = tradingDays.GetBetween(DateOnly.FromDateTime(start), DateOnly.FromDateTime(today));
        var days = cal.Count > 0
            ? cal.Select(d => d.ToDateTime(TimeOnly.MinValue)).OrderBy(d => d).ToList()
            : AllDays(start, today);

        if (cal.Count == 0)
            Report("⚠ 本地交易日历在这一段里是空的，退回按自然日抓——会多发周末和节假日的空请求，"
                 + "但不会漏抓。跑一次【交易日历】就能恢复。");

        return args.MaxItems is { } max && days.Count > max ? days.Take(max).ToList() : days;
    }

    private static List<DateTime> AllDays(DateTime from, DateTime to)
    {
        var list = new List<DateTime>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1)) list.Add(d);
        return list;
    }

    /// <summary>
    /// 没抓全的天写进残缺日待办——<c>PartialDayRepair</c> 下轮会拿它们逐日重抓、
    /// 用体检同一套判据复查。已有的名单要**合并**不是覆盖：别的来源（日频体检）记的那些
    /// 还欠着呢。
    /// </summary>
    private void SaveIncompleteTodo()
    {
        if (_incomplete.Count == 0) return;
        var manifest = manifestStore.Load();
        var byDay = (manifest.Todo(RetryTaskIds.LhbSeat, RetryTodoKind.PartialDay)?.Targets ?? [])
            .Where(t => t.Day.HasValue)
            .ToDictionary(t => t.Day!.Value.Date, t => t);
        foreach (var d in _incomplete.Distinct())
            byDay.TryAdd(d.Date, new RetryTarget { Day = d.Date, Tries = 0 });
        manifest.SetTodo(RetryTaskIds.LhbSeat, RetryTodoKind.PartialDay,
                         byDay.Values.OrderBy(t => t.Day).ToList());
        manifestStore.Save(manifest);
    }

    private static string Describe(FetchMode mode) => mode switch
    {
        FetchMode.FirstBackfill => "整段回补 2016 年至今",
        FetchMode.SpecificDay => "只抓指定的那一天",
        _ => $"增量（水位线往前回看 {LookbackDays} 天）",
    };

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:F1} 秒";
}
