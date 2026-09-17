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
/// 去乘当天的融券余量，两样都落库了才算得出来。
///
/// **两档模式**：
///   · 【日常增量】/【首次整段回补】只补缺值，幂等、空转，适合每天跑
///   · 【彻底重查】**重算已有值**——口径改过之后必须用这一档，否则旧的错值永远留在库里
///     （2026-09-16 就踩了：第一版取价用前复权 day，已填的 263 万行偏低，茅台早年低到 18%）
///
/// **幂等**：增量模式下补过的行不再满足"缺值"判据，重跑是空转。中断直接重跑，不用记断点——
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
        // 【彻底重查】= **重算已有值**，【日常增量】/【首次整段回补】= 只补缺值。
        //
        // ⚠ 模式选 Thorough 不选 FirstBackfill，是按项目 2026-09-11 定的词义分界
        // （见 FetchMode.Thorough 的注释）：**彻底重查＝不管原来有没有、全部重来一次**，
        // 只补缺、不动已有的那种才是 FirstBackfill。这里要的正是前者。
        //
        // 为什么需要"重算"这一档（2026-09-16 加）：2026-09-16 第一次全历史回填时，取价用的是
        // granularity='day'，而那是**前复权**（减法式）——高分红老股的历史价被减得很低、早年甚至
        // 为负。已经填进去的 263 万行因此偏低（茅台实测 2020-06-01 低 18%、2026-06-01 低 2.1%，
        // 越往前越错）。取价口径随后改成 day_raw 优先，但补算器是"缺值才补"的幂等设计，
        // **重跑不会修正已有的错值**，所以必须有一档能强制重算。
        //
        // 日常增量不能用重算：全量 3999 个交易日跑一趟 48 分钟，日更只需要补新抓进来的那一天。
        bool recompute = args.Mode == FetchMode.Thorough;
        Report(recompute
            ? "开始**重算**沪市融券余额（余量 × 当日收盘价，取价走不复权 day_raw）—— "
              + "已有值也会被覆盖，全历史约 48 分钟"
            : "开始补算沪市融券余额（余量 × 当日收盘价，交易所官方公式）—— 只补缺值");

        // ⚠ 整段是同步的重活（全历史 270 万行、几十分钟），**必须自己推线程池**——
        // 骨架不会替你推，同步跑会把 UI 整段冻死（见 feedback_task_must_offload_heavy_sync）。
        var result = await Task.Run(
            () => filler.Fill(s => Report(s), sinceDate: null, ct, recompute), ct);

        if (result.Rows == 0)
        {
            Report(recompute
                ? "没有可重算的行——沪市两融行里没有带融券余量的记录，这不正常，请检查数据"
                : "没有待补算的行（这一项是幂等的，跑过就会是这个结果）");
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
