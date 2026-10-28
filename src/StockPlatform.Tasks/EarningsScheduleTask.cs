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
/// <remarks>
/// 2026-10-27 起**两家都抓**（见 doc/financial-fetch-disclosure-race-design.md）：
/// 巨潮那份照旧进 EarningsSchedule，东财那份进 EarningsScheduleEm，消费端取并集。
/// 起因是巨潮的"已披露"标记滞后半天以上，而【拉取财务报表】拿它当闸门，于是当晚披露的票
/// 整批晚一轮。东财那一路是**纯增益**：它快就早开闸，它慢也不会比只用巨潮更差。
/// <para>
/// 东财是**可选**的（<c>emProvider</c> 可以为 null），抓失败也只记一句不让整项失败——
/// 它本来就是加在现状之上的第二条腿，断了要退回原来的样子，不能反过来把能用的那条腿拖垮。
/// </para>
/// </remarks>
public sealed class EarningsScheduleTask(
    FetchPaths paths,
    CninfoPrebookProvider provider,
    EastMoneyPrebookProvider? emProvider = null) : FetchTaskBase<EarningsScheduleRow>
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

            // ── 同一期再抓一遍东财，然后把两家合成一份（2026-10-27）────────────
            // 紧跟在巨潮这一期后面抓，两边拿到的是**同一时刻**的状态，对账那句才有意义。
            // 不走 yield：东财的行字段集不一样（多 is_publish/trade_market 等十来列），
            // 有自己的模型和表，骨架的泛型装不下它。
            var emRows = await FetchEmAsync(period, label, ct);

            // **合并就发生在这里，只此一处。**消费端（财务抓取、股东抓取、金融监管指标、
            // 界面的财报日列）读的都是合并表，谁都不需要知道底下有两个数据源。
            // 东财那边没抓到就用空表合——结果跟巨潮原样一致，也就是现状。
            await MergeAndSaveAsync(period, label, rows, emRows, ct);
        }
    }

    /// <summary>
    /// 抓东财版的同一期并落它自己那张表，返回抓到的行（失败/没有则返回空表）。
    ///
    /// **失败只记一句、不抛**——这是加在巨潮之上的第二条腿，它断了应该退回"只有巨潮"的现状，
    /// 而不是让整项任务失败。返回空表正好让下面的合并退化成"巨潮原样"。
    /// </summary>
    private async Task<List<EarningsScheduleEmRow>> FetchEmAsync(
        DateTime period, string label, CancellationToken ct)
    {
        if (emProvider == null) return [];
        try
        {
            var emRows = await emProvider.FetchAsync(period, new Progress<string>(s => Report(s)), ct);
            if (emRows.Count == 0)
            {
                Report($"【{label}】东财返回 0 条，这一期只用巨潮那份。");
                return [];
            }

            await Task.Run(() => Repo.UpsertEm(emRows), ct);
            return emRows;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _errors.Add($"【{label}】东财预约日抓取失败：{ex.Message}");
            Report($"⚠ {_errors[^1]}（巨潮那份已经抓到了，这一期按只有巨潮处理）");
            return [];
        }
    }

    /// <summary>
    /// 两家合成一份落进合并表，并报一句对账。
    ///
    /// 对账里**「只有东财标了」那个数就是这条腿的全部价值**——它是今天靠东财提前一轮
    /// 就能抓到财报的票数。长期为 0 ⇒ 两家同步 ⇒ 这条腿可以撤掉。
    /// </summary>
    private async Task MergeAndSaveAsync(
        DateTime period, string label,
        IReadOnlyList<EarningsScheduleRow> cninfoRows, IReadOnlyList<EarningsScheduleEmRow> emRows,
        CancellationToken ct)
    {
        try
        {
            var merged = await Task.Run(() => EarningsScheduleMerge.Merge(cninfoRows, emRows), ct);
            await Task.Run(() => Repo.UpsertMerged(merged), ct);

            if (emRows.Count == 0)
            {
                Report($"【{label}】合并表已更新 {merged.Count} 条（这一期只有巨潮）。");
                return;
            }

            var c = await Task.Run(() => Repo.CompareSources(period), ct);
            Report($"【{label}】合并 {merged.Count} 条（巨潮 {c.CninfoTotal}、东财 {c.EmTotal}）。"
                 + $"已标已披露：巨潮 {c.CninfoDisclosed}、东财 {c.EmDisclosed}；"
                 + $"**只有东财标了 {c.OnlyEm} 只、只有巨潮标了 {c.OnlyCninfo} 只**"
                 + (c.OnlyEm > 0
                     ? $"——这 {c.OnlyEm} 只就是今天靠东财提前一轮能抓到财报的票。"
                     : "——两家同步，今天没有额外收益。"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 合并塌了比东财抓不到严重得多：消费端读的就是合并表。记成错误让人看见。
            _errors.Add($"【{label}】合并预约日失败：{ex.Message}");
            Report($"⚠ {_errors[^1]}");
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
