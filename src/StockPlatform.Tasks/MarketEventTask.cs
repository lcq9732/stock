using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取市场事件】（2026-09-21 从编排器迁到新框架，
/// 见 doc/remaining-tasks-migration-design.md §2.3）——三张表：
/// 机构调研（<c>OrgSurvey</c>）、股东增减持（<c>HolderChange</c>）、限售解禁（<c>ShareLift</c>）。
///
/// ════ 一批＝一张表 ════
/// 粗，但诚实：这三张各自是独立的一段（各自的水位线、各自的 provider 调用），
/// 停在表边界最干净。三张表都是**回调落库**的形状，所以这一项自己存、不产出批（同 §1.3）。
///
/// ════ 限售解禁不传日期 ════
/// 它是"未来要解禁的"，全量重取才对——按水位线增量会永远补不到新排进来的那些。
///
/// ⚠ **主键重复告警必须收进 errors**：`IMarketEventRepository.OnWarning` 是"数据静默丢失"
/// 的唯一早期信号（龙虎榜席位曾丢 7.5% 且一声不吭）。丢掉这条订阅等于把报警器拆了。
/// </summary>
public sealed class MarketEventTask(
    EastMoneyMarketEventProvider provider,
    IMarketEventRepository repository) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.FetchMarketEvents;

    /// <summary>历史起点——再早数据源也没有。</summary>
    private static readonly DateTime Floor = new(2016, 1, 1);

    /// <summary>增量时额外往前推几天，兜"公告补发/修订"。</summary>
    private const int LookbackDays = 30;

    private readonly List<string> _errors = [];
    private int _tablesDone;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _tablesDone = 0;
        bool fullBackfill = args.Mode.HasFlag(FetchMode.FirstBackfill);
        var today = DateTime.Today;

        void Forward(string s) => Report(s);
        // 主键重复告警必须收——见类注释
        void OnWarn(string s) { Report(s); _errors.Add(s); }
        provider.OnStatus += Forward;
        repository.OnWarning += OnWarn;
        try
        {
            await Task.Run(() => repository.EnsureSchema(), ct);

            await RunOneAsync("机构调研", "OrgSurvey", "notice_date", fullBackfill, today,
                start => provider.FetchOrgSurveysAsync(start, today,
                    b => repository.UpsertOrgSurveys(b), ProgressSink, ct), ct);

            await RunOneAsync("股东增减持", "HolderChange", "notice_date", fullBackfill, today,
                start => provider.FetchHolderChangesAsync(start, today,
                    b => repository.UpsertHolderChanges(b), ProgressSink, ct), ct);

            // 限售解禁不传日期——全量重取，理由见类注释
            try
            {
                ct.ThrowIfCancellationRequested();
                int n = await provider.FetchShareLiftsAsync(
                    b => repository.UpsertShareLifts(b), ProgressSink, ct);
                _tablesDone++;
                Report($"限售解禁写入 {n} 行，本地共 {repository.Count("ShareLift")} 行。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _errors.Add($"限售解禁抓取失败：{ex.Message}");
                Report($"⚠ {_errors[^1]}");
            }
        }
        finally
        {
            provider.OnStatus -= Forward;
            repository.OnWarning -= OnWarn;
        }

        yield break;   // 自己存，不产出批
    }

    /// <summary>跑一张按日期增量的表。一张失败不拖垮另外两张。</summary>
    private async Task RunOneAsync(
        string label, string table, string dateCol, bool fullBackfill, DateTime today,
        Func<DateTime, Task<int>> fetch, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            int before = await Task.Run(() => repository.Count(table), ct);
            var watermark = before == 0 ? null : await Task.Run(() => repository.GetLatestDate(table, dateCol), ct);
            var (start, mode) = MarketEventWindowRule.Start(fullBackfill, watermark, Floor, LookbackDays);

            Report($"{label}：从 {start:yyyy-MM-dd} 抓到 {today:yyyy-MM-dd}{mode}");
            int n = await fetch(start);
            int after = await Task.Run(() => repository.Count(table), ct);
            _tablesDone++;

            // 整段回补时报"净增了多少行"：这一轮补回来的正是**此前跨页遗漏、而且从来没有任何
            // 告警**的那部分（重复那半有主键自检喊，遗漏那半没有）。净增 0 就说明之前没漏——
            // 这个数本身就是结论，值得留在日志里。
            Report($"{label} 写入 {n} 行，本地共 {after} 行。"
                 + (fullBackfill
                     ? $"　整段回补对账：回补前 {before} 行 → 现在 {after} 行，"
                       + (after > before
                           ? $"**补回此前遗漏的 {after - before} 行**（{(after - before) * 100.0 / Math.Max(after, 1):F3}%）"
                           : "没有净增，说明此前没有跨页遗漏")
                     : ""));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _errors.Add($"{label} 抓取失败：{ex.Message}");
            Report($"⚠ {_errors[^1]}");
        }
    }

    /// <summary>用不上——这一项自己存，骨架永远拿不到批。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var summary = $"市场事件三项完成（成功 {_tablesDone}/3 张表）"
                    + (_errors.Count > 0 ? $"，{_errors.Count} 条告警/错误" : "") + "。";
        Report(summary);

        // 三张全挂了才算整项失败——一张挂了另外两张的数据是好的。
        return Task.FromResult<TaskRunResult?>(_tablesDone == 0
            ? new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, summary)
            : new TaskRunResult(TaskState.Completed, _errors, NothingToDo: false, summary));
    }
}
