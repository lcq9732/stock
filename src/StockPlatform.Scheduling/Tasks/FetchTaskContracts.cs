using StockPlatform.Data.Orchestration;

namespace StockPlatform.Scheduling.Tasks;

/// <summary>
/// 新式抓取任务的契约（2026-09-08）——**新任务一律按这个形状写，老任务维持原样**。
///
/// ════ 跟 doc/fetcher-task-refactor-design.md 的关系 ════
/// 那份文档（2026-09-05 归档）设计了任务/数据源/存储三层重构，结论是"改动面过大、暂不实施，
/// 代码一行未动"。2026-09-08 定的折中：**不迁老任务，但新任务按那个形状落地**——否则等真要
/// 重构时，新写的任务反而成了第三种形状，迁移成本更大。
///
/// 相对文档原稿改了三处（用户 2026-09-08 拍板，已写回文档 3.1/3.3 节）：
///
/// ① **准入判断移出任务**。原稿里任务自己实现 <c>CheckCanRunAsync</c>、基类负责登记数据源占用。
///    现实早就跑在前面了：<see cref="SourceOccupancy"/>（2026-09-04）和 <see cref="SourceAdmission"/>
///    （2026-09-05）已经在调度侧做完了"谁占着哪个源、让路还是抢占"，占用的释放也在调度侧的
///    finally 里。所以协调归调度、任务只做自己的活。
///
///    ⚠ 界线在哪：**只有任务自己知道的前提仍归任务**——本地还没有K线所以定不了补齐起点、
///    没配置那个数据源、日历已经是最新的、这一天该不该发请求。这些不是"准入"，是任务开工后的
///    第一步结论，返回 <see cref="TaskRunResult.NothingToDo"/> 或 Failed，不是被拒绝。
///    调度侧无从判断也不该判断它们。
///
/// ② **没有 StopAsync**。停止＝取消 <see cref="CancellationToken"/>，任务在自己的 finally 里
///    收尾（落库、记水位线）后正常返回 <see cref="TaskState.Stopped"/>。理由见 SourceOccupancy 的
///    类注释：40 个手写的 Stop 方法漏一个就永远等不到那个 true。
///
/// ③ **进度从"传进去"改成"发出来"**。原来是 <c>IProgress&lt;string&gt;</c> 一路传到底；现在任务
///    只管往外广播，调度类、UI、正在执行任务表、静默看门狗各自订阅、各取所需。
/// </summary>
public interface IFetchTask
{
    /// <summary>任务身份。直接复用现有的 <see cref="FetchActionId"/>——另造一套 id 就得维护两套映射。</summary>
    FetchActionId Id { get; }

    /// <summary>
    /// **真进展**：抓完一批、写了多少行。<see cref="QuietWatchdog"/> 吃的就是这个信号。
    /// </summary>
    event Action<TaskProgress>? OnProgress;

    /// <summary>
    /// **只证明进程还活着**的定时播报（"这一步已用时 N 分钟"）。
    ///
    /// 跟 <see cref="OnProgress"/> 分成两路事件、而不是加一个 bool 字段，是因为
    /// <see cref="QuietWatchdog"/> 的类注释里写死了一条：这种播报**不能**喂给看门狗——
    /// 卡在一把 SQLite 写锁上时它照样每 30 秒吐一句，接进去看门狗就成了摆设。
    /// 分两个事件，订阅者不可能订错。
    /// </summary>
    event Action<TaskLiveness>? OnLiveness;

    /// <summary>状态变化。UI 刷行状态、调度类记结果。</summary>
    event Action<TaskStateChanged>? OnStateChanged;

    /// <summary>
    /// 这个任务能不能**自己补待办**（<see cref="FetchMode.FillBacklog"/>）。默认 false。
    ///
    /// false＝待办的编排仍在 <c>FetchOrchestrator.RunFillBacklogAsync</c> 那边（席位/大宗/
    /// 龙虎榜的残缺日就是这样，按天重抓的动作两边共用一个写入器）。
    ///
    /// ⚠ **分派按这个属性走，不按"是不是新式任务"走**（2026-09-18）：粗暴地让 registry 里的
    /// 任务全部自己接管的话，那三个没实现 FillBacklog 的任务会收到这个模式、返回空、
    /// 报一句"没有欠着的"——**待办永远补不上而且一声不吭**。
    /// 见 doc/dividend-task-design.md §9。
    /// </summary>
    bool HandlesBacklog => false;

    /// <summary>干活。"能不能跑"已经由调度侧判完了，这里直接开工。</summary>
    Task<TaskRunResult> RunAsync(TaskRunArgs args, CancellationToken ct);
}

