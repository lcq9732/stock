using System.Runtime.CompilerServices;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【拉取行业分类】（2026-09-10 从 FetchOrchestrator 迁成新式任务）。
/// 证监会两级分类：门类字母来自沪深两所官网，门类名+大类来自东财 F10
/// （可切回新浪，配置 <c>IndustrySource</c>）。约 55 个请求、1~1.5 分钟。
///
/// ════ 为什么整表只有一批 ════
/// 这张表的语义是"全市场当前分类的**完整快照**"，落库是清表重写（<c>ReplaceAll</c>）。
/// 若按页落账，中途停下来库里就只剩跑过的那部分，其余票的行业凭空消失——
/// 跟【全库数据体检】不按 500 只落账是同一个理由：**安静的错比全丢更糟**。
///
/// 代价算得很清楚：整批在内存里约 6000 行 × 4 个短字符串 ≈ 2MB，跑一趟 1~1.5 分钟，
/// 中断重跑就是再花这一分半。相比"库里行业残缺且没人发现"，这个代价可以忽略。
/// 所以 <see cref="TaskRunArgs.MaxItems"/> / <see cref="TaskRunArgs.Deadline"/>
/// 对本任务**没有意义**，不实现分批——框架允许一批就是一批。
///
/// ════ 护栏为什么在这里而不在 provider ════
/// "这轮结果比库里少 5% 以上就整轮放弃"要读库，而 provider 不该碰库。
/// 这道护栏挡的是"接口改版/翻页断在半路"——半截名单进了库，等于一批股票的行业无声消失。
/// </summary>
public sealed class IndustryTask(SqliteIndustryRepository repository, IIndustryProvider provider)
    : FetchTaskBase<StockIndustry>
{
    /// <summary>比库里少这个比例以上就判定为"半截名单"，整轮放弃。</summary>
    private const double MinKeepRatio = 0.95;

    public override FetchActionId Id => FetchActionId.FetchIndustry;

    /// <summary>非空＝这一轮没写库，<see cref="OnCompletedAsync"/> 据此报 Failed。</summary>
    private string? _abortReason;

    /// <summary>落库后的自检数（门类/大类各多少种、多少只有大类），跑完报出来。</summary>
    private int _rowsSaved, _withMajor, _classKinds, _majorKinds, _before;

    protected override async IAsyncEnumerable<IReadOnlyList<StockIndustry>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _abortReason = null;
        _rowsSaved = _withMajor = _classKinds = _majorKinds = 0;
        _before = repository.Count();

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        List<StockIndustry> rows;
        try
        {
            Report($"开始抓取全市场行业分类（来源 {provider.SourceName}，库里现有 {_before} 只）...");
            rows = await provider.GetAllAsync(ct);
        }
        finally
        {
            provider.OnStatus -= Forward;
        }

        if (rows.Count == 0)
        {
            _abortReason = "行业分类返回空——接口可能变了，本轮不写库（库里保留上一版）";
            yield break;
        }

        // 半截名单护栏。⚠ 只在库里本来就有数据时才判：空库首次抓取要放行。
        if (_before > 0 && rows.Count < _before * MinKeepRatio)
        {
            _abortReason = $"行业分类只拿到 {rows.Count} 只、库里原有 {_before} 只（少了 " +
                           $"{(1 - (double)rows.Count / _before) * 100:F1}%），疑似半截名单，本轮不写库";
            yield break;
        }

        yield return rows;
    }

    /// <summary>整表替换。一批就是全量，所以这里直接 ReplaceAll。</summary>
    protected override Task SaveBatchAsync(IReadOnlyList<StockIndustry> batch, CancellationToken ct)
    {
        repository.ReplaceAll(batch, provider.SourceName);

        _rowsSaved = batch.Count;
        _withMajor = batch.Count(r => !string.IsNullOrEmpty(r.MajorName));
        _classKinds = batch.Where(r => !string.IsNullOrEmpty(r.ClassName)).Select(r => r.ClassName).Distinct().Count();
        _majorKinds = batch.Where(r => !string.IsNullOrEmpty(r.MajorName)).Select(r => r.MajorName).Distinct().Count();
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        if (_abortReason != null)
        {
            Report("⚠ " + _abortReason);
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Failed, [_abortReason]));
        }

        // 这三个数就是每轮的自检：门类应为 19 种（东财口径）、大类 84 种。
        // 门类种数明显多于 19 说明混进了两所那套不统一的叫法（换源前库里有 32 种）。
        Report($"行业分类完成：{_rowsSaved} 只（{_withMajor} 只有大类），" +
               $"门类 {_classKinds} 种、大类 {_majorKinds} 种，来源 {provider.SourceName}");
        return Task.FromResult<TaskRunResult?>(null);
    }
}
