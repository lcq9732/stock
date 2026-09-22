using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【重新拉取失败】（2026-09-22 从 <c>FetchOrchestrator.RunRetryFailedInternalAsync</c> 迁到新框架）——
/// **自己不抓任何东西**：读统一待办清单，按 <see cref="RetryTodo.TaskId"/> 挨个让那个任务
/// 以 <see cref="FetchMode.FillBacklog"/> 跑一轮。
///
/// 跟【拉取区间数据】是同一个形状（<see cref="FetchYearTask"/>），差别只在**分派依据**：
/// 那边是年份区间，这边是待办清单。
///
/// ════ 为什么是「读清单」而不是手写一串 .Count == 0 ════
/// 2026-09-13 之前这里是一长串 <c>if (xxx.Count &gt; 0)</c>，每加一类待办就要再写一段。
/// 于是 09-02 加的历史空洞、09-06 加的资金流缺失日，执行链加上了、判「有没有活」那里却没跟——
/// 名单里躺着 1909 段，只要那几份失败名单清零就直接返回「不需要重试」，
/// **那 1909 段永远补不上而且一声不吭**。现在「有哪些待办」只有 <see cref="RetryBacklog"/> 一处知道，
/// 显示、按钮、这里的分派全读同一份。
///
/// ════ 一批 ＝ 一个任务 ════
/// 所以 <see cref="TaskRunArgs.MaxItems"/>／<see cref="TaskRunArgs.Deadline"/> 落在任务之间。
/// <c>Deadline</c> 往下传（单个子任务自己也会在批边界到点收尾），<c>MaxItems</c> 不传——
/// 这一层的「批」是子任务，传下去会变成「每个子任务只做 N 批」，语义完全不同。
///
/// ════ 失败名单这里不动 ════
/// 待办全由任务自己补，谁失败了谁自己记（各任务按自己的 taskId 写）。
/// 这里若攒一份「碰过的代码」统一写，没有 taskId 就会默认记到前复权那一格——
/// 正是 2026-09-13 拆分失败名单要解决的老问题。
/// </summary>
public sealed class RetryFailedTask(
    FetchPaths paths,
    IManifestStore manifestStore,
    IFetchTaskRegistry registry,
    IFetchTaskDispatcher dispatcher) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.RetryFailed;

    private readonly List<string> _errors = [];
    private readonly List<string> _done = [];
    private string? _nothingToDoReason;

    /// <summary>
    /// 这一轮要按什么顺序调哪些任务（2026-09-13 定，迁移时原样搬过来）。
    ///
    /// 顺序固定，日志才可预期：便宜的整轮扫描排前面，K线那几项最重、排后面。
    /// 不在这张表里的任务排最后、按 id 字典序——新加一个任务忘了登记顺序也跑得起来，只是排在末尾。
    ///
    /// <c>public static</c> 是为了能单测（见 <c>RetryDispatchTests</c>）：**分派对不对是这一项的核心**，
    /// 而真跑一轮要发几千个网络请求，测不了。
    /// </summary>
    public static List<string> DispatchOrder(RetryBacklog backlog)
    {
        var order = new[]
        {
            RetryTaskIds.Roster, RetryTaskIds.NetInflow,
            RetryTaskIds.IndexCons, RetryTaskIds.IndexWeight,
            RetryTaskIds.Shareholder, RetryTaskIds.Dividend,
            RetryTaskIds.StockDayBars, RetryTaskIds.StockHfqBars, RetryTaskIds.StockRawBars,
            RetryTaskIds.EtfBars, RetryTaskIds.IndexBars, RetryTaskIds.DelistedTails,
        };
        return backlog.Actionable.Select(i => i.TaskId).Distinct(StringComparer.Ordinal)
            .OrderBy(id => Array.IndexOf(order, id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _done.Clear();
        _nothingToDoReason = null;

        if (!File.Exists(paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法重新拉取，请先执行一次「拉取全部」");

        var backlog = await Task.Run(() => RetryBacklog.From(manifestStore.Load()), ct);
        if (!backlog.Any)
        {
            _nothingToDoReason = "目前没有记录到抓取失败或缺当天数据的股票，不需要重试";
            Report(_nothingToDoReason);
            yield break;
        }

        var taskIds = DispatchOrder(backlog);
        Report($"本轮要补：{backlog.Describe()}", 0, taskIds.Count);

        int i = 0;
        foreach (var taskId in taskIds)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            if (!Enum.TryParse<FetchActionId>(taskId, out var action))
            {
                _errors.Add($"认不出的动作：{taskId}");
                Report($"⚠ {_errors[^1]}（跳过）");
                yield return [i];
                continue;
            }

            var label = FetchTaskCatalog.Info(action).Name;

            // 走到这儿说明有个任务没声明 HandlesBacklog、却挂着待办——那是配置错，说清楚别静默。
            // ⚠ 这条今天打不到（所有挂待办的任务都声明了），但它是配置错的唯一提示，别删。
            //   2026-09-22 查过一个近似的：FetchMoneyFlowDetail 在 RetryBacklog 里有标签映射、
            //   却没有任何写入方，哪天给它加上残缺日体检就会落到这一支。
            if (!registry.HandlesBacklog(action))
            {
                Report($"⚠【{label}】没有声明自己补待办（HandlesBacklog=false），"
                     + "它欠着的那些补不了——这是个配置问题，见 IFetchTask.HandlesBacklog。");
                yield return [i];
                continue;
            }

            Report($"[{i}/{taskIds.Count}] 开始补【{label}】的待办…", i, taskIds.Count, phase: label);

            FetchResult? result = null;
            try
            {
                // 转发子任务的事件（Quiet 心跳也要过来），Deadline 往下传、MaxItems 不传
                result = await dispatcher.RunAsync(
                    action, new TaskRunArgs(FetchMode.FillBacklog, Deadline: args.Deadline),
                    t => ForwardFrom(t, label), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _errors.Add($"【{label}】补待办失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}（不影响后面几项，继续）");
            }

            if (result != null)
            {
                foreach (var e in result.Errors) _errors.Add($"【{label}】{e}");
                if (!result.NothingToDo) _done.Add(label);
            }

            yield return [i];
        }
    }

    /// <summary>用不上——每个子任务自己落库。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        Report($"重新拉取失败已停止：已经补完的 {_done.Count} 项都算数"
             + (_done.Count > 0 ? $"（{string.Join("、", _done)}）" : "")
             + "，剩下的待办还在名单上，下轮接着补。");
        return Task.CompletedTask;
    }

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_nothingToDoReason is { } idle)
            return new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, idle);

        // 刚补过一轮，当天覆盖的名单得重算——⚠ 只有这条路做这件事，
        // 单项【只补待办】不做（那一项只碰自己那一格）。
        await Task.Run(CheckLatestDayCoverage, ct);

        Report(_done.Count == 0 ? "本轮没有需要重试的项目" : "本轮重试完成：" + string.Join("、", _done));
        var left = await Task.Run(() => RetryBacklog.From(manifestStore.Load()), ct);
        var summary = left.Any
            ? $"仍有待重试：{left.Describe()}——可以再点一次这个按钮"
            : "失败名单已全部清零，没有遗留项目";
        Report(summary);
        return new TaskRunResult(TaskState.Completed, _errors, NothingToDo: _done.Count == 0, summary);
    }

    /// <summary>
    /// 当日完整性体检（2026-09-22 从编排层搬过来，那边只剩这一个调用方了）。
    /// 判据和编排都在 <see cref="SqliteDayCompletenessAuditor"/>，这里只是「跑一轮、写 manifest、报结论」。
    ///
    /// ⚠ 计划里的【当日完整性体检】那一项走的**不是**这里，而是 <c>DayCompletenessTask</c>。
    /// 两边共用同一个 auditor，所以不会出现「体检说齐了、重试这边还挂着单子」的分叉。
    /// </summary>
    private void CheckLatestDayCoverage()
    {
        var auditor = new SqliteDayCompletenessAuditor(paths.CurrentDb);
        var found = new List<DayFinding>();
        DateTime? latest;
        lock (SqliteWriteGate.Local)
        {
            foreach (var batch in auditor.RunSegments(out latest)) found.AddRange(batch);
        }
        if (latest == null)
        {
            Report("（跳过当日完整性体检：本地上证指数日线不足两根，没有交易日锚可用）");
            return;
        }

        lock (SqliteWriteGate.Local)
        {
            var manifest = manifestStore.Load();
            SqliteDayCompletenessAuditor.Apply(manifest, found);
            manifestStore.Save(manifest);
        }

        foreach (var f in found.Where(f => f.Detail is { Length: > 0 })) Report($"⚠ {f.Detail}");

        int problems = found.Count(f => f.IsBad);
        Report(problems == 0
            ? $"当日完整性体检：{latest:yyyy-MM-dd} 全齐 —— {string.Join("；", found.Select(f => f.Summary))}"
            : $"当日完整性体检：{latest:yyyy-MM-dd} 有 {problems} 处不齐 —— "
              + string.Join("；", found.Select(f => f.Summary)));
    }
}
