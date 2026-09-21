using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

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
/// <param name="manifestStore">
/// 用来记"这一项什么时候跑完的"（<see cref="Manifest.LastRunByTask"/>）——【数据状态】页那份
/// "最近任务运行"清单读的就是它。
///
/// ⚠ 2026-09-21 补：老路每项跑完都会写一条（<c>FetchOrchestrator.FinishFetchRun</c>），
/// 而骨架一直没做这件事，于是**每迁走一项，那一页就少一行**（迁走的 32 项全都不见了）。
/// 记在这里而不是每个任务里：所有新任务都从 <see cref="RunAsync"/> 过，一处写、34 项全覆盖，
/// 任务本身一行都不用改。传 null 就是不记（测试里用）。
/// </param>
public sealed class FetchTaskRegistry(IManifestStore? manifestStore = null)
    : IFetchTaskRegistry, ITaskBacklogRunner, ITaskRunner
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

    // ── ITaskRunner：给编排层触发一个任务用（眼下只有【拉取区间数据】末尾重合成板块指数）──

    Task<FetchResult> ITaskRunner.RunAsync(string actionId, IProgress<string>? progress, CancellationToken ct)
    {
        if (!Enum.TryParse<FetchActionId>(actionId, out var id))
            throw new InvalidOperationException($"认不出的动作：{actionId}");
        return RunAsync(id, new TaskRunArgs(), progress, ct);
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
            // Quiet 的进展只喂静默看门狗、不进日志（2026-09-19，见 TaskProgress.Quiet）。
            //
            // ⚠ 拿不到那条通道时是**整条丢掉**，不是"退化成写日志"：喂心跳的只有
            //    QuietWatchdog（它 Wrap 出来的 progress 就实现了这个接口），拿不到就说明
            //    这条路上根本没有看门狗——手动点【执行】那条路就是这样——那里丢掉没有代价，
            //    而"退化成写日志"会让同一个任务手动跑时日志密度变成十倍（实测 186 行一轮）。
            //    真进展该有的那几行仍由任务用非 Quiet 的 Report 打出来，两条路一样密。
            task.OnProgress += p =>
            {
                if (!p.Quiet) progress.Report(p.ToString());
                else if (progress is QuietWatchdog.IBeatOnlySink beat) beat.BeatOnly(p.ToString());
            };
            task.OnLiveness += l => progress.Report(l.Text);
        }
        subscribe?.Invoke(task);

        var result = await task.RunAsync(args, ct);
        RecordRun(id, result);
        return result.ToFetchResult();
    }

    /// <summary>
    /// 记一条"这一项刚跑完"。键用**目录里的中文名**，跟老路
    /// （<c>FinishFetchRun</c> 里的 <c>fetchKind</c>）一致——两边写同一个键，
    /// 【数据状态】页才不会把同一项显示成两行。
    ///
    /// 取消不记（那一路是抛 <see cref="OperationCanceledException"/> 出去的，根本走不到这里）；
    /// 写 manifest 失败也不许影响任务结果——记录是给人看的，不该把一轮成功的抓取判成失败。
    /// </summary>
    private void RecordRun(FetchActionId id, TaskRunResult result)
    {
        if (manifestStore == null) return;
        try
        {
            var manifest = manifestStore.Load();
            manifest.LastRunByTask[FetchTaskCatalog.Info(id).Name] = new TaskRunRecord
            {
                At = DateTime.Now,
                ErrorCount = result.Errors.Count,
            };
            manifestStore.Save(manifest);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FetchTaskRegistry] 记 LastRunByTask 失败已忽略：{ex.Message}");
        }
    }
}
