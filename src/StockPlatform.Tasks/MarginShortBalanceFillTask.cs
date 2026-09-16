using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【融券余额补算】（2026-09-16）——把**沪市**行缺失的融券余额按交易所官方公式算出来。
/// **纯本地查库，一个请求都不发。**
///
/// 修的是什么、凭什么能算、为什么只动沪市，全在 <see cref="SqliteMarginShortBalanceFiller"/>
/// 的类注释里。这里只说调度上的事：
///
/// **什么时候跑**：并入每日流程，排在【融资余额】和【个股日K】**之后**——它要拿当天的收盘价
/// 去乘当天的融券余量，两样都落库了才算得出来。单独跑也行（全历史回填就是单独跑一次）。
///
/// **幂等**：补过的行不再满足"缺值"判据，重跑是空转。中断直接重跑，不用记断点——
/// 按交易日分批提交，最多丢当天那一批。
///
/// ⚠ 这一项**不**负责深市：深交所那张 xlsx 第 6 列直接给了融券余额，是源头的真值，
/// 而且深市有 31% 的行本来就是 0（当天确实没融券余量）。把"是不是 0"当判据会把真 0 也改掉，
/// 所以判据必须先分市场——见 Filler 里用 MarketClassifier 过的那张临时表。
/// </summary>
public sealed class MarginShortBalanceFillTask(SqliteMarginShortBalanceFiller filler)
    : FetchTaskBase<MarginShortBalanceFillResult>
{
    public override FetchActionId Id => FetchActionId.StepFillShortBalance;

    protected override async IAsyncEnumerable<IReadOnlyList<MarginShortBalanceFillResult>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        Report("开始补算沪市融券余额（余量 × 当日收盘价，交易所官方公式）...");

        // ⚠ 整段是同步的重活（全历史 270 万行、几分钟），**必须自己推线程池**——
        // 骨架不会替你推，同步跑会把 UI 整段冻死（见 feedback_task_must_offload_heavy_sync）。
        var result = await Task.Run(() => filler.Fill(s => Report(s), sinceDate: null, ct), ct);

        if (result.Rows == 0)
        {
            Report("没有待补算的行（这一项是幂等的，跑过就会是这个结果）");
            yield break;
        }
        yield return new[] { result };
    }

    /// <summary>补算就是 UPDATE，落库在 <see cref="SqliteMarginShortBalanceFiller"/> 里做完了。</summary>
    protected override Task SaveBatchAsync(
        IReadOnlyList<MarginShortBalanceFillResult> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        Report("补算完成。沪市的「融券余额」从此跟深市同口径，资金面诊断的融券一行不再是空的。");
        return Task.FromResult<TaskRunResult?>(null);
    }
}
