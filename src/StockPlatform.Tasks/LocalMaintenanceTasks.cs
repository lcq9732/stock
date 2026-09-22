using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

// ════════════════════════════════════════════════════════════════════════
//  最后 5 项本地维护类任务（2026-09-22 从编排器迁到新框架）
//
//  它们都**不联网**、都很短（5 秒到几分钟），单看每一项迁移的收益都不大——
//  真正的收益是**迁完之后编排层的分派整块消失**：界面那个 switch 没有剩下的 case 了，
//  FetchOrchestrator 不再是任何一项的执行入口。
//
//  放在同一个文件里是因为每个都只有几十行、而且是同一类东西（本地维护）；
//  真要长起来了再拆。
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// 【回填"无更早数据"水位】——不发一个请求，直接从本地已有历史推出每只票的
/// <c>BarProbeFloor</c> 水位。判据在 <see cref="ProbeFloorPlanner.PlanFromLocalHistory"/>。
///
/// 为什么值得跑一次：【拉取区间数据】往前补历史时，一只 2020 年上市的票被请求 1990~2016 必然返回空，
/// 而这个结论不落库的话每次重跑都要重新试一遍——2026-09-07 实测一轮 5558 只 × 3 个粒度、
/// 四个半小时、写入为零。
///
/// ⚠ 三路（前复权/后复权/不复权）最早一根**不一致**的票不填：那说明其中某一路确实还缺前段，该抓。
/// 填了就是把它的历史永久跳过，而且不会有任何报错。
/// </summary>
public sealed class ProbeFloorBackfillTask(FetchPaths paths) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.StepFillProbeFloor;

    private string? _summary;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _summary = null;
        // 三次全表 GROUP BY 是同步重活，推线程池——骨架不替子类推，首个 await 之前干这些会冻住界面
        var written = await Task.Run(() => Run(ct), ct);
        if (written > 0) yield return [written];
    }

    private int Run(CancellationToken ct)
    {
        var bars = new SqliteBarRepository(paths.CurrentDb);
        var floors = new SqliteBarProbeFloorRepository(paths.CurrentDb);
        floors.EnsureSchema();

        Report("正在查本地三路（前复权/后复权/不复权）日K的最早一根……23GB 库上约需半分钟，不联网。");
        var eDay = bars.GetEarliestPeriodStartByCode(Granularity.Day);
        ct.ThrowIfCancellationRequested();
        var eHfq = bars.GetEarliestPeriodStartByCode(Granularity.DayHfq);
        ct.ThrowIfCancellationRequested();
        var eRaw = bars.GetEarliestPeriodStartByCode(Granularity.DayRaw);
        ct.ThrowIfCancellationRequested();

        var plan = ProbeFloorPlanner.PlanFromLocalHistory(eDay, eHfq, eRaw,
            Granularity.Day, Granularity.DayHfq, Granularity.DayRaw,
            out int agreed, out int disagreed, out int dayOnly);

        int before = floors.Count();
        foreach (var (gran, rows) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count > 0) lock (SqliteWriteGate.Local) floors.Record(rows, gran);
        }
        int after = floors.Count();

        Report($"三路最早一根一致的标的 {agreed} 只 → 已按它写入水位（三个粒度各 {agreed} 条，"
             + $"表里从 {before} 条变成 {after} 条）。这些票往后的区间回补连请求都不会发。");
        if (disagreed > 0)
            Report($"三路最早一根不一致的 {disagreed} 只**没有填**——那说明其中某一路确实还缺前段，该抓。"
                 + "下一次【拉取区间数据】会照旧请求它们。");
        if (dayOnly > 0)
            Report($"只有前复权、另两路一根都没有的 {dayOnly} 只也没填（同上，该抓）。");

        _summary = $"水位回填完成：表里 {before} → {after} 条";
        Report(_summary);
        return after - before;
    }

    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;   // Run 里已经落库了

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
        => Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, [], NothingToDo: stats.Items == 0, _summary));
}

/// <summary>
/// 【ETF指数映射】——按名称把 ETF 匹配到指数（"沪深300ETF华泰" → 沪深300），
/// 供「股票 → 指数 → ETF」反查。纯本地匹配、不联网。
/// 判据在 <see cref="EtfIndexMatcher"/>（Logic 层纯函数）——【指数成分/权重】末尾也会顺带重建一次，
/// 两边共用同一个匹配器。
/// </summary>
public sealed class EtfIndexMapTask(FetchPaths paths, IIndexConsRepository repository) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.StepEtfIndexMap;

    private string? _summary;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _summary = null;
        int n = await Task.Run(() =>
        {
            repository.EnsureSchema();
            var etfs = SqliteStockMetaUpsert.GetAllInstruments(paths.CurrentDb)
                .Where(x => x.Type == SqliteStockMetaUpsert.TypeEtf)
                .Select(x => (x.Code, x.Name)).ToList();
            if (etfs.Count == 0)
            {
                _summary = "本地还没有 ETF 名单，这一项没什么可做（先跑一次【ETF日K】）";
                Report($"（{_summary}）");
                return 0;
            }

            var matches = EtfIndexMatcher.Match(etfs, IndexCatalog.All.Select(i => (i.Code, i.Name)));
            lock (SqliteWriteGate.Local)
                repository.ReplaceEtfIndexMap(
                    matches.Select(m => (m.EtfCode, m.IndexCode, m.MatchType)).ToList());

            int matched = matches.Count(m => m.IndexCode != null);
            _summary = $"ETF→指数名称匹配：{etfs.Count} 只 ETF，匹配到 {matched}、"
                     + $"未匹配 {etfs.Count - matched}（未匹配多为债券/货币/黄金ETF，本就无A股成分）";
            Report(_summary);
            return matches.Count;
        }, ct);
        if (n > 0) yield return [n];
    }

    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
        => Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, [], NothingToDo: stats.Items == 0, _summary));
}

