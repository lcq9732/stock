using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 「这一轮跑完，失败名单该变成什么样」——纯判据，零 IO。
///
/// 2026-09-21 抽出来。在这之前同一条规则有两份实现：<c>FetchOrchestrator</c> 里的
/// <c>ComputeUpdatedFailedCodes</c>，和 <c>NetInflowTask.SaveFailedTodo</c> 里手写的那三行。
/// 两份没分叉纯属运气——它的错法是静默的（见下面那条 ⚠）。
///
/// ════ 规则只有一句，但那一句是重点 ════
/// **只动"本轮碰过"的那些票**：碰过且这次成了 → 移出名单；碰过且这次又失败 → 留着；
/// **没碰到的一律保持原样**。
///
/// ⚠ 别简化成"清空重写"。那样一来，任何一次**部分跑**（分批到点收尾、中途停止、
/// 只补某几只）都会把没轮到的那些票从名单里抹掉——名单空了，界面显示"没有待办"，
/// 而那些票的数据其实一直缺着，再也没有人去补。
/// </summary>
public static class FailedTodoRule
{
    /// <summary>算出新的失败名单。</summary>
    /// <param name="current">名单里现在挂着哪些。</param>
    /// <param name="attempted">这一轮**真去抓过**的（不管成没成）。</param>
    /// <param name="failedThisRun">这一轮抓失败的。</param>
    public static List<string> Update(
        IEnumerable<string> current,
        IReadOnlyCollection<string> attempted,
        IReadOnlyCollection<string> failedThisRun)
    {
        var stillFailed = new HashSet<string>(current, StringComparer.Ordinal);
        stillFailed.ExceptWith(attempted);
        stillFailed.UnionWith(failedThisRun);
        return stillFailed.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 按上面的规则把某个任务的失败名单写回 manifest（<see cref="Manifest"/> 是纯模型，
    /// 存盘由调用方负责）。
    ///
    /// <paramref name="taskId"/> 就是**这件事该归谁补**——见 <see cref="RetryTaskIds"/>。
    /// 填错的后果是那些票永远认领不到自己的待办（后复权失败的票被按前复权重抓，补不回来）。
    /// </summary>
    public static void SetFailed(
        Manifest manifest, string taskId,
        IReadOnlyCollection<string> attempted, IReadOnlyCollection<string> failedThisRun)
    {
        var current = manifest.Todo(taskId, RetryTodoKind.Failed)?.Targets.Select(t => t.Code) ?? [];
        var updated = Update(current, attempted, failedThisRun);
        manifest.SetTodo(taskId, RetryTodoKind.Failed,
                         updated.Select(c => new RetryTarget { Code = c }).ToList());
    }
}
