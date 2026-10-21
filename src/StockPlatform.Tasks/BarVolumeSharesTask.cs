using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【成交量改存股】（2026-10-20，见 doc/unit-normalization-inventory.md）——<c>Bar.volume</c> 全表 ×100，
/// 手 → 股（ETF 份）。一次性、纯本地、一个请求都不发。活在 <see cref="SqliteBarVolumeMigration"/> 里，
/// 这里只负责分批调它、报进度、在批与批之间响应停止。
///
/// 新版 Fetcher 启动时发现没做完就自动开跑（MainViewModel）；做完之前别的任务被
/// <see cref="BarVolumeMigrationGate"/> 挡住。停止只在两批之间生效——一批是一个事务，
/// 掐在中间也只是回滚这一批，但没必要：一批只有几秒。
/// </summary>
public sealed class BarVolumeSharesTask(SqliteBarVolumeMigration migration) : FetchTaskBase<BarVolumeMigrationChunk>
{
    /// <summary>每做完这么大比例打一条日志，其余批次只喂看门狗。</summary>
    private const double ReportEveryFraction = 0.05;

    public override FetchActionId Id => FetchActionId.StepBarVolumeShares;

    protected override async IAsyncEnumerable<IReadOnlyList<BarVolumeMigrationChunk>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        if (await Task.Run(migration.IsDone, ct))
        {
            Report("成交量已经是按股存的（迁移做过了），这一项什么都不用做。");
            yield break;
        }

        var (cursor, max) = await Task.Run(migration.Progress, ct);
        Report(cursor == 0
            ? $"开始把 Bar.volume 从手改成股（×100）：按 rowid 顺序每 {SqliteBarVolumeMigration.DefaultChunkRowIds:N0} 个一批，共约 {max:N0} 个。"
              + "做完之前别的任务不开工。"
            : $"接着上次做：已做到 rowid {cursor:N0} / {max:N0}（{Pct(cursor, max)}）。");

        double nextReport = (double)cursor / Math.Max(1, max) + ReportEveryFraction;
        long rows = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();       // 只在批与批之间停
            var chunk = await Task.Run(() => migration.RunChunk(), ct);
            rows += chunk.Rows;
            double frac = (double)chunk.Cursor / Math.Max(1, chunk.MaxRowId);
            var text = $"成交量改存股 {Pct(chunk.Cursor, chunk.MaxRowId)}（本轮已改 {rows:N0} 行）";
            if (chunk.Done || frac >= nextReport)
            {
                Report(text);
                nextReport = frac + ReportEveryFraction;
            }
            else ReportQuiet(text);

            yield return [chunk];
            if (chunk.Done) break;
        }
        Report($"成交量改存股完成：本轮改了 {rows:N0} 行。别的任务从现在起放行。");
    }

    /// <summary>落库在 <see cref="SqliteBarVolumeMigration.RunChunk"/> 里就做完了（改值和游标同一个事务）。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<BarVolumeMigrationChunk> batch, CancellationToken ct)
        => Task.CompletedTask;

    private static string Pct(long done, long total) => total <= 0 ? "100%" : $"{100.0 * done / total:0.0}%";
}

/// <summary>
/// 【成交量改存股】没做完之前的全局准入闸（挂在 <see cref="FetchTaskRegistry.RunGate"/> 上）。
/// 做到一半时同一只票新旧两种单位混着：抓进来的新行是股、老行有的还是手；板块合成、体检、
/// ETF 换手率都会算错。所以除了迁移任务自己，一律记成「本轮没开工」，做完计划自动接上。
///
/// 做完之后结果缓存住，不再查库。
/// </summary>
public sealed class BarVolumeMigrationGate(SqliteBarVolumeMigration migration)
{
    private volatile bool _done;

    public string? Check(FetchActionId id)
    {
        if (_done || id == FetchActionId.StepBarVolumeShares) return null;
        if (migration.IsDone())
        {
            _done = true;
            return null;
        }
        return "【成交量改存股】还没做完（库里同一只票新旧两种单位混着），做完之前别的任务不开工";
    }
}
