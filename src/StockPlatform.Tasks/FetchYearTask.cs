using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取区间数据】（2026-09-22 从 <c>FetchOrchestrator.RunFetchYearInternalAsync</c> 迁到新框架，
/// 见 doc/fetch-year-migration-design.md）——**向前回补**：按年份区间往回补更早的历史。
///
/// ════ 它自己不抓任何东西 ════
/// 这一项是个**分派器**：带着"年份区间"这个参数去调已有的各个任务，跟【重新拉取失败】
/// 是同一个形状（那边分派的依据是待办清单，这边是年份区间）。
///
/// 迁移前它是一个 190 行的方法，把十一段的抓取逻辑**又写了一遍**——而那十三段现在全都已经
/// 有对应的新框架任务了。再写一遍的代价不是重复代码本身，是**两份判据迟早分叉**，
/// 而分叉的表现是"某个口径的K线值悄悄不一样"，没有任何地方会报。
///
/// ════ 断点续：无状态，不记"跑到第几项" ════
/// 跟【重新拉取失败】不记一样：每个子任务的目标都是**从库现算**的（按各标的水位线 / 完整性判据），
/// 重跑一个已经补完的子任务，它会把所有票都跳过、一个请求都不发。
/// 显式游标只会带来"游标跟实际数据不一致"这一整类新的失效模式。
/// 代价是重跑时每个子任务各做一次全表 GROUP BY（几分钟量级，对一轮 1.7 小时是个位数百分比）——
/// 用户 2026-09-22 拍板接受，2026-09-02 那条"故意保持复合以共享一次预取"的理由随之作废。
///
/// ════ 一批 ＝ 一个子任务 ════
/// 所以 <see cref="TaskRunArgs.MaxItems"/>／<see cref="TaskRunArgs.Deadline"/> 落在**子任务之间**。
/// <c>Deadline</c> 还会**往下传**：单个子任务自己也会在批边界上到点收尾，
/// 否则一个跑一小时的子任务会把整轮的空窗约定拖穿。<c>MaxItems</c> 不往下传——
/// 这一层的"批"是子任务，传下去会变成"每个子任务只做 N 批"，语义完全不同。
///
/// ════ 它是"任务套任务"，事件要原样转发 ════
/// 新框架本来就支持这个形状：任务是独立的，别人调用时**转发它的事件**即可
/// （注册表的 <c>RunAsync</c> 一直留着 <c>subscribe</c> 口子）。这里用
/// <see cref="FetchTaskBase{T}.ForwardFrom"/>。
/// ⚠ 别退化成 <c>ProgressSink</c>：那是压扁成字符串的通道，<c>Quiet</c> 心跳过不来——
/// 2026-09-22 第一版就是这么写的，实测被静默看门狗掐断了一整轮。
///
/// ════ 顺序是有依赖的 ════
/// 退市名单要在退市收尾之前（它的产出是后者的输入）；个股不复权要在【重算回测序列】之前
/// （day_adj 是拿它算的）；板块指数合成最后（它读 day_adj）。
/// </summary>
public sealed class FetchYearTask(IFetchTaskDispatcher dispatcher) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.FetchYear;

    /// <summary>A股最早的年份（<see cref="IncrementalWindowCalculator.AShareMarketOpen"/> 那一年）。</summary>
    private const int FirstAShareYear = 1990;

    /// <summary>
    /// 分派表：跑哪些、按什么顺序、吃不吃年份区间。
    /// </summary>
    /// <param name="Action">调哪个任务。</param>
    /// <param name="TakesYears">
    /// false＝这一项**不吃年份区间**，按它自己的日常语义跑一次：
    /// 退市名单是一份全量名单、根本没有年份维度；退市收尾补的是"终止日前最后那几天"，
    /// 窗口由终止日决定（退市股的**区间历史**已经由上面三个口径带上了，见 StockDayBarTask.BackfillCodes）；
    /// 重算回测序列和板块指数合成都是本地计算，读多少算多少。
    /// </param>
    private sealed record Step(FetchActionId Action, bool TakesYears = true);

    /// <summary>⚠ 顺序有依赖，改之前看类注释最后一段。</summary>
    private static readonly Step[] Steps =
    [
        new(FetchActionId.StepIndexBars),            // 大盘指数日K
        new(FetchActionId.StepStockDayBars),         // 个股前复权（含退市股）
        new(FetchActionId.StepStockHfqBars),         // 个股后复权
        new(FetchActionId.StepStockRawBars),         // 个股不复权
        new(FetchActionId.StepEtfBars),              // ETF 日K
        new(FetchActionId.StepEtfRawBars),           // ETF 不复权
        new(FetchActionId.StepDelistedSupplement, TakesYears: false),
        new(FetchActionId.StepDelistedTails, TakesYears: false),
        new(FetchActionId.StepNetInflow),            // 资金净流入（源起点 2010-03-01）
        new(FetchActionId.StepAnnouncements),        // 中标/订单公告（按年切片）
        new(FetchActionId.StepMargin),               // 融资余额
        new(FetchActionId.StepLhb),                  // 龙虎榜
        // 补完更早的不复权历史之后，day_adj 就旧了——回测序列和板块指数都是拿它算的。
        // ⚠ 老实现漏了这一步：它只在末尾重合成板块指数，而板块指数读的是 day_adj，
        //   day_adj 没重算的话"历史变长了"这件事根本传不过去。
        new(FetchActionId.RebuildAdjSeries, TakesYears: false),
        new(FetchActionId.StepBoardIndex, TakesYears: false),
    ];

    private readonly List<string> _errors = [];
    private readonly List<string> _done = [];
    private int _ran, _failed;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _done.Clear();
        _ran = _failed = 0;

        var (startYear, endYear) = ValidateYears(args);
        Report($"区间回补 {startYear}~{endYear} 年：按顺序跑 {Steps.Length} 项，"
             + "每一项都只补本地还缺的部分（已经有的整只跳过、不发请求）。"
             + "停在任何一项之间都算数——下轮重跑时补过的那些会被各项自己跳过。");

        int i = 0;
        foreach (var step in Steps)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var info = FetchTaskCatalog.Info(step.Action);

            var sub = new TaskRunArgs(
                // 「首次整段回补」＝不看水位线、只补缺的。年份区间把它的"整段"收窄成这几年
                // （见 BackfillWindowRule：只收窄、永不放宽）。不吃年份的那几项按日常语义跑。
                Mode: step.TakesYears ? FetchMode.FirstBackfill : FetchMode.Incremental,
                // Deadline 往下传、MaxItems 不传，理由见类注释
                Deadline: args.Deadline,
                YearStart: step.TakesYears ? startYear : null,
                YearEnd: step.TakesYears ? endYear : null,
                OverwriteQfq: step.TakesYears && args.OverwriteQfq);
            // ⚠ **不传 Keywords**：那是【中标/订单公告】自己的参数，在它自己那一行填。
            //   这里传一份就是两处配置、两处可能不一致。子任务拿不到就用它自己的默认值
            //   （见 AnnouncementTask：null＝没人指定、用默认；空列表＝人明确清空了、不抓）。

            Report($"[{i}/{Steps.Length}] 开始【{info.Name}】…", i, Steps.Length, phase: info.Name);

            // ⚠ C# 不允许在 catch 里 yield，所以异常只在这里转成结果、让出边界那句放在外面。
            FetchResult? result = null;
            try
            {
                // **转发子任务的事件**，不是把它压扁成 IProgress——压扁会丢掉 Quiet 心跳，
                // 于是靠 ReportQuiet 喂狗的子任务在外层看来是哑的（见 FetchTaskBase.ForwardFrom）。
                result = await dispatcher.RunAsync(step.Action, sub, t => ForwardFrom(t, info.Name), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单项失败不该带倒后面的——各项之间没有「失败就不能继续」的硬依赖
                // （退市名单失败的话退市收尾用库里已有的名单继续，跟它平时那样）。
                _failed++;
                _errors.Add($"【{info.Name}】失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}（不影响后面几项，继续）");
            }

            if (result != null)
            {
                _ran++;
                foreach (var e in result.Errors) _errors.Add($"【{info.Name}】{e}");
                if (!result.NothingToDo) _done.Add(info.Name);
            }

            // 一个子任务跑完＝一个可停边界。返回一条计数、骨架据此判 MaxItems/Deadline。
            yield return [i];
        }
    }

    /// <summary>用不上——每个子任务自己落库。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// 年份区间校验。**填错了就明确失败**，不要猜一个默认值继续跑：这一项一轮一个多小时，
    /// 按错的年份跑完再发现，代价比报错大得多。
    /// </summary>
    private static (int Start, int End) ValidateYears(TaskRunArgs args)
    {
        int today = DateTime.Today.Year;
        int start = args.YearStart ?? throw new InvalidOperationException(
            "【拉取区间数据】要填起始年份——它是「从哪一年开始往回补」，没有合理的默认值。");
        int end = args.YearEnd ?? today;

        if (start < FirstAShareYear || start > today)
            throw new InvalidOperationException(
                $"起始年份 {start} 超出可抓范围（A股最早 {FirstAShareYear} 年，且不能晚于今年 {today}）");
        if (end < start || end > today)
            throw new InvalidOperationException(
                $"结束年份 {end} 不对：不能早于起始年份 {start}，也不能晚于今年 {today}");
        return (start, end);
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        Report($"区间回补已停止：{Steps.Length} 项里跑完了 {_ran} 项"
             + (_done.Count > 0 ? $"（有产出的：{string.Join("、", _done)}）" : "")
             + "。已经补进库的都算数，下轮重跑时各项会自己跳过补过的部分。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var summary = $"区间回补完成：{Steps.Length} 项跑完 {_ran} 项"
                    + (_failed > 0 ? $"、{_failed} 项失败" : "")
                    + (_done.Count > 0 ? $"；有产出的：{string.Join("、", _done)}" : "；各项都没有可补的");
        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: _done.Count == 0, summary));
    }
}
