using System.Runtime.CompilerServices;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;

namespace StockPlatform.Tasks;

/// <summary>
/// 【回购公告进展】（2026-09-11，见 doc/watch-item-design.md M2）。
///
/// ════ 它回答什么 ════
/// **「这家公司的回购到底开始买了没有」** —— 法定披露里这个信号最快就是 T+1：
/// 首次回购股份的事实发生后**次一交易日**必须公告；回购期间每月前三个交易日披露截至上月末进展；
/// 累计每增加总股本 1% 再公告一次。回购走集中竞价，盘中混在主力资金里，
/// 资金流/大宗/席位都识别不出来——所以没有比公告更快的合法渠道，这一项就是天花板。
///
/// ════ 为什么要落成结构化的表 ════
/// 不是为了"记录回购"（想知道买没买打开公告看一眼就行）。是为了让 **L1 能自动摘除观察项**：
/// 方案的生命周期不落成 stage，程序永远不知道回购已结束、该把待办撤下来，而人一定会忘。
///
/// ════ 复用两条现成通道，不新写 provider ════
/// ① 发现：<see cref="IAnnouncementSearchProvider"/> 巨潮全市场**标题**检索关键词「回购」——
///    一次分页覆盖全市场，比按股票查几十个请求更省，而且更全：自选股之外新冒出来的方案也捕获。
/// ② 正文：<see cref="IAnnouncementDetailFetcher"/> 东财直接给纯文本，绕开 PDF 解析。
/// 两条都是 <c>OrderWinAnnouncement</c> 那条管线在用的，已验证。
///
/// ════ 一批＝一天的命中 ════
/// 水位线是库里的 <c>MAX(announce_date)</c>，批的粒度是「一个搜索日窗」——
/// 水位线粒度（日）不粗于截断粒度（日窗），所以不需要额外的完成度表
/// （对比【客户与供应商】：那边一批 2000 行、水位线是「年」，粗两个数量级才必须加表）。
///
/// ════ 失败语义＝累积 ════
/// 某条公告取不到正文是**软失败**：标题那部分照样落库（stage 还在，数值留 null），
/// 不算整轮失败。⚠ <c>cum_amount=0</c>（公告明说"尚未实施"）跟 <c>null</c>（没抽到）
/// 是两回事，混了的话"抽取坏了"会显示成"公司没买"。
/// </summary>
public sealed class PlanWatchTask(
    IPlanAnnouncementRepository repository,
    IAnnouncementSearchProvider searchProvider,
    IAnnouncementDetailFetcher detailFetcher) : FetchTaskBase<PlanAnnouncement>
{
    public override FetchActionId Id => FetchActionId.StepPlanWatch;

    /// <summary>
    /// 回看天数。法定节奏是"次一交易日"和"每月前三个交易日"，回看 14 天足够覆盖长假 +
    /// 连着几天没跑的情况；重复扫同一天是安全的（主键 upsert 去重）。
    /// 首轮库里空时用这个更长的窗，把当前还在进行的方案捞回来。
    /// </summary>
    private const int LookbackDays = 14;

    /// <summary>
    /// 首轮回看天数。
    ///
    /// ⚠ **不能设得更长**（2026-09-11 实机验证）：正文是靠
    /// <see cref="IAnnouncementDetailFetcher"/> 在"该股票**最近 100 条**公告"里按标题+日期匹配的，
    /// 一年前的公告早就不在那个列表里了——实测回看 400 天时 34 条命中只有 1 条取到正文，
    /// 抽取率 0%。抓回来一堆空壳记录，比不抓更糟（看起来像"这些公司都没在回购"）。
    ///
    /// 90 天足够：回购期间**每月**都有进展公告，三个月内必然至少露面一次；
    /// 判断"方案是否还在进行中"也不依赖抓到几个月前那份方案公告，见
    /// <c>IPlanAnnouncementRepository.GetOpenPlans</c>。
    /// </summary>
    private const int FirstRunLookbackDays = 90;

    /// <summary>巨潮每页大致条数，只用来估"是不是撞到翻页上限了"，不必精确。</summary>
    private const int PageSizeGuess = 10;

    private int _hits, _parsed, _noDetail;
    private readonly List<string> _warnings = [];

    protected override async IAsyncEnumerable<IReadOnlyList<PlanAnnouncement>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _hits = _parsed = _noDetail = 0;
        _warnings.Clear();

        var watermark = repository.GetLatestAnnounceDate(PlanKind.Buyback);
        var today = DateOnly.FromDateTime(DateTime.Today);
        // 首轮（库里空）往回捞一年多：回购期限通常 12 个月，这样才能把"还在进行中"的方案接上。
        var days = watermark is null ? FirstRunLookbackDays : LookbackDays;
        var start = today.AddDays(-days);
        Report($"搜索关键词「回购」，{start:yyyy-MM-dd} 至 {today:yyyy-MM-dd}"
               + (watermark is null
                   ? $"（首轮，回看 {days} 天——再往前正文取不到，见类注释）"
                   : $"（增量，库里最新到 {watermark:yyyy-MM-dd}）"));

        // ⚠ **必须按天切片搜索，不能一次搜整个区间**（2026-09-11 实测发现）：
        // CninfoAnnouncementSearchProvider 有 MaxPages=30 的硬上限，而「回购」是高频关键词——
        // 14 天一次搜正好撞满 30 页（285 条）就 break，**剩下的静默丢掉、没有任何告警**。
        // 漏掉的公告就是漏掉的信号，而这一项的全部价值就是不漏掉「首次回购」那一条。
        // 按天切之后单日只有几页，撞不到上限；总请求数反而差不多（页数是一样的）。
        // 这跟 project_em_sort_key_must_be_unique 记的是同一类病：分页截断不报错。
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();

            List<AnnouncementSearchHit> dayHits;
            try
            {
                dayHits = await searchProvider.SearchAsync("回购", day, day, null, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 单日失败不连累其余：这是累积语义，不是快照。
                _warnings.Add($"{day:yyyy-MM-dd} 检索失败：{ex.Message}");
                continue;
            }

            if (dayHits.Count == 0) continue;

            // 撞上限了就告警——宁可吵，也不要静默漏。
            if (dayHits.Count >= PageSizeGuess * 30)
                _warnings.Add($"{day:yyyy-MM-dd} 命中 {dayHits.Count} 条，疑似撞到翻页上限，可能有遗漏。");

            // ⚠ 标题筛选：《关于回购股份事项前十名股东和前十名无限售条件股东持股情况的公告》
            // 标题含"回购"但内容是股东名册。不排除会解析出一条各字段全空的"进展"，
            // **看起来像回购停滞** —— 本项唯一会产生错误结论（而非漏数据）的坑。
            var group = dayHits.Where(h => PlanAnnouncementExtractor.IsBuybackAnnouncement(h.Title)).ToList();
            _hits += group.Count;
            if (group.Count == 0) continue;

            Report($"{day:yyyy-MM-dd} 命中 {dayHits.Count} 条，真回购公告 {group.Count} 条，开始取正文…");

            var batch = new List<PlanAnnouncement>();
            foreach (var hit in group)
            {
                ct.ThrowIfCancellationRequested();
                string? content = null;
                string artCode = "";
                try
                {
                    var detail = await detailFetcher.FetchDetailAsync(
                        hit.Code, hit.Title, DateOnly.FromDateTime(hit.PublishDate), ct);
                    if (detail is { } d) { artCode = d.ArtCode; content = d.Content; }
                    else _noDetail++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 软失败：取不到正文照样把标题那部分留下来
                    _noDetail++;
                    _warnings.Add($"{hit.Code} {hit.Title}：正文取不到（{ex.Message}）");
                }

                batch.Add(PlanAnnouncementExtractor.Extract(
                    hit.Code, hit.Name, hit.Title, hit.PublishDate, content, artCode, hit.PdfUrl ?? ""));
                _parsed++;
            }

            Report($"{day:yyyy-MM-dd} 解析 {batch.Count} 条", _parsed, _hits);
            yield return batch;
        }
    }

    protected override Task SaveBatchAsync(IReadOnlyList<PlanAnnouncement> batch, CancellationToken ct)
    {
        repository.Upsert(batch);
        return Task.CompletedTask;
    }

    protected override Task<TaskRunResult?> OnCompletedAsync(
        TaskRunStats stats, TaskRunArgs args, CancellationToken ct)
    {
        foreach (var w in _warnings.Take(20)) Report("⚠ " + w);
        if (_warnings.Count > 20) Report($"⚠ 另有 {_warnings.Count - 20} 条告警未列出。");

        if (stats.Items == 0)
            return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(
                nothingToDo: true, progress: "本轮没有新的回购公告"));

        var (rows, stocks, open) = repository.GetCounts(PlanKind.Buyback);
        var msg = $"本轮解析 {_parsed} 条（{_noDetail} 条没取到正文，已按标题留档）；"
                  + $"库里现有 {rows} 条 / {stocks} 只票，**{open} 个方案还在进行中**。";
        return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(progress: msg));
    }
}
