using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>一个口径的修正结果。</summary>
public sealed record VolumeUnitFixResult(string Granularity, int Rows);

/// <summary>
/// 【统一成交量单位】（2026-09-10）——把 <c>Bar.volume</c> 里按"股"存进去的历史行改成"手"。
/// **纯本地查库，一个请求都不发。**
///
/// ════ 修的是什么 ════
/// 三个数据源的成交量口径不一样（腾讯主板给手、科创板给股；新浪一律给股；东财都给手），
/// 而两个 fetcher 原来把源给的数字原样入库，于是这一列里并存两种单位——2026-09-08 那天
/// 全市场日线里，主板/创业板/北交所 4949 只是手，**科创板 613 只是股**，差 100 倍。
/// 解析层已经归一化（见 <c>BarVolumeUnit</c>），这一项负责已经躺在库里的历史。
///
/// ════ 为什么这个错一直没被发现 ════
/// 单票内部的分析用的都是**比值**（放量比、量能排序、K线图），单位约掉了，怎么看都正常。
/// 只有跨股票累加才露馅——<c>BoardIndexSynthesizer</c> 把成分股成交量加总，
/// 含科创板的板块因此常年虚高 100 倍，而这不会报任何错。
///
/// ════ 幂等，可以反复跑 ════
/// 判据是逐行的量额比（<c>amount/(volume×close) ∈ [0.8,1.25]</c> 才算"股"），
/// 改完的行比值变成 ≈100，再跑一次不会被选中。所以中断了直接重跑，不用记断点。
///
/// ⚠ 跑完**要重算板块指数**（【板块指数合成】）：它存的成交量是按旧单位加总出来的。
/// </summary>
public sealed class BarVolumeUnitFixTask(SqliteBarVolumeUnitFixer fixer) : FetchTaskBase<VolumeUnitFixResult>
{
    public override FetchActionId Id => FetchActionId.StepFixVolumeUnit;

    protected override async IAsyncEnumerable<IReadOnlyList<VolumeUnitFixResult>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        // ⚠ 故意**不先调 Preview**：每次扫描都是 2540 万行的全表扫（granularity 上没索引，
        // 那条要人点【优化数据库】才建），先数一遍再改一遍等于把最慢的部分做两次。
        // Fix 自己会边改边报每个口径的行数，够看了。
        Report("开始扫描（Bar 表 2540 万行、granularity 无索引，头一步要几分钟）...");

        var fixedRows = await Task.Run(() => fixer.FixAsync(s => Report(s), ct), ct);
        int total = fixedRows.Values.Sum();
        if (total == 0)
        {
            Report("没有按\"股\"存的行，无需修正（这一项是幂等的，跑过就会是这个结果）");
            yield break;
        }
        Report($"共修正 {total} 行");
        yield return fixedRows.Select(kv => new VolumeUnitFixResult(kv.Key, kv.Value)).ToList();
    }

    /// <summary>修正是 UPDATE，落库在 <see cref="SqliteBarVolumeUnitFixer"/> 里就做完了，这里没有额外的存。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<VolumeUnitFixResult> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        Report("修正完成。⚠ 记得再跑一次【板块指数合成】——它的成交量是按旧单位加总的。");
        return Task.FromResult<TaskRunResult?>(null);
    }
}
