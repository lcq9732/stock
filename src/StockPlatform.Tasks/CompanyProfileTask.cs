using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【公司档案】（2026-09-08）。东财 <c>RPT_HSF9_BASIC_ORGINFO</c>，5634 家 A 股、14 页、季度更新。
///
/// ════ 为什么单独一项，不并进【客户与供应商】 ════
/// 它是那一项做**实体消歧**的前提（年报里写"福建时代星云科技有限公司"，本地只有简称，对不上），
/// 但请求量差 100 倍：这里 14 页、那里首轮 1531 页。并成一项的话，
/// <see cref="TaskRunArgs.Deadline"/> 一到很可能是"档案刚拉完就没时间抓关系了"——
/// 而两者其实各自独立有用。何况档案本身是一份公司基本面（省份、员工数、实控人、主营业务），
/// 不该是别人的私有步骤。
///
/// ════ 一份响应，两张表 ════
/// 长文本（公司简介/沿革/经营范围/经营评述）单独存 <see cref="CompanyNarrative"/>。
/// 不是洁癖：<c>CompanyProfile</c> 会被消歧那一步**全表读**，而经营评述平均 4186 字、
/// 最长 4.6 万字，一个字段占整条记录体积的 69%——混在一行里每次全表扫描要多读 3 倍数据。
/// 两张表必须在同一个事务里写，否则会留下"有档案没简介"的半拉记录。
/// </summary>
public sealed class CompanyProfileTask(
    ICompanyProfileRepository repository,
    EastMoneyCompanyProfileProvider provider)
    : FetchTaskBase<(CompanyProfile Profile, CompanyNarrative Narrative)>
{
    public override FetchActionId Id => FetchActionId.StepCompanyProfile;

    /// <summary>接口自报的总行数（含港股等非 A 股），收尾时用来说明"为什么收到的比自报的少"。</summary>
    private int _reported;

    protected override async IAsyncEnumerable<IReadOnlyList<(CompanyProfile, CompanyNarrative)>>
        FetchAsync(TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            Report("拉取公司档案（5634 家 A 股、14 页）...", phase: "档案");
            await foreach (var batch in provider.StreamAsync(n => _reported = n, ct: ct))
                yield return batch;
        }
        finally { provider.OnStatus -= Forward; }
    }

    protected override Task SaveBatchAsync(
        IReadOnlyList<(CompanyProfile Profile, CompanyNarrative Narrative)> batch, CancellationToken ct)
    {
        repository.Upsert(batch);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        var (profiles, narratives) = repository.GetCounts();

        // 两张表条数必须相等——不等就是有半拉记录（一张写了另一张漏），
        // 那会让消歧那一步读到全称、却查不到对应的简介。
        var errors = new List<string>();
        if (profiles != narratives)
            errors.Add($"公司档案两张表条数对不上：档案 {profiles}、长文本 {narratives}（应该相等）");

        var summary = $"公司档案 {profiles} 家"
                    + (_reported > 0 ? $"（接口自报 {_reported} 行，差额是港股等非 A 股）" : "");
        Report(summary);

        if (errors.Count > 0)
            return Task.FromResult<TaskRunResult?>(
                new TaskRunResult(TaskState.Completed, errors, Progress: summary));

        return Task.FromResult<TaskRunResult?>(
            TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }
}
