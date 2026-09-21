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
/// ════ 快照语义：<c>ClearAll</c> 必须等第一批到手 ════
/// 这张表是"当下全量"，不是累积的。但**不能一上来就清表**：接口挂了的话库里旧数据也没了，
/// 而旧的行业分类照样能用（分析侧每天都在读）。所以清表挂在第一批回调里——
/// 拿到数据了才清，一个字都拿不到就什么也不动。
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
    private int _industry, _theme, _covered;
    private bool _failed;

    protected override async IAsyncEnumerable<IReadOnlyList<int>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        _errors.Clear();
        _industry = _theme = _covered = 0;
        _failed = false;

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            await Task.Run(() => repository.EnsureSchema(), ct);

            bool cleared = false;
            var (ind, theme) = await provider.FetchAsync(
                (inds, themes) =>
                {
                    // 拿到第一批才清表——这样接口挂了的话库里旧数据还在（见类注释）
                    if (!cleared) { repository.ClearAll(); cleared = true; }
                    return (repository.UpsertIndustries(inds), repository.UpsertThemes(themes));
                },
                ProgressSink, ct);

            _industry = ind;
            _theme = theme;
            _covered = await Task.Run(repository.CountIndustryStocks, ct);
        }
        catch (OperationCanceledException)
        {
            Report("个股行业/题材抓取中断，已落库的部分有效；这张表是快照，下次重新全量取。");
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

        var summary = $"个股行业/题材完成：行业 {_industry} 条（覆盖 {_covered} 只）、题材 {_theme} 条。"
                    + "东财覆盖不到的股票仍走证监会分类兜底（StockIndustry 表保留）。";
        Report(summary);
        return Task.FromResult<TaskRunResult?>(
            new TaskRunResult(TaskState.Completed, _errors,
                              NothingToDo: _industry == 0 && _theme == 0, summary));
    }
}
