using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Logic.Services;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【指数成分名单】（2026-09-18 从编排器迁到新框架）。见 doc/index-roster-task-design.md。
///
/// 内置指数全集 <c>IndexCatalog.All</c>（732 个），逐个问数据源要成分股，整份替换落库。
///
/// ⚠ **空结果不算失败**：新浪对某些老指数本来就没有成分，返回空是事实不是错误。
/// 写成"空就记失败"的话，那些指数会永远躺在失败名单里刷存在感、每轮都白抓一次。
///
/// 一批＝一个指数，于是 <c>MaxItems</c>/<c>Deadline</c> 落在指数边界上——732 个的轮次
/// 终于能分批跑、能到点收尾（迁移前中断就是整轮白费）。
/// </summary>
public sealed class IndexConsTask(
    IIndexConsProvider provider,
    IIndexConsRepository repository,
    IManifestStore manifestStore) : FetchTaskBase<IndexConsTask.IndexCons>
{
    public override FetchActionId Id => FetchActionId.StepIndexCons;

    /// <summary>待办只有"失败名单"一类，补法跟日常抓取是同一个动作。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>一个指数的成分。空列表＝这个指数在数据源上就是没有成分（不是失败）。</summary>
    public sealed record IndexCons(string Code, List<(string Code, DateTime? InDate)> Members);

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _attempted = [];
    private readonly List<string> _failed = [];

    private int _ok, _empty, _planned;
    private string? _skipped;

    protected override async IAsyncEnumerable<IReadOnlyList<IndexCons>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear(); _attempted.Clear(); _failed.Clear();
        _ok = _empty = _planned = 0;
        _skipped = null;
        _sw.Restart();

        var codes = await Task.Run(() => PlanCodes(args), ct);
        _planned = codes.Count;
        if (codes.Count == 0)
        {
            _skipped = args.Mode == FetchMode.FillBacklog
                ? "指数成分没有欠着的失败项"
                : "内置指数清单为空（IndexCatalog.csv 未打包？），无法拉取指数成分";
            Report($"{_skipped}。");
            yield break;
        }

        Report($"指数成分：{(args.Mode == FetchMode.FillBacklog ? "只补失败名单" : "全量刷新")}，"
             + $"共 {codes.Count} 个指数...");

        int done = 0;
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            _attempted.Add(code);
            List<(string Code, DateTime? InDate)>? members = null;
            try
            {
                members = await provider.GetConsAsync(code, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _failed.Add(code);
                _errors.Add($"指数 {code} 成分抓取失败：{ex.Message}");
            }

            // ⚠ 心跳**每只**一次、日志仍按上面的间隔（2026-09-19）：只按日志间隔出声的话，
            //   单位一慢就顶上静默看门狗的 5 分钟上限，一路正常跑也会被判成卡死
            //   （【资金净流入】09-18/09-19 就是这么被掐的，见 QuietWatchdog.IBeatOnlySink）。
            if (++done % 20 == 0 || done == codes.Count)
                // ⚠ 不报"成功 N"：骨架先 yield 再 SaveBatchAsync，_ok 要等这一批存完才涨，
                //   在这儿读永远差一批。计数留给收尾那句汇总。
                Report($"指数成分：{done}/{codes.Count}", done, codes.Count);
            else
                ReportQuiet($"指数成分：{done}/{codes.Count}", done, codes.Count);

            if (members != null) yield return [new IndexCons(code, members)];
        }
    }

    /// <summary>一批＝一个指数：整份替换。空的那个只记一笔"它就是没有成分"，不落库、不算失败。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<IndexCons> batch, CancellationToken ct)
    {
        var now = DateTime.Now;
        foreach (var c in batch)
        {
            if (c.Members.Count == 0) { _empty++; continue; }
            repository.ReplaceCons(c.Code, c.Members, now);
            _ok++;
        }
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo();
        Report($"指数成分中断。已落库的 {_ok} 个指数有效（各自整份替换），下次再跑一轮即可。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        SaveFailedTodo();
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

        var summary = $"指数成分完成：{_ok} 个指数有数据、{_empty} 个无成分、失败 {_failed.Count} 个"
                    + (_failed.Count > 0 ? "（可点【重新拉取失败】重试）" : "")
                    + $"，用时 {ElapsedText.Format(_sw.Elapsed)}。";
        Report(summary);

        // 问过的全失败 → 整项失败（多半是被限流或断网）。
        if (_attempted.Count > 0 && _failed.Count == _attempted.Count)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }

    private List<string> PlanCodes(TaskRunArgs args)
    {
        if (args.Mode == FetchMode.FillBacklog)
            return (manifestStore.Load().Todo(RetryTaskIds.IndexCons, RetryTodoKind.Failed)?.Targets ?? [])
                .Select(t => t.Code).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        return IndexCatalog.All.Select(i => i.Code).ToList();
    }

    /// <summary>本轮问过的里，这次没失败的移出名单；没问到的原样留着。</summary>
    private void SaveFailedTodo()
    {
        if (_attempted.Count == 0) return;
        var manifest = manifestStore.Load();
        var current = (manifest.Todo(RetryTaskIds.IndexCons, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).ToList();
        var stillFailed = new HashSet<string>(current, StringComparer.Ordinal);
        stillFailed.ExceptWith(_attempted);
        stillFailed.UnionWith(_failed);
        manifest.SetTodo(RetryTaskIds.IndexCons, RetryTodoKind.Failed,
                         stillFailed.OrderBy(c => c, StringComparer.Ordinal)
                                    .Select(c => new RetryTarget { Code = c }).ToList());
        manifestStore.Save(manifest);
    }
}
