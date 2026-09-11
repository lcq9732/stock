using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【观察指标映射】（2026-09-11，见 doc/watch-item-design.md M1）。
/// **纯本地查库 + 读配置，一个请求都不发。**
///
/// ════ 它解决什么 ════
/// 东财自带的个股→指标映射（<c>StockIndustryIndicator</c>）只给**上游资源股**挂原材料价格。
/// 实测：锂电池板块 BK1303 的 33 只成分股里只有 1 只有映射；碳酸锂指数 EMI00662659 只挂给
/// 6 只上游锂矿；宁德时代一个指标都没挂——而锂价正是它的核心成本变量。
/// 「中游对上游价格的敏感度」是判断，不是结构，自动映射给不出来，得我们自己挂。
///
/// ════ 为什么不直接往东财那张表里补 ════
/// <c>SqliteIndustryIndicatorRepository.ReplaceLinks()</c> 是 <c>DELETE FROM</c> 全表替换——
/// 补进去的映射在下一轮【行业景气指标】跑完就被**静默清空**，界面上只表现为
/// 「这只票恰好没有指标」。所以我们的映射单独一张 <c>StockWatchIndicator</c>，
/// 东财那张原样不动，查询时两张 UNION。
///
/// ════ 一批 = 全部 ════
/// 这一项是**整体重算**的（规则变了要全部重铺），没有增量可言，所以只 yield 一批。
/// <c>MaxItems</c>／<c>Deadline</c> 对它没意义——跟 <c>IndustryTask</c> 同理。
/// 跑一轮的量级：全市场 1.7 万条板块归属 × 几条规则，毫秒级。
///
/// ════ 幂等 ════
/// 每轮先删 <c>origin='rule'</c> 再全量写，反复跑结果一样。
/// <c>origin='manual'</c>（人手挂的）一行都不碰。
/// </summary>
public sealed class WatchIndicatorRuleTask(
    IWatchIndicatorRepository watchRepository,
    IStockBoardMapRepository boardMapRepository,
    IIndustryIndicatorRepository indicatorRepository,
    FetchPaths paths) : FetchTaskBase<WatchIndicatorLink>
{
    public override FetchActionId Id => FetchActionId.StepWatchIndicator;

    /// <summary>本轮的告警（配置指向不存在的板块/指标等），<c>OnCompletedAsync</c> 要报出来。</summary>
    private readonly List<string> _warnings = [];
    private int _ruleCount, _writtenRows;

    protected override async IAsyncEnumerable<IReadOnlyList<WatchIndicatorLink>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        watchRepository.EnsureSchema();
        _warnings.Clear();
        _ruleCount = _writtenRows = 0;

        // ① 配置。不存在就写一份模板——模板**默认启用锂电池那两条**，所以全新环境下
        //    首次跑就能产出约 107 条映射，不需要人先去改文件（见 WatchIndicatorRuleStore）。
        WatchIndicatorRuleStore.EnsureTemplate(paths.WatchIndicatorRulesPath);
        var (rules, cfgWarnings) = WatchIndicatorRuleStore.Read(paths.WatchIndicatorRulesPath);
        _warnings.AddRange(cfgWarnings);
        _ruleCount = rules.Count;

        if (rules.Count == 0)
        {
            // ⚠ 这里**不能**走到 ReplaceRuleLinks 去写空集合当"清空"——那会把上一轮算好的映射
            // 抹掉。空集合在仓储那边本来就是空操作，这里直接结束，语义更清楚。
            Report($"规则文件里 0 条规则（{paths.WatchIndicatorRulesPath}），本轮不改动映射。");
            yield break;
        }

        // ② 两个输入都在本地库里。板块归属要**全部层级**，不能只取最细一级——
        //    规则可能挂在一级(电力设备)/二级(电池)/三级(锂电池)任意一层上。
        var boardLinks = boardMapRepository.GetAllIndustryLinks();
        var knownIndicators = indicatorRepository.GetIndicators()
            .Select(i => i.IndicatorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Report($"规则 {rules.Count} 条；板块归属 {boardLinks.Count} 条；指标字典 {knownIndicators.Count} 个。");

        if (boardLinks.Count == 0 || knownIndicators.Count == 0)
        {
            // 两个输入表是别的任务填的。空了说明那边没跑过或跑挂了，这时候算出来必然是 0 行，
            // 写进去等于把映射清掉——所以整项跳过，保留上一轮的。
            _warnings.Add(boardLinks.Count == 0
                ? "StockIndustryEm 是空的（【拉取行业分类】没跑过？），本轮整项跳过。"
                : "IndustryIndicator 是空的（【行业景气指标】没跑过？），本轮整项跳过。");
            yield break;
        }

        // ③ 纯计算。校验失败是逐条跳过 + 告警，不整项放弃——理由见 WatchIndicatorRuleEngine 类注释。
        var result = await Task.Run(
            () => WatchIndicatorRuleEngine.Build(boardLinks, rules, knownIndicators), ct);
        _warnings.AddRange(result.Warnings);

        if (result.Links.Count == 0)
        {
            Report("规则一行都没命中，本轮不改动映射（告警见下）。");
            yield break;
        }

        var stocks = result.Links.Select(l => l.Code).Distinct().Count();
        Report($"算出 {result.Links.Count} 条映射，覆盖 {stocks} 只票。");
        yield return result.Links;
    }

    protected override Task SaveBatchAsync(IReadOnlyList<WatchIndicatorLink> batch, CancellationToken ct)
    {
        _writtenRows = watchRepository.ReplaceRuleLinks(batch);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        // 告警必须进日志。配置里把 EMI00662659 写错一个字母，结果是这条规则 0 行产出，
        // 而"0 行"跟"这个板块本来就没成分股"在库里长得一模一样 —— 不报出来没人会发现配置坏了。
        foreach (var w in _warnings) Report("⚠ " + w);

        if (_writtenRows == 0)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(
                nothingToDo: true,
                progress: _ruleCount == 0
                    ? "配置里没有启用的规则（模板默认启用锂电池那两条，是不是被注释掉了？）"
                    : "规则没命中任何票"));

        var (ruleLinks, manualLinks, coveredStocks) = watchRepository.GetCounts();
        return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(
            progress: $"写入 {_writtenRows} 条规则映射；库里现有 规则 {ruleLinks} 条 / 手挂 {manualLinks} 条，"
                + $"覆盖 {coveredStocks} 只票。"));
    }
}