/// <summary>
/// 一条进展。比原来的裸字符串多了 Done/Total/Phase——UI 想画进度条不用再从文本里抠数字。
/// </summary>
/// <param name="Text">给人看的一句话。桥接到老的 <c>IProgress&lt;string&gt;</c> 时用的就是它。</param>
/// <param name="Done">已完成多少（不适用就留空）。</param>
/// <param name="Total">总共多少（事先不知道就留空）。</param>
/// <param name="Phase">当前阶段名，比如"官方日历"/"本地归纳"/"对账"。</param>
public sealed record TaskProgress(string Text, int? Done = null, int? Total = null, string? Phase = null)
{
    public override string ToString() => Done is { } d && Total is { } t ? $"{Text}（{d}/{t}）" : Text;
}

/// <summary>"我还在这一步，已用时这么久"——只证明活着，不证明有前进。见 <see cref="IFetchTask.OnLiveness"/>。</summary>
public sealed record TaskLiveness(string Text, TimeSpan Elapsed);

/// <summary>
/// 任务的结局。**没有 Refused**——拒绝发生在调度侧（任务压根没被调用），
/// 那边用 <see cref="AdmissionKind"/> 的 GaveWay/PreemptTimedOut 表达。
/// </summary>
public enum TaskState
{
    /// <summary>开工了。</summary>
    Running,
    /// <summary>这一轮做完了（可能什么都没得做，见 <see cref="TaskRunResult.NothingToDo"/>）。</summary>
    Completed,
    /// <summary>开工了但出错。</summary>
    Failed,
    /// <summary>被取消，已收尾。</summary>
    Stopped,
}

/// <summary>状态变化事件的载荷。</summary>
public sealed record TaskStateChanged(FetchActionId Id, TaskState State, string? Message = null);

/// <summary>
/// 运行期参数。**不进构造函数**——这样任务实例跟"跑哪一轮"无关。
/// </summary>
/// <param name="Mode">增量 / 只抓某一天 / 首次整段回补。</param>
/// <param name="Day">「只抓某一天」用的日期；null＝今天。</param>
/// <param name="Deadline">空闲窗口要在这个点前收尾；null＝不限。</param>
/// <param name="MaxItems">分批跑：本轮最多做多少批；null＝不限。</param>
/// <param name="Manual">手动触发（影响日志措辞，也留给将来"要不要弹框问"用）。</param>
public sealed record TaskRunArgs(
    FetchMode Mode = FetchMode.Incremental,
    DateOnly? Day = null,
    DateTime? Deadline = null,
    int? MaxItems = null,
    bool Manual = false);

/// <summary>跑完之后的统计，给 <c>OnCompletedAsync</c> 用。</summary>
/// <param name="Batches">抓了几批。</param>
/// <param name="Items">一共多少条。</param>
/// <param name="Elapsed">用时。</param>
public sealed record TaskRunStats(int Batches, int Items, TimeSpan Elapsed);

/// <summary>
/// 任务跑完的结果。<see cref="ToFetchResult"/> 把它翻译成现有的 <see cref="FetchResult"/>，
/// 于是 PlanRunner 和界面完全不用改——这是"新任务和老任务并存"的关键接缝。
/// </summary>
public sealed record TaskRunResult(
    TaskState State,
    IReadOnlyList<string> Errors,
    bool NothingToDo = false,
    string? Progress = null,
    string? SkippedReason = null)
{
    public static TaskRunResult Ok(bool nothingToDo = false, string? progress = null)
        => new(TaskState.Completed, Array.Empty<string>(), nothingToDo, progress);

    /// <summary>
    /// 「这一轮**根本没开工**」——数据源在熔断、快照要等收盘清算这类。
    ///
    /// 必须跟「完成」分开（2026-09-11 补上，老编排层一直有这个字段、新框架漏了）：
    /// 记成完成的话界面上是个绿勾，而且 <c>FetchPlanItem.AlreadyRanOn</c> 只认完成——
    /// **今天就不会再来了**，等熔断过去也白搭。记成跳过，今天恢复之后还有机会补上。
    /// </summary>
    public static TaskRunResult Skipped(string reason, IReadOnlyList<string>? errors = null)
        => new(TaskState.Completed, errors ?? Array.Empty<string>(), SkippedReason: reason);

    public FetchResult ToFetchResult()
    {
        var r = new FetchResult
        {
            NothingToDo = NothingToDo, Progress = Progress, SkippedReason = SkippedReason,
            // 失败要带过去（2026-09-16）：骨架把异常吞了、只在这个 State 上留了痕，
            // 不传的话 PlanRunner 会把失败的轮次记成「完成」并显示成绿色（见 FetchResult.Failed）。
            Failed = State == TaskState.Failed,
        };
        r.Errors.AddRange(Errors);
        // 被停止不是失败：现有引擎靠 OperationCanceledException 区分，这里保持一致——
        // 调度侧接到 Stopped 时本来就在取消路径上，不需要再翻译成错误。
        return r;
    }
}
