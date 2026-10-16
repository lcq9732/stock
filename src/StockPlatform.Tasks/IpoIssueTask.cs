using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【新股发行】（2026-10-16，见 doc/ipo-issue-design.md）——东财新股发行表的全部 151 列存进 IpoIssue，
/// 上市日顺带写进名册 StockMeta.list_date（那一列此前从没被写过）。
///
/// ════ 两档 ════
/// · 「首次整段回补」：全量（1990 年起 5,640 只，12 个请求）。
/// · 「日常增量」：申购日在最近 <see cref="ApplyLookbackDays"/> 天的（一两个请求）。新股申购时就有一行，
///   上市日、首日表现要等上市后才补上——回看一年把"申购→上市"这一段全盖住。
///
/// ════ 对账 ════
/// 拿到的行数必须等于接口自报的总数，对不上就整项报失败（中途悄悄少了一页是这类接口的老毛病）。
/// 接口多出来的列存不进来、少了的列存成空——都在日志里点名，好去改列目录 <see cref="IpoIssueColumns"/>。
/// </summary>
public sealed class IpoIssueTask(IIpoIssueProvider provider, IIpoIssueRepository repository)
    : FetchTaskBase<IpoIssueRow>
{
    /// <summary>日常增量回看多少天的申购日。</summary>
    public const int ApplyLookbackDays = 365;

    public override FetchActionId Id => FetchActionId.FetchIpoIssue;

    private IpoFetchStats _stats = new();
    private int _listDates;
    private bool _all;

    protected override async IAsyncEnumerable<IReadOnlyList<IpoIssueRow>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _stats = new IpoFetchStats();
        _listDates = 0;
        _all = args.Mode.HasFlag(FetchMode.FirstBackfill);
        await Task.Run(repository.EnsureSchema, ct);

        var since = _all ? (DateOnly?)null : DateOnly.FromDateTime(DateTime.Today).AddDays(-ApplyLookbackDays);
        Report(_all
            ? "新股发行：全量（1990 年起全部，约 12 个请求）…"
            : $"新股发行：申购日在 {since:yyyy-MM-dd} 以后的（含还没上市的）…");

        using var _ = ForwardStatus(h => provider.OnStatus += h, h => provider.OnStatus -= h);
        await foreach (var page in provider.FetchAsync(since, _stats, ct))
        {
            ReportQuiet($"新股发行：已取 {_stats.Fetched} 只"
                      + (_stats.ReportedTotal is { } t ? $" / {t}" : ""), _stats.Fetched, _stats.ReportedTotal);
            yield return page;
        }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<IpoIssueRow> batch, CancellationToken ct)
        => Task.Run(() =>
        {
            lock (SqliteWriteGate.Local)
            {
                repository.Upsert(batch);
                _listDates += repository.UpdateListDates(batch);
            }
        }, ct);

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var errors = new List<string>();
        if (_stats.ReportedTotal is { } total && total != _stats.Fetched)
            errors.Add($"新股发行：接口说有 {total} 只、实际拿到 {_stats.Fetched} 只——中途少了，已存的不受影响，下轮重来");
        if (_stats.UnknownColumns.Count > 0)
            Report($"　⚠ 接口多了 {_stats.UnknownColumns.Count} 列、没存：{string.Join("、", _stats.UnknownColumns)}"
                 + "——要存就把它们加进 IpoIssueColumns 列目录");
        if (_stats.MissingColumns.Count > 0 && _stats.Fetched > 0)
            Report($"　⚠ 列目录里有 {_stats.MissingColumns.Count} 列这轮接口没给（存成空）：{string.Join("、", _stats.MissingColumns)}");
        if (_stats.TypeMismatches.Count > 0)
            Report($"　⚠ 这些数值列收到了解析不了的文本（存成空）：{string.Join("、", _stats.TypeMismatches)}");

        var summary = $"{_stats.Fetched} 只写入 IpoIssue，名册上市日更新 {_listDates} 只";
        Report($"新股发行完成：{summary}"
             + (_all ? "" : $"（日常增量：申购日回看 {ApplyLookbackDays} 天）")
             + $"，用时 {ElapsedText.Format(stats.Elapsed)}。");
        foreach (var e in errors) Report("　⚠ " + e);

        return Task.FromResult<TaskRunResult?>(errors.Count > 0
            ? new TaskRunResult(TaskState.Failed, errors, Progress: summary)
            : TaskRunResult.Ok(nothingToDo: _stats.Fetched == 0, progress: summary));
    }
}
