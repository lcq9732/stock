namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 「这个任务的待办由它自己补」的转交口（2026-09-18）。
///
/// ════ 为什么需要它 ════
/// 待办有两条入口：计划项设成【只补待办】走界面那一层的分派，而【重新拉取失败股票】
/// 是在 <c>FetchOrchestrator.RunRetryFailedInternalAsync</c> 里按 <c>DispatchOrder</c> 挨个跑
/// taskId 的，**根本不经过界面**。所以把某一项的补法搬进新式任务之后，编排层必须能回调过去，
/// 否则【重新拉取失败股票】会**静默跳过**那一项。
///
/// 直接调不行：新式任务的注册表在 Scheduling 层，而 Scheduling 引用 Data——反过来引用会成环。
/// 所以接口定在这里（Data），实现在 Scheduling（<c>FetchTaskRegistry</c>），
/// 由 App 组装时注入到 <see cref="FetchOrchestrator.BacklogRunner"/>。
///
/// 见 doc/dividend-task-design.md §9。
/// </summary>
public interface ITaskBacklogRunner
{
    /// <summary>这个 taskId（<c>FetchActionId</c> 的枚举名）的待办是不是由任务自己补。</summary>
    bool Handles(string taskId);

    /// <summary>让那个任务以 <c>FetchMode.FillBacklog</c> 跑一轮。</summary>
    Task<FetchResult> RunAsync(string taskId, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// 让编排层能**触发一个新框架任务**（2026-09-21）。
///
/// 起因：【板块指数合成】迁去 StockPlatform.Tasks 之后，【拉取区间数据】末尾那句"按新补齐的
/// 个股日K重新合成板块指数"就调不到了——编排层在 Data、任务在 Tasks，依赖方向是
/// Tasks → Data，反过来引用不到。
///
/// 跟 <see cref="ITaskBacklogRunner"/> 同一个套路：**端口定义在 Data、实现是
/// <c>FetchTaskRegistry</c>、由组合根接上**。没接上时调用方要说一句，别静默少干活。
/// </summary>
public interface ITaskRunner
{
    /// <summary>跑一个任务（<paramref name="actionId"/> 是 <c>FetchActionId</c> 的枚举名）。</summary>
    Task<FetchResult> RunAsync(string actionId, IProgress<string>? progress, CancellationToken ct);
}
