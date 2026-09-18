using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【大宗交易】（2026-09-17 从【拉取市场事件】拆出来）。见 doc/block-trade-task-design.md。
///
/// ════ 为什么拆 ════
/// 原来四张表（大宗/调研/解禁/增减持）挤在一项里，因为它们都是 datacenter 报表、形状同构。
/// 但大宗改成**按交易日**抓之后片数从十几片变成两千多片，绑在一起会让另外三张陪着跑完
/// 整个历史回补。而体检层面它们本来就已经分开了——日频残缺日体检只认大宗。
///
/// ════ 为什么非改按日不可 ════
/// 主键是 <c>(trade_date, code, daily_rank)</c>，第三列原来存东财的 <c>DAILY_RANK</c>。
/// 那个值**跨抓取不稳定**：同一笔交易 2026-09-15 抓到 rank 22、09-16 再抓变成 rank 1，
/// 于是 UPSERT 认不出"同一笔"，每次重抓都 INSERT 一份副本。叠加"增量回看 30 天补滞后字段"，
/// 最近一个月每跑一轮就复制一批——实测 2026-09-08 库里 20.3 亿、真值只有 9.8 亿。
///
/// 改法是**按日抓 + 整日替换 + count 校验**：单日 100~600 笔、一页 500 行，永远 1~2 页；
/// 抓完拿接口自报的 <c>count</c> 核对，对得上才删掉那天重写。于是反复跑多少次结果都一样。
///
/// ════ 一批＝一个交易日 ════
/// 整日替换要求先收齐一整天，否则 DELETE 完只写进半天，留下的残缺事后完全看不出来
/// （跟 <see cref="MoneyFlowSnapshotTask"/> 的"一批＝一整天"是同一条理由）。
/// 于是 <c>MaxItems</c>＝本轮最多抓几天、<c>Deadline</c>＝到点收尾，两个都是白送的——
/// 而这正是 2600 天的历史回补最需要的两件事。
///
/// ⚠ 抓不全的那一天**不落库**，记成残缺日待办下轮再来。拿半天的数据覆盖完整的一天，
/// 是这张表能犯的最坏的错：库里看不出，体检也看不出（行数判据只会觉得"那天本来就少"）。
/// </summary>
public sealed class BlockTradeTask(
    IMarketEventRepository repository,
    IBlockTradeDayFetcher provider,
    ITradingDayRepository tradingDays,
    IManifestStore manifestStore,
    FetchPaths paths) : FetchTaskBase<BlockTradeDay>
{
    public override FetchActionId Id => FetchActionId.FetchBlockTrade;

    /// <summary>
    /// 残缺日待办自己补（2026-09-18 收口，见 doc/fill-backlog-to-tasks-design.md）。
    ///
    /// 09-17 迁移时这一条还是 false——待办编排留在 <c>FetchOrchestrator</c> 那边。
    /// 能搬过来的前提是**大宗的待办只有"残缺日"这一类**（体检的 OwnerTaskId 和
    /// <c>SaveIncompleteTodo</c> 是仅有的两个来源）：转交之后 orchestrator 那条路
    /// 对本项不再跑，真有别的类别就会**静默补不上**。这个前提由
    /// <c>RetryDispatchTests</c> 的守卫钉着。
    /// </summary>
    public override bool HandlesBacklog => true;

    /// <summary>东财这张表最早到 2016-01-04，再往前查是空的。</summary>
    private static readonly DateTime Floor = new(2016, 1, 1);

    /// <summary>
    /// 增量往前回看的天数——为**滞后字段**：<c>change_rate_1d/5d/10d/20d</c> 是东财事后才算的，
    /// 抓取当天窗口没走完一律返回 null。最长 20 个交易日 ≈ 28 自然日，取 30 天足够覆盖。
    /// 整日替换是幂等的，回看多少天都不会产生重复行——这正是改造之前做不到的事。
    /// </summary>
    private const int LookbackDays = 30;

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];

    /// <summary>抓全了的天（已落库）。</summary>
    private int _okDays;
    /// <summary>抓不全、跳过没落库的天——这些要记成残缺日待办。</summary>
    private readonly List<DateTime> _incomplete = [];
    /// <summary>接口自报 0 行的天（那天真的没有大宗交易）。</summary>
    private int _emptyDays;
    private int _rows;
    /// <summary>真发出过请求的天数——判"整轮全败"要用它，不能用计划的天数：
    /// 到点收尾或被取消时一天都没发，那不是失败。</summary>
    private int _attempted;
    private string? _skipped;
    /// <summary>本轮跑的是哪个模式——<see cref="OnStoppedAsync"/> 拿不到 args，
    /// 而"中断之后怎么接着走"恰恰是按模式分岔的。</summary>
    private FetchMode _mode;

    /// <summary>【只补待办】那一路的结果。null＝这轮不是补待办、或没有欠着的天。</summary>
    private PartialDayRepairResult? _backlog;

    protected override async IAsyncEnumerable<IReadOnlyList<BlockTradeDay>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear();
        _incomplete.Clear();
        _okDays = _emptyDays = _rows = _attempted = 0;
        _skipped = null;
        _backlog = null;
        _mode = args.Mode;
        _sw.Restart();

        // ── 【只补待办】走完全不同的一条路：目标从待办清单来，不从水位线来 ──
        // 编排整个在 PartialDayRepair 里（逐日重抓 → 用体检同一套判据复查 → Tries →
        // 满 MaxTries 判定"数据源那天就这些"）。**不要**把残缺日当普通的天塞进 PlanDays
        // 走下面的流式路径：那等于把复查判据和 Tries 抄一遍，正是抽出那个类要避免的事。
        //
        // 它自己一天一天循环、每天开头检查取消，所以停止停在**天的边界**上，
        // 整日替换的事务不会被腰斩。MaxItems/Deadline 进不去它——残缺日是个位数天，
        // 真需要的话给 PartialDayRepair 加可选参数，别在这里另写循环。
        if (args.Mode == FetchMode.FillBacklog)
        {
            // 包 Task.Run：它开头读 manifest、查体检 spec 都是同步 IO，骨架不替子类推线程池。
            _backlog = await Task.Run(() => new PartialDayRepair(paths.CurrentDb, manifestStore)
                .RunAsync(RetryTaskIds.BlockTrade,
                          d => new BlockTradeDayWriter(provider, repository).RefetchAsync(d, ct),
                          ProgressSink, ct), ct);
            yield break;
        }

        // 定"抓哪些天"要查库（水位线、交易日历、待办），都是同步 IO。骨架不替子类推到线程池，
        // 在首个 await 之前干这些会冻住界面。
        var days = await Task.Run(() => PlanDays(args), ct);
        if (days.Count == 0)
        {
            _skipped = "大宗交易已经抓到最新交易日了";
            Report($"{_skipped}，本轮不用抓。");
            yield break;
        }

        // 说"天"不说"交易日"：日历为空时退回按自然日铺，那批里是混着周末的。
        Report($"大宗交易：{Describe(args.Mode)}，共 {days.Count} 天"
             + $"（{days[0]:yyyy-MM-dd} ~ {days[^1]:yyyy-MM-dd}），每天 1~2 个请求...");

        int done = 0;
        foreach (var day in days)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Deadline is { } dl && DateTime.Now >= dl)
            {
                Report($"到收尾时间了，本轮抓到 {day.AddDays(-1):yyyy-MM-dd} 为止，剩下的下轮接着来。");
                yield break;
            }

            BlockTradeDay? one = null;
            _attempted++;
            try
            {
                one = await provider.FetchBlockTradesOfDayAsync(day, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单天失败不拖垮整轮：把这天记成残缺日、继续往下抓。全轮都失败的话
                // 下面 OnCompletedAsync 会整项判失败。
                _incomplete.Add(day);
                var msg = $"大宗交易 {day:yyyy-MM-dd} 抓取失败：{ex.Message}";
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
                var msg = $"大宗交易 {day:yyyy-MM-dd} 只收到 {one.Rows.Count} 行、接口自报 {one.ReportedCount} 行，"
                        + "多半是某页被限流截断——这一天不落库，记成残缺日下轮再来。";
                Report($"⚠ {msg}");
                _errors.Add(msg);
                continue;
            }

            // ⚠ 文本里**不要**再写一遍进度数字：TaskProgress.ToString() 已经会拼 "（done/total）"，
            // 写了就成 "（1/1）（1/1）"。
            if (done % 20 == 0 || done == days.Count)
                Report($"大宗交易：{day:yyyy-MM-dd}", done, days.Count);

            yield return [one];
        }
    }

    /// <summary>
    /// 一批＝一个交易日：整天删了重写。空的那天不落库，只记一笔"那天真没有"。
    ///
    /// 走到这儿的批都已经过了 <c>IsComplete</c> 这一关（在 <see cref="FetchAsync"/> 里筛的）——
    /// 没抓全的天压根不会 yield 出来。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<BlockTradeDay> batch, CancellationToken ct)
    {
        foreach (var d in batch)
        {
            if (d.Rows.Count == 0) { _emptyDays++; continue; }
            _rows += repository.ReplaceBlockTradesForDay(d.Day, d.Rows);
            _okDays++;
        }
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        // 【只补待办】中断：待办清单本身就是进度，补齐的那些天已经在复查时划掉了
        // （复查发生在每一轮 PartialDayRepair 的末尾——中途停的话这一轮一天都没划，
        // 下轮原样重来，整日替换是幂等的）。跟水位线无关，所以不套下面那套话。
        if (_mode == FetchMode.FillBacklog)
        {
            Report("大宗交易·补待办中断。已重抓的天都是整日替换、各自一个事务，"
                 + "没补完的仍在待办里，下次再点一次即可。");
            return Task.CompletedTask;
        }

        // ⚠ "下次从水位线接着走"只对**增量**成立。「整段回补」压根不看水位线——它每轮都从
        //   数据起点重新排期，所以中断＝**进度不保留**；而改用增量也接不上，那条只从水位线
        //   往前回看 30 天，够不着中间没跑到的那一大段。2026-09-17 这句话真的把人误导过一次。
        var resume = _mode == FetchMode.FirstBackfill
            ? "⚠ 但「整段回补」不看水位线，**进度不保留**：要补齐只能再跑一整遍"
              + "（已修好的天会被再抓一次，结果不变）；改用「增量」补不上——它只从水位线往前"
              + $"回看 {LookbackDays} 天，够不着中间没跑到的那段。"
            : "下次从水位线接着走即可。";
        Report($"大宗交易中断。已落库的 {_okDays} 天是完整的（每天整日替换、各自一个事务）。{resume}");
        SaveIncompleteTodo();
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 【只补待办】那一路：一行数据都不经过 SaveBatchAsync（编排和落库都在
        // PartialDayRepair 里），所以结局要单独翻译——走下面那套会报"0 天写入 0 行"。
        if (args.Mode == FetchMode.FillBacklog)
        {
            if (_backlog is not { } r)
                return Task.FromResult<TaskRunResult?>(
                    TaskRunResult.Skipped("大宗交易没有欠着的残缺日", _errors));

            var line = $"大宗交易残缺日：{r.Days} 天里补上 {r.Fixed} 天、写入 {r.Rows} 行"
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

        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

        SaveIncompleteTodo();

        var summary = $"大宗交易：{_okDays} 天写入 {_rows} 行"
                    + (_emptyDays > 0 ? $"，{_emptyDays} 天数据源本来就没有" : "")
                    + (_incomplete.Count > 0 ? $"，{_incomplete.Count} 天没抓全（已记进待办）" : "")
                    + $"，用时 {Fmt(_sw.Elapsed)}。本地共 {repository.Count("BlockTrade")} 行。";
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

        // 【只补待办】不走这儿——它在 FetchAsync 开头就分流掉了（目标从待办清单来，
        // 不从水位线来）。这里只管"按水位线/起点排期"那一类模式。

        DateTime start;
        if (args.Mode == FetchMode.FirstBackfill)
        {
            start = Floor;
        }
        else
        {
            var mark = repository.GetLatestDate("BlockTrade", "trade_date");
            // 没有水位线＝这张表还空着，按整段回补走——首次不该只抓 30 天。
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
    /// 没抓全的天写进残缺日待办——<see cref="PartialDayRepair"/> 下轮会拿它们逐日重抓、
    /// 用体检同一套判据复查。已有的名单要**合并**不是覆盖：别的来源（日频体检）记的那些
    /// 还欠着呢。
    /// </summary>
    private void SaveIncompleteTodo()
    {
        if (_incomplete.Count == 0) return;
        var manifest = manifestStore.Load();
        var byDay = (manifest.Todo(RetryTaskIds.BlockTrade, RetryTodoKind.PartialDay)?.Targets ?? [])
            .Where(t => t.Day.HasValue)
            .ToDictionary(t => t.Day!.Value.Date, t => t);
        foreach (var d in _incomplete.Distinct())
            byDay.TryAdd(d.Date, new RetryTarget { Day = d.Date, Tries = 0 });
        manifest.SetTodo(RetryTaskIds.BlockTrade, RetryTodoKind.PartialDay,
                         byDay.Values.OrderBy(t => t.Day).ToList());
        manifestStore.Save(manifest);
    }

    private static string Describe(FetchMode mode) => mode switch
    {
        FetchMode.FirstBackfill => "整段回补 2016 年至今",
        FetchMode.SpecificDay => "只抓指定的那一天",
        _ => $"增量（水位线往前回看 {LookbackDays} 天补滞后字段）",
    };

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:F1} 秒";
}