/// <summary>
/// 【重解析已有PDF】——把本地已下载的银行/券商/保险年报中报用**当前**规则重跑一遍，
/// 一个网络请求都不发。实现在 <see cref="BankReportReparser"/>。
///
/// 为什么单独成项：解析规则一直在改（版式差异不断暴露新坑——注释角标没清干净让拨备覆盖率
/// 变成 3.0、目录页页码被当成资本充足率），改完想全库重跑时，不该连带把联网下载那一大段也跑一遍。
///
/// ⚠ 纯 CPU（PDF 解析/OCR），必须推线程池——骨架不替子类推。
/// </summary>
public sealed class BankPdfReparseTask(FetchPaths paths) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.StepReparseBankPdf;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var repo = new SqliteBankRegulatoryRepository(paths.CurrentDb);
            repo.EnsureSchema();
            // 机构类型是靠财务特征科目认出来的，这里只**读**本地已有的快照、不联网补抓——
            // 补抓是【金融监管指标】那一项的事。认不出类型的按银行的标签集解析。
            var latest = new SqliteFinancialRepository(paths.CurrentDb).GetLatestSnapshotByCode();
            new BankReportReparser(repo, paths.ReportsDir, SqliteWriteGate.Local)
                .Run(latest, m => Report(m), ct);
        }, ct);
        yield break;   // 重解析器自己落库，不产出批
    }

    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// 【导入手工数据】——把填好的「待手工回填清单.csv」写回库。文件没填就是空跑、无副作用。
/// </summary>
public sealed class ManualImportTask(FetchPaths paths) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.ImportManual;

    private readonly List<string> _errors = [];
    private string? _summary;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _summary = null;

        var csv = Path.Combine(paths.ReportsDir, ManualFillWorklist.FileName);
        if (!File.Exists(csv))
        {
            // ⚠ 这是**错误**不是「没事可做」：人点了这一项就是想导入，文件不在说明前置没跑，
            //   记成完成会让人以为导进去了。
            var msg = $"找不到清单文件：{csv}。请先跑一次【金融监管指标】生成它。";
            _errors.Add(msg);
            Report("⚠ " + msg);
            yield break;
        }

        Report($"读取 {csv} ...");
        int imported = await Task.Run(() =>
        {
            var (imported, confirmed, skipped, errors) = ManualFillWorklist.Import(paths.CurrentDb, csv);
            foreach (var e in errors) { _errors.Add(e); Report("⚠ " + e); }
            _summary = $"导入完成：写入 {imported} 条人工填的值，确认 {confirmed} 条 OCR 值正确（留空的那些），"
                     + $"跳过 {skipped} 条（既没 OCR 值也没填）。这两类以后都不会被自动解析覆盖。";
            Report(_summary);
            return imported;
        }, ct);
        if (imported > 0) yield return [imported];
    }

    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
        => Task.FromResult<TaskRunResult?>(
            new TaskRunResult(_errors.Count > 0 ? TaskState.Failed : TaskState.Completed,
                              _errors, NothingToDo: stats.Items == 0, _summary));
}

/// <summary>
/// 【优化数据库】——给几张大表补建二级索引并更新统计信息，见 <see cref="SqliteMaintenance"/>。
/// 不联网、纯本地维护。一次性动作：建成后是持久对象，重复点会检测到已存在、秒返回。
///
/// ⚠ 它是全库唯一的**真·黑盒**：单条 <c>CREATE INDEX</c> 一旦下发就无法中断、中间也没有任何
/// 可插进度的地方，而库现在 23GB，一条跑十几分钟很正常。所以走 <see cref="ReportLiveness"/>
/// 定时播报「仍在建 xxx」——⚠ 那条**不喂静默看门狗**（它证明不了这条 SQL 在前进）。
/// 取消只在两条索引**之间**生效，已建好的保留、下次继续。
/// </summary>
public sealed class DatabaseOptimizeTask(FetchPaths paths) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.OptimizeDatabase;

    private readonly List<string> _errors = [];

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        // 整段是同步的阻塞 IO（CREATE INDEX），推线程池
        await Task.Run(() =>
        {
            try
            {
                new SqliteMaintenance(paths.CurrentDb).BuildIndexes(
                    m => Report(m), ct, liveness: s => ReportLiveness(s, TimeSpan.Zero));
            }
            catch (OperationCanceledException)
            {
                // 中断是安全的：每条索引各自独立事务，已建好的保留，下次点会跳过继续。
                Report("已停止；已建好的索引保留，下次点【优化数据库】会跳过它们继续建。");
                throw;
            }
            catch (Exception ex)
            {
                _errors.Add($"优化数据库失败：{ex.Message}");
            }
        }, ct);
        yield break;
    }

    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
        => Task.FromResult<TaskRunResult?>(
            new TaskRunResult(_errors.Count > 0 ? TaskState.Failed : TaskState.Completed, _errors));
}
