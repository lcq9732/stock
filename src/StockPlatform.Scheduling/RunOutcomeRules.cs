using StockPlatform.Data.Orchestration;

namespace StockPlatform.Scheduling;

/// <summary>
/// 「这一轮到底算什么结果」——把 <see cref="FetchResult"/> 翻译成 <see cref="RunOutcome"/>
/// 的**唯一判据**（2026-09-21）。
///
/// ════ 为什么要单独拎出来 ════
/// 同一个判据原来有两份实现，跑同一个任务的两条路各写各的：
///   · 计划自动跑 → <see cref="PlanRunner"/>.FinishRunAsync，判了 SkippedReason 和 Failed；
///   · 手动点【执行】/自动重试 → Fetcher 的 MainViewModel.RunPlanItemCoreAsync，
///     **只判了 SkippedReason，漏了 <see cref="FetchResult.Failed"/>**。
///
/// 漏的那一半 2026-09-21 咬了一口：【分档资金流快照】20:22 手动重跑，push2delay 第 1 页就被
/// 东财切断、任务如实返回 <c>Failed</c>，而界面上记的是绿勾「✓ 20:22 完成」。
/// 显示难看还是小事——<see cref="FetchPlanItem.AlreadyRanOn"/> 只认 <see cref="RunOutcome.Ok"/>，
/// 记成完成之后**计划当天就不会再自动重跑这一项**，而它恰好是全库唯一漏一天就永久取不回来的
/// 数据（快照接口只给最近一个交易日）。
///
/// 所以判据只留这一份，两条路都从这里问。
/// </summary>
public static class RunOutcomeRules
{
    /// <summary>
    /// 这一轮算什么结果。
    ///
    /// ⚠ 顺序要紧：<see cref="FetchResult.SkippedReason"/> 压过 <see cref="FetchResult.Failed"/>，
    /// 跟 <see cref="PlanRunner"/> 原本的分支顺序一致——「根本没开工」（数据源还在限流熔断里）
    /// 是"今天还有机会补"，记成失败会让它在界面上变成一笔要人管的账，而它并不需要人管。
    ///
    /// <paramref name="result"/> 为 null＝这条路没返回结果对象（老编排层的任务），按完成算，
    /// 行为跟加这个判据之前一模一样。
    /// </summary>
    public static RunOutcome Classify(FetchResult? result) =>
        result?.SkippedReason is { } ? RunOutcome.Skipped
        : result is { Failed: true } ? RunOutcome.Failed
        : RunOutcome.Ok;

    /// <summary>
    /// 整项失败时写进状态列的那句话：拿第一条错误，一条都没有才用兜底文案。
    /// （任务报告 Failed 却一条错误都不给，本身就是任务写得不对，但不能因此在界面上留白。）
    /// </summary>
    public static string FailureReason(FetchResult result) =>
        result.Errors.Count > 0 ? result.Errors[0] : "任务报告失败";

    /// <summary>
    /// 整项失败时的错误条数：**至少 1**。
    /// 「这一项没干成」本身就是一条账，不能因为任务没往 Errors 里塞东西就显示成「0 条错误」。
    /// </summary>
    public static int FailureErrorCount(FetchResult result) => Math.Max(result.Errors.Count, 1);
}
