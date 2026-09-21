using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取财报预约日】（2026-09-21 从编排器迁到新框架，
/// 见 doc/remaining-tasks-migration-design.md §2.7）——巨潮的预约披露时间表。
///
/// ════ 一批＝一个报告期 ════
/// 先问数据源有哪些报告期（通常 1~4 个），逐个抓。
///
/// ⚠ **改期对账**是这个任务最该报出来的事：抓之前记下旧的有效日期，抓完逐条比，
/// 改了的挑出来报——盯着某只票财报的人要的就是这个。丢了它，这一项就只剩"又抓了一遍"。
/// </summary>
public sealed class EarningsScheduleTask(
    FetchPaths paths,
    CninfoPrebookProvider provider) : FetchTaskBase<EarningsScheduleRow>
{
    public override FetchActionId Id => FetchActionId.FetchEarningsSchedule;

    /// <summary>改期最多列几只（再多日志就没法看了）。</summary>
    private const int MaxMovedListed = 20;

    private SqliteEarningsScheduleRepository Repo => _repo ??= new SqliteEarningsScheduleRepository(paths.CurrentDb);
    private SqliteEarningsScheduleRepository? _repo;

    private readonly List<string> _errors = [];
    private readonly List<string> _moved = [];
    private Dictionary<string, DateTime?> _before = [];
    private int _total;
    private string? _nothingToDoReason;

    protected override async IAsyncEnumerable<IReadOnlyList<EarningsScheduleRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _moved.Clear();
        _total = 0;
        _nothingToDoReason = null;

        await Task.Run(() => Repo.EnsureSchema(), ct);

        List<(DateTime Period, string Label)> periods;
        try
        {
            periods = await provider.GetAvailablePeriodsAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _errors.Add($"取可选报告期失败：{ex.Message}");
            _nothingToDoReason = "取可选报告期失败";
            Report($"⚠ {_errors[^1]}。本轮跳过。");
            yield break;
        }

        if (periods.Count == 0)
        {
            _nothingToDoReason = "数据源没给出可用的报告期";
            Report($"{_nothingToDoReason}，本轮跳过。");
            yield break;
        }

        // 抓之前先记下旧的有效日期，抓完对一遍——改期是这个任务最该报出来的事
        _before = await Task.Run(
            () => Repo.GetUpcomingByCode().ToDictionary(kv => kv.Key, kv => kv.Value.EffectiveDate), ct);

        int i = 0;
        foreach (var (period, label) in periods)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            List<EarningsScheduleRow>? rows = null;
            try
            {
                rows = await provider.FetchAsync(period, null, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _errors.Add($"【{label}】抓取失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}");
            }

            if (rows == null) continue;
            if (rows.Count == 0)
            {
                Report($"【{label}】数据源返回 0 条，跳过。", i, periods.Count);
                continue;
            }

            _total += rows.Count;
            int pending = rows.Count(r => r.Pending);
            int changed = rows.Count(r => r.ChangeCount > 0);
            Report($"【{label}】{rows.Count} 只：还没披露 {pending} 只、改过披露日 {changed} 只。",
                   i, periods.Count);

            yield return rows;
        }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<EarningsScheduleRow> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            Repo.Upsert(batch);
            // 改期对账：只看"还没披露"的那些——已经披露完的日期不会再动
            foreach (var r in batch)
            {
                if (!r.Pending || r.EffectiveDate is not { } now) continue;
                if (_before.TryGetValue(r.Code, out var was) && was is { } old
                    && old != now && _moved.Count < MaxMovedListed)
                    _moved.Add($"{r.Code} {old:MM-dd}→{now:MM-dd}（{(now - old).Days:+0;-0} 天）");
            }
        }, ct);

    protected override async Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_nothingToDoReason is { } idle)
            return new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, idle);

        if (_moved.Count > 0)
            Report($"⚠ 有 {_moved.Count} 只改了披露日期：{string.Join("、", _moved)}"
                 + "——盯着这几只的话记得对一下日子。");

        int left = await Task.Run(() => Repo.PendingCount(), ct);
        var summary = $"财报预约日完成：{_total} 条已更新，还有 {left} 只没到披露日。";
        Report(summary);
        return new TaskRunResult(TaskState.Completed, _errors, NothingToDo: _total == 0, summary);
    }
}
