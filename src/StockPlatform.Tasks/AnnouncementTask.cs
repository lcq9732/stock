using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【中标/订单公告】（2026-09-21 从编排器迁到新框架，
/// 见 doc/remaining-tasks-migration-design.md §2.4）——按关键词搜巨潮、取正文、抽金额。
///
/// ════ 一批＝一个自然年切片 ════
/// 窗口必须按自然年切（<see cref="CalendarYearSlicer"/>）：**不切会被搜索源的翻页上限静默截断**。
/// 日常回看 14 天时只有 1 片，跨年补历史时才有多片——所以批边界天然就在年份上。
///
/// ════ 关键词为空＝空跑 ════
/// 关键词是计划里那一行自己填的（逗号分隔）。留空不是错，是"这一轮不抓公告"，
/// 但**要在日志里说清楚原因**——否则人只看到"完成"，不知道它什么都没干。
///
/// ⚠ 单片失败不带倒后面的年份：公告是非致命的旁路数据，2019 年那片挂了不该让 2020 也不跑。
/// </summary>
public sealed class AnnouncementTask(
    AnnouncementFetchOrchestrator announcements) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.StepAnnouncements;

    /// <summary>增量模式回看多少天。公告没有按标的记的水位线，重复扫同一窗口靠主键去重是安全的。</summary>
    public const int LookbackDays = 14;

    private readonly List<string> _errors = [];
    private int _slicesDone, _slicesTotal;
    private string? _nothingToDoReason;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _slicesDone = _slicesTotal = 0;
        _nothingToDoReason = null;

        // 关键词从**自己那一行**来。两种"没给"要分开（2026-09-22）：
        //   · null   ＝ 调用方压根没指定（比如【拉取区间数据】分派过来时）→ 用本项的默认值；
        //   · 空列表 ＝ 人在自己那一行**明确清空了** → 就是"这一轮别抓"，照旧跳过。
        // 混成一种的话，要么"清空了还照抓"，要么"被别人调用时永远不抓"，两头都不对。
        var keywords = args.Keywords ?? DefaultKeywords();
        if (keywords.Count == 0)
        {
            _nothingToDoReason = "公告关键词为空，这一项跳过";
            Report($"（{_nothingToDoReason}——在计划那一行的参数格里填，逗号分隔）");
            yield break;
        }

        // 「只抓某一天」就把窗口收成那一天；「整段回补」按年份区间；否则按回看窗口。
        DateOnly start, end;
        if (args.Mode == FetchMode.SpecificDay)
        {
            var day = args.Day ?? DateOnly.FromDateTime(DateTime.Today);
            start = end = day;
        }
        else if (args.Mode.HasFlag(FetchMode.FirstBackfill))
        {
            // ⚠ 这一项跟别的任务不一样：它**没有**"本地最早是哪天"那种水位线可依，
            //   搜索是按关键词打的、不按标的。所以"整段"对它来说就等于**调用方给的那几年**，
            //   没给就没有意义——不填年份的整段回补会退化成"从 1990 年搜到今天"，
            //   而搜索源有翻页上限，那只会翻满即停、剩下的静默丢掉。所以这里明确不跑。
            //   （2026-09-22 用户拍板：就按"只吃年份区间、不填就不跑"。）
            if (args.YearStart is not { } ys)
            {
                _nothingToDoReason = "整段回补要填年份区间，这一项跳过";
                Report($"（{_nothingToDoReason}——公告是按关键词搜的、没有「本地补到哪儿了」这种水位线，"
                     + "不给年份就只能从头搜到尾，而搜索源有翻页上限，搜不全还会静默丢掉。）");
                yield break;
            }
            start = new DateOnly(ys, 1, 1);
            int ye = args.YearEnd ?? DateTime.Today.Year;
            end = ye >= DateTime.Today.Year
                ? DateOnly.FromDateTime(DateTime.Today)      // 别往未来搜
                : new DateOnly(ye, 12, 31);
            if (start > end)
            {
                _nothingToDoReason = $"年份区间 {ys}~{ye} 是空的，这一项跳过";
                Report($"（{_nothingToDoReason}）");
                yield break;
            }
        }
        else
        {
            var today = DateTime.Today;
            start = DateOnly.FromDateTime(today.AddDays(-LookbackDays));
            end = DateOnly.FromDateTime(today);
        }

        var slices = CalendarYearSlicer.Split(start, end);
        _slicesTotal = slices.Count;
        if (slices.Count > 1)
            Report($"中标/订单公告：{start:yyyy-MM-dd}~{end:yyyy-MM-dd} 跨 {slices.Count} 个自然年，"
                 + "按年切片分别搜索（不切会被搜索源的翻页上限静默截断）。");

        int i = 0;
        foreach (var (sliceStart, sliceEnd) in slices)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            try
            {
                await announcements.RunAsync(keywords, sliceStart, sliceEnd, ProgressSink, ct);
                _slicesDone++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单片失败不该带倒后面的年份
                _errors.Add($"中标/订单公告（{sliceStart:yyyy} 年这一片）失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}");
            }
            Report($"中标/订单公告：{i}/{slices.Count} 片", i, slices.Count);
        }

        yield break;   // 公告那条链自己落库（AnnouncementFetchOrchestrator），不产出批
    }

    /// <summary>
    /// 本项自己的默认关键词——目录里那份（建计划项时也是拿它预填这一行的）。
    /// 只在调用方没指定时用；人把那一格清空了走的是另一条路（见上面）。
    /// </summary>
    private static IReadOnlyList<string> DefaultKeywords()
        => (FetchTaskCatalog.DefaultParamText(FetchActionId.StepAnnouncements, FetchActionParams.Keywords) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>用不上——公告那条链自己落库。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_nothingToDoReason is { } idle)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Completed, _errors, NothingToDo: true, idle));

        var summary = $"中标/订单公告完成：{_slicesDone}/{_slicesTotal} 片"
                    + (_errors.Count > 0 ? $"，{_errors.Count} 片失败" : "") + "。";
        Report(summary);

        // 片片都挂了才算整项失败
        return Task.FromResult<TaskRunResult?>(_slicesTotal > 0 && _slicesDone == 0
            ? new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary)
            : new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }
}
