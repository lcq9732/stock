using System.Runtime.CompilerServices;
using StockPlatform.Data.Remote;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【行业景气指标】（2026-09-08 从 FetchOrchestrator 迁成新式任务）。
/// 东财 <c>RPTA_DATA_IF_*</c> 三张报表，走 datacenter，约 120 个请求，日更。
///
/// ════ 它是什么 ════
/// 周期品的**价格和库存**：猪粮比价、螺纹钢期货价与库存、焦煤、原油、铜铝锌铅、水泥价格指数、
/// 国房景气指数、全国汽车销量…共 116 个指标，日 45 / 月 55 / 周 14 / 旬 1 / 半年 1。
///
/// 这是**传统行业分析**那一路的输入，跟风口分析分开看——风口看的是叙事能不能兑现成别人的报表，
/// 周期股看的是价格本身，而价格是日周频的、比季报早一个季度。
/// 能力边界：覆盖 658 只票（约 12%），全是周期股；成长题材一个都没有。历史只到 2024-04。
///
/// ════ 两步的失败语义**故意不一样** ════
/// ① <b>目录</b>（指标字典 + 股票映射）是<b>快照</b>：东财一次给全 116 个指标、1190 条映射。
///    拿不全就整项放弃、正表一动不动——半截名单进库等于凭空少掉一批股票的指标关联，
///    而那种缺失在界面上看不出来（只是某只票"恰好没有指标"），排查时根本想不到是这儿。
/// ② <b>序列</b>是<b>累积</b>：一个指标一批，抓一个存一个。某个指标这轮失败，其余 115 个照样落库。
///
/// ════ 为什么这一项**不需要**完成度表 ════
/// 骨架会在 Deadline / MaxItems 到点时从批中间收尾。要紧的是**批的粒度跟水位线的粒度对得上**：
/// 这里一批＝一个指标的完整序列，而水位线是每个指标自己的 <c>MAX(trade_date)</c>——
/// 截断只可能发生在指标边界，被切掉的指标水位线还是旧的，下轮自然重抓。
///
/// 对比【客户与供应商】：那边一批是 2000 行、水位线却是"年"，粗了两个数量级，
/// 所以必须额外记 CustSuppYearState。**任务的水位线粒度必须细于骨架的截断粒度**——
/// 满足了就不用加表，不满足就必须加。
/// </summary>
public sealed class IndustryIndicatorTask(
    IIndustryIndicatorRepository repository,
    EastMoneyIndustryIndicatorProvider provider) : FetchTaskBase<IndicatorPoint>
{
    public override FetchActionId Id => FetchActionId.StepIndustryIndicator;

    /// <summary>目录那一步的失败原因。非空＝这一轮整项没开工，<c>OnCompletedAsync</c> 据此报 Failed。</summary>
    private string? _catalogError;

    /// <summary>本轮统计：成功几个指标、几个没有代表股、哪些失败了。</summary>
    private int _okIndicators, _noRepresentative, _totalIndicators;
    private readonly List<string> _failedIndicators = [];

    protected override async IAsyncEnumerable<IReadOnlyList<IndicatorPoint>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _catalogError = null;
        _okIndicators = _noRepresentative = _totalIndicators = 0;
        _failedIndicators.Clear();

        void Forward(string s) => Report(s);
        provider.OnStatus += Forward;
        try
        {
            // ── ① 目录：快照，拿不全就整项放弃 ──
            List<IndustryIndicator> indicators;
            List<StockIndicatorLink> links;
            try
            {
                (indicators, links) = await FetchCatalogAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _catalogError = ex.Message;
                Report($"⚠ 指标目录没拉到（{ex.Message}），本轮整项跳过，库里保留上一次的目录和序列。");
                yield break;
            }

            if (indicators.Count == 0 || links.Count == 0)
            {
                // 空结果不当成"没有数据"去清库——跟板块快照同一条铁律
                _catalogError = "目录返回空";
                Report("⚠ 指标目录返回空，本轮不写库，库里保留上一次的。");
                yield break;
            }

            repository.UpsertIndicators(indicators);
            repository.ReplaceLinks(links);
            Report($"指标目录已更新：{indicators.Count} 个指标、{links.Count} 条股票关联。", phase: "目录");

            // ── ② 序列：一个指标一批 ──
            var reps = repository.GetRepresentativeStocks();
            var watermarks = repository.GetLatestDates();
            _totalIndicators = indicators.Count;

            int done = 0;
            foreach (var ind in indicators)
            {
                ct.ThrowIfCancellationRequested();
                done++;

                if (!reps.TryGetValue(ind.IndicatorId, out var rep)
                    || EastMoneyIndustryIndicatorProvider.ToSecuCode(rep) is not { } secu)
                {
                    // 指标在目录里但没有任何股票关联——查序列必须带 SECUCODE，没法查
                    _noRepresentative++;
                    continue;
                }

                watermarks.TryGetValue(ind.IndicatorId, out var since);

                List<IndicatorPoint>? points = null;
                try
                {
                    points = await provider.FetchSeriesAsync(
                        ind, secu, since == default ? null : since, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 一个指标失败不牵连其余——它下轮水位线还是旧的，自然会重抓
                    _failedIndicators.Add($"{ind.Name}（{ex.Message}）");
                }

                if (points == null) continue;
                _okIndicators++;
                if (points.Count > 0)
                {
                    Report($"{ind.Name}：+{points.Count} 行", done, _totalIndicators, "序列");
                    yield return points;
                }
            }
        }
        finally { provider.OnStatus -= Forward; }
    }

    /// <summary>目录那 3 个请求。Provider 的接口要 IProgress，这里桥回事件。</summary>
    private Task<(List<IndustryIndicator>, List<StockIndicatorLink>)> FetchCatalogAsync(
        CancellationToken ct)
        => provider.FetchCatalogAsync(new Progress<string>(s => Report(s, phase: "目录")), ct);

    protected override Task SaveBatchAsync(IReadOnlyList<IndicatorPoint> batch, CancellationToken ct)
    {
        repository.UpsertPoints(batch);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 目录没拿到＝这一项这轮压根没开工。这是**任务自己才知道的前提**，
        // 按 IFetchTask 的契约由任务返回 Failed，不是调度侧的"拒绝"。
        if (_catalogError != null)
            return Task.FromResult<TaskRunResult?>(new TaskRunResult(
                TaskState.Failed,
                [$"行业景气指标目录拉取失败：{_catalogError}"],
                Progress: $"指标目录没拿到（{_catalogError}），库里保留上一次的"));

        var errors = new List<string>();
        var (nInd, nPts, nLinks) = repository.GetCounts();

        var summary = $"行业景气指标：本轮 {_okIndicators}/{_totalIndicators} 个指标新增 {stats.Items} 行；"
                    + $"库里共 {nInd} 个指标、{nPts} 行序列、{nLinks} 条关联";
        Report(summary);

        if (_noRepresentative > 0)
            Report($"（{_noRepresentative} 个指标没有可用的代表股，查序列必须带股票代码，已跳过。）");

        if (_failedIndicators.Count > 0)
        {
            var sample = string.Join("、", _failedIndicators.Take(3));
            Report($"⚠ {_failedIndicators.Count} 个指标本轮没抓到（{sample}"
                 + (_failedIndicators.Count > 3 ? " 等" : "") + "），下轮接着抓。");
            errors.Add($"行业景气指标有 {_failedIndicators.Count} 个未抓到：{sample}");
        }

        return Task.FromResult<TaskRunResult?>(
            errors.Count > 0
                ? new TaskRunResult(TaskState.Completed, errors, Progress: summary)
                : TaskRunResult.Ok(nothingToDo: stats.Items == 0, progress: summary));
    }
}
