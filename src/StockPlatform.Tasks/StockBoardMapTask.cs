using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取个股行业与题材】（2026-09-21 从编排器迁到新框架，
/// 见 doc/remaining-tasks-migration-design.md §2.2）——东财 datacenter，约 9.4 万行。
///
/// ════ 快照语义：按票整只替换，不清表（2026-09-22 改）════
/// 这张表是"当下全量"，不是累积的。但**清表不行**，两版都不行：
///   · 一上来就清 → 接口挂了库里旧数据也没了，而旧的行业分类照样能用（分析侧每天都在读）；
///   · 等第一批到手再清（2026-09-03 ~ 09-22 的做法）→ **抓了一半停下来只剩一部分票**，
///     其余票静默退回证监会粗分类，而且没有任何标记说这份快照是半截的。
///
/// 现在走 <see cref="IStockBoardMapRepository.ReplaceForStocks"/>：一批＝若干只票，
/// 先删这些票的旧行再插新的，一个事务。于是**任何时刻库里每只票都是自洽的一代**——
/// 抓到的是本轮新值，没抓到的是上一轮的完整值。
///
/// 为什么非得"整只"、不能按行代际清理：这两张表有一条隐含不变式——同一只票的 1/2/3 级
/// 三行构成一条父子链（<c>GetBoardParents</c> 靠它还原整棵板块树），而
/// <c>GetFinestIndustryByStock</c> 取 <c>board_level</c> 最大那条。一只票身上混着两代的行，
/// 链就断了、旧的三级板块还会**压掉**本轮的正确值——那是静默错值，比"没有数据"更糟。
///
/// ════ 孤儿票 ════
/// 按票替换管不了"这只票现在一个板块都不属于了"——它本轮一行都不出现，旧行就留着。
/// 所以**整轮抓完且对账通过**之后再清一次 <c>fetched_at</c> 比本轮早的行
/// （<see cref="IStockBoardMapRepository.PurgeOlderThan"/>）。中断时**不清**：那会把
/// "还没轮到"误当成"已经没有归属"。
///
/// ════ 为什么这一项 yield 不出批 ════
/// provider 是**回调落库**的形状（自己翻页、每攒一批回调一次），一次调用跑完整段。
/// 硬要拆成骨架的"yield 一批、骨架存一批"得给 provider 改签名加分页控制，
/// 而回调落库本身是对的——它保证了"抓一页落一页、中途停不丢"。
/// 所以这里**自己存**，`FetchAsync` 不产出批；心跳靠 provider 的 OnStatus 转出来。
/// 见设计文档 §1.3（`NetInflowTask.FillDaysAsync`、`BarFetchTaskBase.FillValueAsync` 同款）。
///
/// ⚠ 也**不设 MaxItems**：快照语义下"只做一半"没有意义，下次还是全量重取。
/// </summary>
public sealed class StockBoardMapTask(
    EastMoneyStockBoardMapProvider provider,
    IStockBoardMapRepository repository) : FetchTaskBase<int>
{
    public override FetchActionId Id => FetchActionId.FetchStockBoardMap;

    private readonly List<string> _errors = [];
    private int _industry, _theme, _covered, _purged;
    private bool _failed, _incomplete;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _industry = _theme = _covered = _purged = 0;
        _failed = _incomplete = false;

        // 这一轮的落盘时刻。provider 拿它盖在每一行上、收尾拿它清孤儿票，**必须是同一个值**。
        var runAt = DateTime.Now;

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            await Task.Run(() => repository.EnsureSchema(), ct);

            var outcome = await provider.FetchAsync(
                // 一批＝若干只票，整只替换（provider 保证一只票的行不被批边界切开）
                (inds, themes) => repository.ReplaceForStocks(inds, themes),
                runAt, ProgressSink, ct);

            _industry = outcome.Industry;
            _theme = outcome.Theme;
            _incomplete = !outcome.Complete;

            // 孤儿票只有在"整轮抓完且对账通过"时才敢清——见类注释「孤儿票」。
            if (outcome.Complete)
                _purged = await Task.Run(() => repository.PurgeOlderThan(runAt), ct);
            else
                Report("⚠ 这一轮没取全，**不清**上一轮残留的行——宁可留下几条过期归属，"
                     + "也不能把\"这轮没轮到\"当成\"已经没有归属\"。下一轮取全后会自动清掉。");

            _covered = await Task.Run(repository.CountIndustryStocks, ct);
        }
        catch (OperationCanceledException)
        {
            Report("个股行业/题材抓取中断。已落库的票是本轮新值、没抓到的仍是上一轮的完整值"
                 + "（按票整只替换，每只自洽）；这张表是快照，下次重新全量取即可。");
            throw;
        }
        catch (Exception ex)
        {
            _failed = true;
            _errors.Add($"个股行业/题材抓取失败：{ex.Message}");
            Report($"⚠ {_errors[^1]}");
        }
        finally { provider.OnStatus -= Forward; }

        yield break;   // 自己存，不产出批（见类注释）
    }

    /// <summary>用不上——这一项自己存，骨架永远拿不到批。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<int> batch, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_failed)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, _errors, NothingToDo: false, _errors[^1]));

        var summary = $"个股行业/题材完成：行业 {_industry} 条（覆盖 {_covered} 只）、题材 {_theme} 条"
                    + (_purged > 0 ? $"；清掉 {_purged} 条本轮已不存在的旧归属" : "")
                    + (_incomplete ? "；⚠ 这一轮没取全，上一轮的残留行留着没清" : "")
                    + "。东财覆盖不到的股票仍走证监会分类兜底（StockIndustry 表保留）。";
        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors,
                              NothingToDo: _industry == 0 && _theme == 0, summary));
    }
}
