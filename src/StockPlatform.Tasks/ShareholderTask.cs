using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取股东数据】（2026-09-18 从 <c>FetchOrchestrator.RunFetchShareholderAsync</c> 迁过来）。
/// 股东户数 + 十大股东 + 十大流通股东，见 doc/shareholder-task-design.md。
///
/// ════ 为什么迁 ════
/// 老实现跟分红那一项是同一个形状、同一个病：每轮取全量名单（5565 只）从头抓，
/// 失败名单写在 <c>await Task.WhenAll</c> **之后**、取消直接冒泡——被限流打断就等于整轮白跑，
/// 连"抓到哪了"都不剩。分红 2026-09-18 凌晨已经这么栽过一次（4 小时 48 分只到 2350/5902）。
///
/// ════ 顺手拆掉一颗写在注释里的雷 ════
/// 一只票要发**两个**请求（股东页 + 流通股东页）。老实现是 <c>Task.WhenAll(全部股票)</c>，
/// 于是实际执行顺序是"所有票的第 1 个请求 → 所有票的第 2 个请求"，前半程一条都写不进库
/// （<see cref="SinaShareholderProvider"/> 的注释原话："一旦这个接口也需要降速，
/// 就会立刻变成跑几百个请求、零写入、零进度"）。改成一批 30 只之后：批内两个请求在
/// 同一只票的 async 里连着发，批间落库，任何时刻停下来都有完整的批已经进库。
///
/// ════ 判据比分红精确：按报告期，不按"多久没抓" ════
/// 股东数据有报告期，所以能问"数据源有没有新东西"而不是"多久没抓了"——
/// 现在库里是半年报那一期全抓齐了，三季报要到 10 月底才披露，**在那之前一个请求都不用发**。
/// 判据在 <see cref="ShareholderFetchPlanner"/>（纯查库、零请求）。
///
/// ════ 名单含退市股 ════
/// 在市个股 + 退市股（2026-09-18 纳入，口径跟分红那一项一致）。回测要消除幸存者偏差，
/// 而纳入前库里 337 只退市股**只有 5 只有股东数据**——十大流通股东里的「香港中央结算」
/// 是北向持股的唯一来源，缺这一块等于那段历史只看得见活下来的票。
/// 退市股的时间兜底走 365 天一档，见 <see cref="ShareholderFetchPlanner.DelistedStaleDays"/>。
///
/// ════ 不设人为的每轮上限 ════
/// 分次是为限流付的税，不是目标（2026-09-18 用户定）。季度高峰一轮约 60 分钟、跑得完；
/// 真撞限流由下面那个熔断 + 断点续跑自然分次。
/// </summary>
public sealed class ShareholderTask(
    FetchPaths paths,
    IShareholderProvider provider,
    IShareholderRepository repository,
    IManifestStore manifestStore,
    int batchSize = ShareholderTask.DefaultBatchSize) : FetchTaskBase<ShareholderTask.ShareholderOutcome>
{
    /// <summary>一批多少只。一只 2 个请求、3 并发/1 秒 ⇒ 30 只≈20 秒，停止最多丢这么多只的在途结果。</summary>
    public const int DefaultBatchSize = 30;

    /// <summary>连续这么多批**全部失败且沾限流**就收工。3 批＝90 只，够排除偶发波动。</summary>
    private const int DeadBatchesBeforeAbort = 3;

    /// <summary>一只票的抓取结果。失败的也是一条——失败名单要靠它。</summary>
    public sealed record ShareholderOutcome(
        string Code, ShareholderData? Data, bool Ok, string? Error, bool RateLimited);

    private readonly int _batchSize = batchSize > 0 ? batchSize : DefaultBatchSize;

    public override FetchActionId Id => FetchActionId.FetchShareholder;

    /// <summary>待办自己补：股东的待办只有"重抓这几只"一类，跟日常抓取是同一个动作。</summary>
    public override bool HandlesBacklog => true;

    private readonly List<string> _attempted = [];
    private readonly List<string> _failed = [];

    private int _planned, _done, _withData, _suspicious;
    private string? _abortReason;
    private string? _nothingToDoNote;
    private readonly List<string> _errors = [];

    protected override async IAsyncEnumerable<IReadOnlyList<ShareholderOutcome>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _attempted.Clear();
        _failed.Clear();
        _errors.Clear();
        _planned = _done = _withData = _suspicious = 0;
        _abortReason = _nothingToDoNote = null;

        if (!File.Exists(paths.CurrentDb))
            throw new InvalidOperationException("本地还没有任何数据，无法拉取股东数据，请先执行一次\"拉取全部\"");

        // 判据要查四张表（股东/预约披露日/日K/名册），全是同步 IO。骨架不替子类推线程池，
        // 在首个 await 之前干这些会冻住界面。
        var codes = await Task.Run(() => PlanCodes(args), ct);
        _planned = codes.Count;

        if (codes.Count == 0)
        {
            Report(_nothingToDoNote + "。");
            yield break;
        }

        Report($"开始拉取股东数据（户数+十大股东+十大流通股东），本轮 {codes.Count} 只"
             + (args.Mode.HasFlag(FetchMode.FirstBackfill) ? "（整段回补：无视判据全抓）" : "")
             + $"，一批 {_batchSize} 只、每只 2 个请求、较慢...", 0, codes.Count);

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        int deadBatches = 0;
        try
        {
            for (int i = 0; i < codes.Count; i += _batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = codes.Skip(i).Take(_batchSize).ToList();
                var outcomes = await FetchChunkAsync(chunk, ct);

                _done += outcomes.Count;
                int failedHere = outcomes.Count(o => !o.Ok);
                Report($"股东数据 {_done}/{codes.Count}（有数据 {_withData + outcomes.Count(Has)}、"
                     + $"失败 {_failed.Count + failedHere}）", _done, codes.Count);

                yield return outcomes;

                deadBatches = IsDeadBatch(outcomes) ? deadBatches + 1 : 0;
                if (deadBatches >= DeadBatchesBeforeAbort)
                {
                    _abortReason = $"新浪在限流：连续 {DeadBatchesBeforeAbort} 批"
                                 + $"（{DeadBatchesBeforeAbort * _batchSize} 只）全部失败，"
                                 + $"本轮停在 {_done}/{codes.Count}。已抓到的都在库里，下次接着抓剩下的";
                    yield break;
                }
            }
        }
        finally { provider.OnStatus -= Forward; }
    }

    /// <summary>这一批并发抓。单只失败不传染：记成 <c>Ok=false</c> 的一条，本批其余照常。</summary>
    private async Task<IReadOnlyList<ShareholderOutcome>> FetchChunkAsync(
        IReadOnlyList<string> chunk, CancellationToken ct)
    {
        var tasks = chunk.Select(async code =>
        {
            try
            {
                // 两个请求在这里连着发（批内），不是"全市场第1个请求跑完再跑第2个"。
                var data = await provider.GetAsync(code, ct);
                return new ShareholderOutcome(code, data, true, null, false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ShareholderOutcome(code, null, false, ex.Message, ex is RateLimitedException);
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>这一批算不算"全军覆没"——熔断只数这种批。整批失败但没有一条是限流
    /// （比如页面改版把解析全打挂了）不算：那种该老老实实报失败，收工只会把问题藏起来。</summary>
    public static bool IsDeadBatch(IReadOnlyList<ShareholderOutcome> outcomes)
        => outcomes.Count > 0 && outcomes.All(o => !o.Ok) && outcomes.Any(o => o.RateLimited);

    private static bool Has(ShareholderOutcome o)
        => o.Ok && o.Data != null && (o.Data.Counts.Count > 0 || o.Data.TopHolders.Count > 0);

    /// <summary>本轮抓哪些。判据都在 <see cref="ShareholderFetchPlanner"/>，这里只管模式分岔。</summary>
    private List<string> PlanCodes(TaskRunArgs args)
    {
        repository.EnsureSchema();

        // 【只补待办】：目标从待办清单来，不从判据来（这是它跟增量的分界）。
        if (args.Mode.HasFlag(FetchMode.FillBacklog))
        {
            var todo = manifestStore.Load().Todo(RetryTaskIds.Shareholder, RetryTodoKind.Failed);
            var backlog = (todo?.Targets ?? []).Select(t => t.Code)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (backlog.Count == 0) _nothingToDoNote = "股东数据没有欠着的失败股票";
            return backlog;
        }

        var plan = new ShareholderFetchPlanner(paths).Plan(all: args.Mode.HasFlag(FetchMode.FirstBackfill));

        if (plan.Pending.Count == 0)
        {
            _nothingToDoNote = $"股东数据都已经是最新报告期了（按披露日判，法定截止口径 "
                             + $"{plan.ExpectedPeriod:yyyy-MM-dd}），{ShareholderFetchPlanner.StaleDays} 天内也都抓过，"
                             + "本轮没有要抓的（想强刷全市场用【整段回补】）";
            return [];
        }

        Report($"待抓 {plan.Pending.Count}/{plan.Total} 只：有新报告期或从没抓过 {plan.NewPeriod} 只、"
             + $"太久没抓 {plan.TimeStale} 只"
             + (plan.DelistedPending > 0 ? $"（其中退市股 {plan.DelistedPending} 只——消除回测的幸存者偏差）" : "")
             + (plan.Dormant > 0 ? $"；另有 {plan.Dormant} 只一年多没成交，跳过（恢复交易会自动回到名单）" : ""));
        return plan.Pending.ToList();
    }

    /// <summary>
    /// 落一批。
    ///
    /// ⚠ **空结果不写**（<c>Counts.Count > 0 || TopHolders.Count > 0</c> 这个判断必须在）：
    /// <c>ReplaceByCode</c> 是先删该 code 两表旧行再写，限流返回空页面时若照写，
    /// 会把那只票的股东历史整只删掉。拉不到新数据时，库里上一次的结果仍然有效。
    ///
    /// 自洽性检查原样保留（**只报警不拦截**）：排名夹在正数中间却持股为 0 ＝ 那一格没解析出来，
    /// 2026-08-13 那次就是这么静默混进 14953 行的。局部异常不该中断整批，
    /// 而且宁可先入库、让人看见问题，也好过悄悄丢数据。
    /// </summary>
    protected override Task SaveBatchAsync(IReadOnlyList<ShareholderOutcome> batch, CancellationToken ct)
    {
        foreach (var o in batch)
        {
            _attempted.Add(o.Code);
            if (!o.Ok)
            {
                _failed.Add(o.Code);
                if (o.Error != null) _errors.Add($"{o.Code}: {o.Error}");
                continue;
            }
            if (!Has(o)) continue;

            var bad = SqliteShareholderRepository.FindInconsistentZeroShares(o.Data!.TopHolders);
            if (bad.Count > 0)
            {
                _suspicious += bad.Count;
                foreach (var msg in bad.Take(3)) _errors.Add($"⚠ 数据可疑 {msg}");
            }

            repository.ReplaceByCode(o.Code, o.Data!);
            _withData++;
        }
        return Task.CompletedTask;
    }

    /// <summary>被取消时把失败名单落盘——**这是这次迁移的起因**：老实现把它写在
    /// <c>Task.WhenAll</c> 之后，取消直接冒泡，于是停一次就什么都不剩。
    /// 数据本身已经按批落过库了。⚠ 这时 ct 已经取消，只做同步的库操作。</summary>
    protected override Task OnStoppedAsync(TaskRunStats stats)
    {
        SaveFailedTodo();
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        SaveFailedTodo();

        if (_abortReason != null)
        {
            Report("⚠ " + _abortReason);
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Skipped(_abortReason, _errors));
        }

        if (_nothingToDoNote != null)
            return Task.FromResult<TaskRunResult?>(
                TaskRunResult.Ok(nothingToDo: true, progress: _nothingToDoNote));

        int left = _planned - _done;
        var summary = $"股东数据完成：本轮 {_done} 只、有数据 {_withData} 只、失败 {_failed.Count} 只"
                    + (left > 0 ? $"；本轮截断，还剩 {left} 只没抓（下轮接着来）" : "");
        Report(summary + (_failed.Count > 0 ? "（失败的可点【重新拉取失败股票】重试）" : ""));

        if (_suspicious > 0)
            Report($"⚠ 有 {_suspicious} 行持股数解析为 0 但排名夹在正数中间——"
                 + "这通常意味着数据源页面格式变了（比如在数字后面加了新的装饰符号）。"
                 + "数据已入库但那几行不可信，请检查 SinaShareholderProvider.ParseD 的清洗规则。");

        return Task.FromResult<TaskRunResult?>(
            _errors.Count > 0
                ? new TaskRunResult(TaskState.Completed, _errors, Progress: summary)
                : TaskRunResult.Ok(progress: summary));
    }

    /// <summary>更新失败待办。语义跟 <c>FetchOrchestrator.SetFailedTodo</c> 一模一样：
    /// **本轮碰过、这次没失败的移出名单**，本轮新失败的并进去。没碰过的原样留着——
    /// 它们只是这轮没排到，不是补好了。</summary>
    private void SaveFailedTodo()
    {
        if (_attempted.Count == 0) return;
        var manifest = manifestStore.Load();
        var current = manifest.Todo(RetryTaskIds.Shareholder, RetryTodoKind.Failed)?.Targets
                              .Select(t => t.Code).ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);
        current.ExceptWith(_attempted);
        current.UnionWith(_failed);
        manifest.SetTodo(RetryTaskIds.Shareholder, RetryTodoKind.Failed,
                         current.OrderBy(c => c, StringComparer.Ordinal)
                                .Select(c => new RetryTarget { Code = c }).ToList());
        manifestStore.Save(manifest);
    }
}
