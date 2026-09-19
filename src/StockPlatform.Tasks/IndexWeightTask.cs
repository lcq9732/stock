using System.Diagnostics;
using System.Runtime.CompilerServices;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【指数权重】（2026-09-18 从编排器迁到新框架）。见 doc/index-roster-task-design.md。
///
/// ════ ⚠ 两道筛子是这一项的命根子，别简化掉 ════
/// 内置指数全集 732 个，而权重文件（中证 closeweight.xls）**只有中证系才有**、其余一律 404；
/// 权重本身是**月度**更新的。原来每轮把 732 个硬敲一遍，等于每次拿四五百个注定 404 的请求
/// 去撞中证的反爬——数据一条也拿不到。两道筛子：
///   ① 本地这一期还新鲜（<see cref="FreshDays"/> 天内）→ 跳过，月中跑基本全跳过；
///   ② 上次已确认没有文件、且没过 <see cref="MissingRetryDays"/> 天 → 跳过。
/// 稳态下真正发出的请求从 732 降到接近 0，只有月初那一轮才实抓中证系那两三百个。
///
/// ════ ⚠ "没有权重文件"那份名单**不在 Todos 里** ════
/// 它是 <c>Manifest.IndexWeightMissing</c>（code → 确认时间）。别顺手"统一"成待办格式：
/// 那是一次不可逆的键迁移，而且它的语义（"这个指数没有文件，30 天后再问一次"）
/// 跟"欠着的活"根本不是一回事。
///
/// 一批＝一个指数，于是 <c>MaxItems</c>/<c>Deadline</c> 落在指数边界上。
/// </summary>
public sealed class IndexWeightTask(
    IIndexWeightProvider provider,
    IIndexConsRepository repository,
    IManifestStore manifestStore) : FetchTaskBase<IndexWeightTask.IndexWeights>
{
    public override FetchActionId Id => FetchActionId.StepIndexWeight;

    /// <summary>待办只有"失败名单"一类。</summary>
    public override bool HandlesBacklog => true;

    /// <summary>本地这一期多新才算"不用再抓"。中证是月度更新（基准日＝月末交易日），
    /// 25 天足够覆盖一个更新周期，又不会把月初的新一期漏掉。</summary>
    public const int FreshDays = 25;

    /// <summary>确认 404 之后隔多久再问一次。中证偶尔会给新指数补上文件，所以不能永久拉黑。</summary>
    public const int MissingRetryDays = 30;

    /// <summary>一个指数的权重。空列表＝404，这个指数没有权重文件（不是失败）。</summary>
    public sealed record IndexWeights(string Code, List<IndexWeightRow> Rows);

    private readonly Stopwatch _sw = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _attempted = [];
    private readonly List<string> _failed = [];
    private readonly List<string> _newlyMissing = [];

    private int _ok, _none, _freshSkip, _missingSkip;
    private string? _skipped;

    protected override async IAsyncEnumerable<IReadOnlyList<IndexWeights>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _errors.Clear(); _attempted.Clear(); _failed.Clear(); _newlyMissing.Clear();
        _ok = _none = _freshSkip = _missingSkip = 0;
        _skipped = null;
        _sw.Restart();

        // 两道筛子要查库（本地这一期的基准日）和读 manifest，都是同步 IO。
        var targets = await Task.Run(() => PlanCodes(args), ct);
        if (targets.Count == 0)
        {
            _skipped = args.Mode == FetchMode.FillBacklog
                ? "指数权重没有欠着的失败项"
                : "指数权重：都不用抓，这一轮无事可做";
            Report($"{_skipped}。");
            yield break;
        }

        int done = 0;
        foreach (var code in targets)
        {
            ct.ThrowIfCancellationRequested();
            _attempted.Add(code);
            List<IndexWeightRow>? rows = null;
            try
            {
                rows = await provider.GetWeightsAsync(code, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _failed.Add(code);
                _errors.Add($"指数 {code} 权重抓取失败：{ex.Message}");
            }

            // ⚠ 心跳**每只**一次、日志仍按上面的间隔（2026-09-19）：只按日志间隔出声的话，
            //   单位一慢就顶上静默看门狗的 5 分钟上限，一路正常跑也会被判成卡死
            //   （【资金净流入】09-18/09-19 就是这么被掐的，见 QuietWatchdog.IBeatOnlySink）。
            if (++done % 20 == 0 || done == targets.Count)
                // ⚠ 同 IndexConsTask：不报累计数，骨架先 yield 再存，读到的永远差一批。
                Report($"指数权重：{done}/{targets.Count}", done, targets.Count);
            else
                ReportQuiet($"指数权重：{done}/{targets.Count}", done, targets.Count);

            if (rows != null) yield return [new IndexWeights(code, rows)];
        }
    }

    /// <summary>一批＝一个指数：整份替换。空的＝404，记进"没有文件"名单、不算失败。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<IndexWeights> batch, CancellationToken ct)
    {
        foreach (var w in batch)
        {
            if (w.Rows.Count == 0) { _none++; _newlyMissing.Add(w.Code); continue; }
            repository.ReplaceWeights(w.Code, w.Rows);
            _ok++;
        }
        return Task.CompletedTask;
    }

    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveManifest();
        Report($"指数权重中断。已落库的 {_ok} 个指数有效（各自整份替换），下次再跑一轮即可。");
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        SaveManifest();
        if (_skipped is { } why)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(why, _errors));

        var summary = $"指数权重完成：{_ok} 个有权重、{_none} 个没有权重文件"
                    + $"（已记下、{MissingRetryDays} 天内不再问）、失败 {_failed.Count} 个"
                    + (_failed.Count > 0 ? "（中证这侧偏不稳，可点【重新拉取失败】重试）" : "")
                    + $"，用时 {ElapsedText.Format(_sw.Elapsed)}。";
        Report(summary);

        if (_attempted.Count > 0 && _failed.Count == _attempted.Count)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary));

        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }

    /// <summary>
    /// 本轮问哪些指数。【只补待办】直接用失败名单（不过筛子——那些就是要重试的）；
    /// 日常则把全集过两道筛子（见类注释）。
    /// </summary>
    private List<string> PlanCodes(TaskRunArgs args)
    {
        var manifest = manifestStore.Load();
        if (args.Mode == FetchMode.FillBacklog)
            return (manifest.Todo(RetryTaskIds.IndexWeight, RetryTodoKind.Failed)?.Targets ?? [])
                .Select(t => t.Code).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        var indexes = IndexCatalog.All;
        if (indexes.Count == 0) return [];

        var latestByIndex = repository.GetLatestWeightDateByIndex();
        var missing = new Dictionary<string, DateTime>(manifest.IndexWeightMissing, StringComparer.Ordinal);
        var today = DateTime.Today;
        var targets = new List<string>();
        foreach (var (code, _) in indexes)
        {
            if (latestByIndex.TryGetValue(code, out var asOf)
                && (today - asOf).TotalDays < FreshDays) { _freshSkip++; continue; }
            if (missing.TryGetValue(code, out var confirmedAt)
                && (today - confirmedAt.Date).TotalDays < MissingRetryDays) { _missingSkip++; continue; }
            targets.Add(code);
        }

        Report($"指数权重：全集 {indexes.Count} 个，本轮要问 {targets.Count} 个"
             + $"（{_freshSkip} 个本地已是最新一期、{_missingSkip} 个确认没有权重文件——"
             + "中证是月度更新，这两道筛子是为了少撞它的反爬）。");
        return targets;
    }

    /// <summary>
    /// 收尾写 manifest：失败名单 + "没有权重文件"名单。
    ///
    /// ⚠ 失败名单的 attempted 是**本轮真问过的**那些，不是全集 732——
    /// 被两道筛子跳过的既不该被清出名单，也不该被记进去。
    /// </summary>
    private void SaveManifest()
    {
        if (_attempted.Count == 0) return;
        var manifest = manifestStore.Load();

        var current = (manifest.Todo(RetryTaskIds.IndexWeight, RetryTodoKind.Failed)?.Targets ?? [])
            .Select(t => t.Code).ToList();
        var stillFailed = new HashSet<string>(current, StringComparer.Ordinal);
        stillFailed.ExceptWith(_attempted);
        stillFailed.UnionWith(_failed);
        manifest.SetTodo(RetryTaskIds.IndexWeight, RetryTodoKind.Failed,
                         stillFailed.OrderBy(c => c, StringComparer.Ordinal)
                                    .Select(c => new RetryTarget { Code = c }).ToList());

        var today = DateTime.Today;
        foreach (var code in _newlyMissing) manifest.IndexWeightMissing[code] = today;
        // 这次抓到权重的，把"没有文件"的记录撤掉（中证补上了文件的情况）
        foreach (var code in _attempted.Except(_newlyMissing, StringComparer.Ordinal))
            manifest.IndexWeightMissing.Remove(code);

        manifestStore.Save(manifest);
    }
}
