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
public sealed class FetchTaskRegistry : IFetchTaskRegistry
{
    private readonly Dictionary<FetchActionId, Func<IFetchTask>> _factories = new();

    public void Register(FetchActionId id, Func<IFetchTask> factory) => _factories[id] = factory;

    public bool Has(FetchActionId id) => _factories.ContainsKey(id);

    public IFetchTask? Create(FetchActionId id)
        => _factories.TryGetValue(id, out var f) ? f() : null;

    public IReadOnlyCollection<FetchActionId> Registered => _factories.Keys;

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
