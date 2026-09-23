using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Services;

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
    /// ⚠ **false 现在意味着"这一项的待办没人补"**（2026-09-22 起）。原先它的含义是
    /// "待办的编排仍在 <c>FetchOrchestrator.RunFillBacklogAsync</c> 那边"——那个中转
    /// 随【重新拉取失败】迁成任务（<c>RetryFailedTask</c>）一起删了，编排层不再参与待办分派。
    /// 所以现在收到这个模式而没声明 true 的项，界面会当场报一句
    /// "没有声明自己补待办，它欠着的那些补不了"，把配置问题喊出来（见
    /// <c>MainViewModel.DispatchPlanActionAsync</c> 开头那条分支）。
    ///
    /// ⚠ **分派按这个属性走，不按"是不是新式任务"走**（2026-09-18）：粗暴地让 registry 里的
    /// 任务全部自己接管的话，没实现 FillBacklog 的任务会收到这个模式、返回空、
    /// 报一句"没有欠着的"——**待办永远补不上而且一声不吭**。这正是上面那句告警要拦的。
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
/// <param name="Quiet">
/// 真进展，但**不必单独写一行日志**（2026-09-19）——喂静默看门狗就够了，界面进度条照收。
/// 用在"一批很快、但要跑几百批"的活上：日志按原来的稀疏间隔打，心跳按批打，
/// 于是它既不刷屏、也不会被判成卡死。详见 <see cref="QuietWatchdog.IBeatOnlySink"/>。
///
/// ⚠ 只影响**日志密度**，不影响"算不算进展"：Quiet 的进展照样喂狗。真正不喂狗的是
/// <see cref="IFetchTask.OnLiveness"/> 那一路，两者别混。
/// </param>
public sealed record TaskProgress(string Text, int? Done = null, int? Total = null, string? Phase = null,
                                  bool Quiet = false)
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
/// <param name="LookbackYears">
/// 「新标的补 N 年」（2026-09-21 随K线任务迁移加）——**只决定"本地一根都没有的标的第一次抓多久历史"**。
/// 已经抓过的永远从自己上次抓到那天续，跟它无关；「首次整段回补」那个模式也不看它
/// （那个模式从开市首日抓起，见 <see cref="IncrementalWindowCalculator.AShareMarketOpen"/>）。
/// null＝任务自己用默认值（3 年）。计划里那一行填的值由界面传进来。
/// </param>
public sealed record TaskRunArgs(
    FetchMode Mode = FetchMode.Incremental,
    DateOnly? Day = null,
    DateTime? Deadline = null,
    int? MaxItems = null,
    bool Manual = false,
    int? LookbackYears = null,
    /// <summary>
    /// 中标/订单公告的关键词（2026-09-21 随公告任务迁移加）——计划里那一行自己填的，逗号分隔。
    /// 留空**不是错**，是"这一轮不抓公告"，任务会在日志里说清楚原因。只有那一项用得上。
    /// </summary>
    IReadOnlyList<string>? Keywords = null,
    /// <summary>
    /// 「整段」从哪一年起（2026-09-22 随【拉取区间数据】改成分派器加）。
    ///
    /// ⚠ **只跟 <see cref="FetchMode.FirstBackfill"/> 搭配使用**：那个模式本来就是
    /// "不看水位线、只补缺的"，而「整段」有多长原本由各任务自己的数据源决定
    /// （K线从开市首日、分档资金流只有 120 个交易日）。这两个参数把那个"整段"
    /// **收窄**成调用方指定的年份区间——是同一种行为的窗口更小，不是另一种行为。
    /// 所以没有为它新开一个 FetchMode：模式是会两两组合的，多一个就多一片要想清楚的格子。
    ///
    /// null＝按各任务自己的"整段"。<see cref="FetchMode.Incremental"/> 下这两个值无意义、
    /// 任务应当忽略它们（增量永远是从各标的自己的水位线往后续）。
    /// </summary>
    int? YearStart = null,
    /// <inheritdoc cref="YearStart"/>
    int? YearEnd = null,
    /// <summary>
    /// 【覆盖重抓前复权】（2026-09-22 同上）——**不看本地已有什么、整段按数据源当前基准重写**。
    ///
    /// 用来抹平历史上分批入库造成的复权基准接缝：数据源的前复权是「原价 − 之后累计分红送配」，
    /// 某只票一分红，它全部历史的前复权值就都变了；而本地历史是分批入库的，
    /// 接缝处会出现假跳空（实测有票虚增 50%）。
    ///
    /// ⚠ 它跟 <see cref="FetchMode.FirstBackfill"/>「只补缺的」正好相反，**只有前复权那一路吃**，
    /// 别的任务忽略即可。走这条路时连 <c>BarProbeFloor</c> 水位也不看——
    /// 否则"抹接缝"的活会被"这段已经探明没有"给跳过。
    /// </summary>
    bool OverwriteQfq = false);

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
    ///
    /// ⚠ **反过来也别滥用**（2026-09-21 实测踩到）："今天还会再来"意味着计划引擎**立刻**
    /// 再排一次。所以只有**条件可能变**的情形才配用它（数据源熔断、要等收盘清算、接口探测失败）。
    /// 「所有标的都已是最新」「没有欠着的待办」这类**今天再来也是同一个结果**的，
    /// 用 <see cref="NothingToDo"/>——写成 Skipped 会空转到被"连着 5 轮瞬间跑完"那道护栏拦下。
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
