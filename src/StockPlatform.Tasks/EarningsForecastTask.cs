using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取业绩预告/快报】（2026-09-21 从编排器迁到新框架，
/// 见 doc/remaining-tasks-migration-design.md §2.5）——东财 datacenter，两段。
///
/// ════ 一批＝一段 ════
/// 预告和快报是**两条独立的线**：各自的水位线（预告看公告日、快报看更新日）、
/// 各自的 provider 调用。停在段边界最干净。
///
/// ⚠ **两段各自 try**：快报是非强制披露、覆盖面本来就小，它失败不该把已经抓好的预告
/// 一起算失败。这是迁移里最容易顺手合并掉的一处。
///
/// 两段都是**回调落库**的形状，所以这一项自己存、不产出批（见设计文档 §1.3）。
/// </summary>
public sealed class EarningsForecastTask(
    EastMoneyEarningsForecastProvider provider,
    IEarningsForecastRepository repository) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.FetchEarningsForecast;

    /// <summary>历史起点——再早数据源也没有。</summary>
    private static readonly DateTime Floor = new(2016, 1, 1);

    private readonly List<string> _errors = [];
    private int _forecast, _express, _segmentsDone;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _forecast = _express = _segmentsDone = 0;

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            await Task.Run(() => repository.EnsureSchema(), ct);
            var today = DateTime.Today;

            // ── 业绩预告 ──
            var fStart = await Task.Run(() => repository.GetLatestForecastNoticeDate(), ct) ?? Floor;
            bool firstRun = await Task.Run(() => repository.CountForecasts(), ct) == 0;
            Report($"业绩预告：从 {fStart:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}"
                 + (firstRun ? "（首次全量，约 400 页）" : "（增量）"));
            try
            {
                ct.ThrowIfCancellationRequested();
                _forecast = await provider.FetchForecastsAsync(
                    fStart, today, b => repository.UpsertForecasts(b), ProgressSink, ct);
                _segmentsDone++;
                Report($"业绩预告写入 {_forecast} 条，本地共 {repository.CountForecasts()} 条。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _errors.Add($"业绩预告抓取失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}");
            }

            // ── 业绩快报 ──
            // 单独 try：快报失败不该把已经抓好的预告一起算失败（见类注释）。
            var eStart = await Task.Run(() => repository.GetLatestExpressUpdateDate(), ct) ?? Floor;
            Report($"业绩快报：从 {eStart:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}");
            try
            {
                ct.ThrowIfCancellationRequested();
                _express = await provider.FetchExpressAsync(
                    eStart, today, b => repository.UpsertExpress(b), ProgressSink, ct);
                _segmentsDone++;
                Report($"业绩快报写入 {_express} 条，本地共 {repository.CountExpress()} 条。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _errors.Add($"业绩快报抓取失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}");
            }
        }
        finally { provider.OnStatus -= Forward; }

        yield break;   // 自己存，不产出批
    }

    /// <summary>用不上——这一项自己存，骨架永远拿不到批。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var summary = $"业绩预告/快报完成：预告 {_forecast} 条、快报 {_express} 条"
                    + (_errors.Count > 0 ? $"，{_errors.Count} 段失败" : "") + "。";
        Report(summary);

        // 两段全挂了才算整项失败
        return Task.FromResult<TaskRunResult?>(_segmentsDone == 0
            ? new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary)
            : new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }
}
