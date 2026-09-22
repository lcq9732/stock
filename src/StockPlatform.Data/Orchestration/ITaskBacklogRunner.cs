namespace StockPlatform.Data.Orchestration;

// 【已删 2026-09-22】ITaskBacklogRunner ——「这个任务的待办由它自己补」的转交口。
// 它存在的唯一理由是：老【重新拉取失败】在编排层（Data）里按 taskId 循环，而新式任务的
// 注册表在 Scheduling，依赖方向是 Scheduling → Data，编排层引用不到它，只能定个端口回调。
// 那一项 2026-09-22 也改成了任务（StockPlatform.Tasks/RetryFailedTask），住在 Tasks 层、
// 看得见 Scheduling，直接用 IFetchTaskDispatcher 就行——不必再为了跨层把 id 降级成字符串。

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
