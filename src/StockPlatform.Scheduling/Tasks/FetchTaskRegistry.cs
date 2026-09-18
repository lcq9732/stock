using StockPlatform.Data.Orchestration;

namespace StockPlatform.Scheduling.Tasks;

/// <summary>
/// 新式任务的注册表（2026-09-08）。
///
/// **加一个新任务＝写一个类 + 在这里注册一行**，不用再动 <c>DispatchPlanActionAsync</c> 那个
/// 44 个 case 的 switch（那边只加了一条"registry 里有就走新路"的分支，加过一次就不用再碰）。
///
/// 存的是**工厂**不是实例：每次运行现 new 一个，跑完丢弃。这样事件订阅天然不会累积——
/// 复用实例的话，忘了配对 -= 就是重复日志加内存泄漏，而任务实例本身很便宜
/// （依赖都在构造参数里，运行期参数走 <see cref="TaskRunArgs"/>）。
/// </summary>
public interface IFetchTaskRegistry
{
    /// <summary>这个动作是不是新式任务。</summary>
    bool Has(FetchActionId id);

    /// <summary>造一个新实例。<see cref="Has"/> 为 false 时返回 null。</summary>
    IFetchTask? Create(FetchActionId id);

    /// <summary>注册了哪些动作（给自检和测试用）。</summary>
    IReadOnlyCollection<FetchActionId> Registered { get; }
}

/// <inheritdoc cref="IFetchTaskRegistry"/>
public sealed class FetchTaskRegistry : IFetchTaskRegistry, ITaskBacklogRunner
{
    private readonly Dictionary<FetchActionId, Func<IFetchTask>> _factories = new();

    public void Register(FetchActionId id, Func<IFetchTask> factory) => _factories[id] = factory;

    public bool Has(FetchActionId id) => _factories.ContainsKey(id);

    public IFetchTask? Create(FetchActionId id)
        => _factories.TryGetValue(id, out var f) ? f() : null;

    public IReadOnlyCollection<FetchActionId> Registered => _factories.Keys;

    /// <summary>
    /// 这个动作的**待办**是不是由任务自己补（2026-09-18）——见
    /// <see cref="IFetchTask.HandlesBacklog"/>。
    ///
    /// ⚠ 按任务的声明判，不是"registry 里有就算"：没实现 FillBacklog 的任务收到这个模式
    /// 会返回空、报一句"没有欠着的"，待办**永远补不上而且一声不吭**。
    /// </summary>
    public bool HandlesBacklog(FetchActionId id) => Create(id)?.HandlesBacklog == true;

    // ── ITaskBacklogRunner：给编排层（Data 层，引用不到这里）转交待办用 ──
    //    【重新拉取失败股票】是在 orchestrator 内部按 taskId 循环的，不经过界面那一层，
    //    所以必须有这条回来的路，否则它会静默跳过"自己补待办"的那些任务。

    bool ITaskBacklogRunner.Handles(string taskId)
        => Enum.TryParse<FetchActionId>(taskId, out var id) && HandlesBacklog(id);

    Task<FetchResult> ITaskBacklogRunner.RunAsync(
        string taskId, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Enum.TryParse<FetchActionId>(taskId, out var id))
            throw new InvalidOperationException($"待办里的任务 id 解析不出动作：{taskId}");
        return RunAsync(id, new TaskRunArgs(Mode: FetchMode.FillBacklog), progress, ct);
    }

    /// <summary>
    /// 跑一个新式任务，并把它的事件桥回现有世界：
    ///
    ///   · <see cref="IFetchTask.OnProgress"/> / <see cref="IFetchTask.OnLiveness"/>
    ///     → 老的 <c>IProgress&lt;string&gt;</c>，于是 PlanRunner、日志窗、静默看门狗全都零改动
    ///   · <see cref="TaskRunResult"/> → <see cref="FetchResult"/>
    ///
    /// <paramref name="subscribe"/> 是给别的订阅者留的口子（正在执行任务表挂实时进度、
    /// UI 挂进度条）——事件是多播的，这里挂的和上面桥接的互不影响。
    /// </summary>
    public async Task<FetchResult> RunAsync(
        FetchActionId id, TaskRunArgs args, IProgress<string>? progress,
        CancellationToken ct, Action<IFetchTask>? subscribe = null)
    {
        var task = Create(id) ?? throw new InvalidOperationException($"没注册的新式任务：{id}");

        if (progress != null)
        {
            task.OnProgress += p => progress.Report(p.ToString());
            task.OnLiveness += l => progress.Report(l.Text);
        }
        subscribe?.Invoke(task);

        var result = await task.RunAsync(args, ct);
        return result.ToFetchResult();
    }
}
