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
/// ════ 一批 ≤ 一天的命中 ════
/// 水位线是库里的 <c>MAX(announce_date)</c>，批的粒度不粗于「一个搜索日窗」——
/// 量大的日子（每月前三个交易日是法定进展披露窗口，两三百条）会拆成几批交，见 <c>YieldChunk</c>。
/// 水位线粒度（日）不粗于截断粒度，所以不需要额外的完成度表
/// （对比【客户与供应商】：那边一批 2000 行、水位线是「年」，粗两个数量级才必须加表）。
///
/// ════ 失败语义＝累积 ════
/// 某条公告取不到正文是**软失败**：标题那部分照样落库（stage 还在，数值留 null），
/// 不算整轮失败。⚠ <c>cum_amount=0</c>（公告明说"尚未实施"）跟 <c>null</c>（没抽到）
/// 是两回事，混了的话"抽取坏了"会显示成"公司没买"。
///
/// ════ 每轮尾巴上回补空壳（2026-09-17）════
/// 软失败留下的空壳行**增量窗口再也不会路过**——水位线早越过那天了。所以每轮跑完新公告之后
/// 再挑一批 <c>art_code</c> 为空的旧行，按 code+标题直接取一次正文（不用重新检索）补上数值，
/// 见 <see cref="BackfillMax"/>。当天新抓那段配不上的，第二天这一段会自动再试一次。
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

    /// <summary>
    /// 每轮回补多少条空壳记录（2026-09-17）。
    ///
    /// 取正文是软失败——配不上就只落标题、数值全 null。这些行**不会**被增量重抓捡起来：
    /// 水位线早越过那天了，下一轮根本不搜那一段。不专门找出来，它们就永远是空壳。
    /// 09-17 体检：库里 1959 条里有 38 条是这样躺着的（1.9%），最早的能追到两个月前。
    ///
    /// 一条两个请求、限流 1 秒一个，实测 1.3 秒/条——60 条≈80 秒，
    /// 挂在一轮 20 分钟的任务尾巴上不显眼，积压再多也是几轮之内补完。
    /// 上限存在的意义是**别让积压把日更那段挤到看门狗线以外**，不是省请求。
    /// </summary>
    private const int BackfillMax = 60;

    /// <summary>
    /// 取正文取到第几条报一次进度（2026-09-14 补，这是被误杀出来的）。
    ///
    /// 原来内层这个 foreach 整段一句话不说：先报「命中 N 条，开始取正文…」，等全天取完才报
    /// 「解析 N 条」。单条正文要两个请求（先在该股最近 100 条公告里匹配 art_code、再取正文），
    /// 限流 1 秒一个，实测 1.3 秒/条——**241 条就是 5 分 13 秒的静默**，刚好越过
    /// <see cref="QuietWatchdog"/> 的 5 分钟线，于是一个正在正常前进的任务被判成卡死掐断
    /// （2026-09-14 08:25，停在 09-02）。
    ///
    /// ⚠ 而且这不是偶发：法定节奏是「回购期间**每月前三个交易日**披露上月进展」，月初那几天
    ///   的量是平日十几倍（09-02 有 241 条，09-04 只有 2 条）——**每个月初都会撞一次**。
    ///
    /// 20 条≈26 秒，比 5 分钟阈值密一个数量级，够。
    /// </summary>
    private const int ProgressEvery = 20;

    /// <summary>
    /// 一天的命中攒到这么多条就先交出去落库（2026-09-14）。
    ///
    /// ════ 为什么不整天一批 ════
    /// <c>yield return</c> 原来在内层循环之外，所以被掐断时**这一天已经抓到的全部丢弃**、一行不落。
    /// 水位线（<c>MAX(announce_date)</c>）停在 09-02 之前，下一次算出来的起点还是同一段，
    /// 跑到 09-02 再死一次——今天失败当天不重试，明天来照样撞。这就是 <see cref="QuietWatchdog"/>
    /// 类注释里龙虎榜那种**自锁死**换了个地方：掐断走失败分支、什么都没留下，所以永远过不去。
    ///
    /// 分段之后，就算被掐（网络真挂了之类），已抓的那几十条也进了库，水位线能往前挪。
    ///
    /// ⚠ 代价是「一天可能只写了一半」变得更常见——这本来就是回看 14 天要兜的情况
    /// （见 <see cref="ResolveSearchWindow"/>「为什么还要往回多退 14 天」），主键 upsert 去重，
    /// 重抓只多花请求、不会写脏。
    ///
    /// 60 是 <see cref="ProgressEvery"/> 的整数倍，这样每次交批之前刚好有一条进度。
    /// </summary>
    private const int YieldChunk = 60;

    /// <summary>
    /// 定本轮要搜的起点。**纯函数，所以能单独测**（见 PlanWatchResumeTests）。
    ///
    /// ════ 这里修过一个会静默丢数据的 bug（2026-09-11）════
    /// 原来是 <c>start = today.AddDays(-(水位线有没有 ? 14 : 90))</c>——
    /// 水位线只用来**选回看几天**，没用来**定起点**。后果：首轮跑到 07-02 被中断后再点一次，
    /// 起点算成 today−14＝08-28，**07-03～08-27 这 56 天永久跳过**，而且不报任何错。
    /// 跟这套设计要躲的其它坑同一类：不报错、但数据少了一截。
    ///
    /// 现在起点从**水位线**算，中断在哪就从哪续。
    ///
    /// ════ 为什么还要往回多退 14 天 ════
    /// 水位线是 <c>MAX(announce_date)</c>，它只说明"这天有公告入库了"，不保证那天**抓全了**——
    /// 骨架会在 MaxItems/Deadline 到点时从批中间收尾，那天可能只写了一半。
    /// 往回退一段重抓，靠主键 upsert 去重，只多花请求、不会写脏。
    /// 日更场景下水位线就是昨天，退 14 天正好是设计里的增量窗口，行为跟以前一致。
    ///
    /// ════ 为什么要夹在首轮窗口内 ════
    /// 再往前正文取不到（东财按"该股最近 100 条公告"匹配标题，见 <see cref="FirstRunLookbackDays"/>），
    /// 捞回来的只会是一堆没有数值的空壳记录——那比不抓更糟，在库里长得像"这些公司都没在回购"。
    /// </summary>
    /// <returns>(起点, 是不是首轮)。</returns>
    public static (DateOnly Start, bool IsFirstRun) ResolveSearchWindow(DateTime? watermark, DateOnly today)
    {
        var firstRunStart = today.AddDays(-FirstRunLookbackDays);
        if (watermark is not { } w) return (firstRunStart, true);

        var start = DateOnly.FromDateTime(w).AddDays(-LookbackDays);
        if (start < firstRunStart) start = firstRunStart;   // 再往前正文取不到
        if (start > today) start = today;                   // 水位线在未来（手工塞过数据）时兜一下
        return (start, false);
    }

    private int _hits, _parsed, _noDetail, _refillTried, _refillOk;
    private readonly List<string> _warnings = [];

    protected override async IAsyncEnumerable<IReadOnlyList<PlanAnnouncement>> FetchAsync(
        TaskRunArgs args, [EnumeratorCancellation] CancellationToken ct)
    {
        repository.EnsureSchema();
        _hits = _parsed = _noDetail = _refillTried = _refillOk = 0;
        _warnings.Clear();

        var watermark = repository.GetLatestAnnounceDate(PlanKind.Buyback);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (start, isFirstRun) = ResolveSearchWindow(watermark, today);
        Report($"搜索关键词「回购」，{start:yyyy-MM-dd} 至 {today:yyyy-MM-dd}"
               + (isFirstRun
                   ? $"（首轮，回看 {FirstRunLookbackDays} 天——再往前正文取不到，见类注释）"
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
            var doneToday = 0;
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
                    else
                    {
                        // ⚠ 这条**也要留名字**（2026-09-17 补）：原来这条路径只 ++ 计数，
                        // 结果日志里只剩「N 条没取到正文」一个数字，是哪几只、为什么配不上，
                        // 不翻库根本看不出来——09-17 那 6 条全是标题全角括号/多余空格配不上，
                        // 归一化就能修，但当时没人知道该去修什么。
                        _noDetail++;
                        _warnings.Add($"{hit.Code} {hit.Title}：东财最近 100 条公告里没匹配到（标题或日期对不上）");
                    }
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
                doneToday++;

                // 心跳。这一段是全任务最慢的地方（1.3 秒/条），不出声就会被看门狗当成卡死，
                // 见 ProgressEvery 的注释。
                if (doneToday % ProgressEvery == 0 && doneToday < group.Count)
                    Report($"{day:yyyy-MM-dd} 取正文 {doneToday}/{group.Count}…", _parsed, _hits);

                // 攒够一段就先落库，别把一天的成果全押在"能跑到最后"上，见 YieldChunk 的注释。
                if (batch.Count >= YieldChunk)
                {
                    yield return batch;
                    batch = [];
                }
            }

            Report($"{day:yyyy-MM-dd} 解析 {doneToday} 条", _parsed, _hits);
            if (batch.Count > 0) yield return batch;
        }

        // ════ 回补：把之前留下的空壳记录再取一次正文（2026-09-17）════
        // 为什么要有这一段，见 BackfillMax 和 IPlanAnnouncementRepository.GetMissingDetail 的注释：
        // 空壳行是软失败留下的，增量窗口再也不会路过它们。
        // 放在日循环**之后**：先把今天的新公告落袋为安，回补是锦上添花，被骨架的
        // MaxItems/Deadline 掐掉也无所谓——幂等，下一轮接着补。
        var since = today.AddDays(-FirstRunLookbackDays).ToDateTime(TimeOnly.MinValue);
        var stale = repository.GetMissingDetail(PlanKind.Buyback, since, BackfillMax);
        if (stale.Count == 0) yield break;

        Report($"回补 {stale.Count} 条只有标题、没取到正文的旧记录…");
        var refill = new List<PlanAnnouncement>();
        foreach (var old in stale)
        {
            ct.ThrowIfCancellationRequested();
            (string ArtCode, string Content)? detail = null;
            try
            {
                detail = await detailFetcher.FetchDetailAsync(
                    old.Code, old.Title, DateOnly.FromDateTime(old.AnnounceDate), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 抛异常＝网络/限流那类真故障，留名字（输出那边有 20 行上限兜着）。
                // 而"配上了但还是没匹配到"（返回 null）那条路**不告警**：积压有多少条就会刷多少行，
                // 把当轮真正的新问题淹了——那种只汇总成末尾一行「回补 N/M」。
                _warnings.Add($"回补 {old.Code} 失败：{ex.Message}");
            }

            _refillTried++;
            // ⚠ 心跳要在**成功与否之前**打（这一段最慢 1.3 秒/条，跟主循环同一个看门狗）：
            // 放在 continue 后面的话，一整批都配不上时就是整段静默。
            if (_refillTried % ProgressEvery == 0 && _refillTried < stale.Count)
                Report($"回补 {_refillTried}/{stale.Count}…");

            if (detail is not { } dd) continue;   // 还是配不上：不写，下一轮再试

            _refillOk++;
            // stage 只从标题定（PlanAnnouncementExtractor.ClassifyStage），所以重抽出来的主键
            // (code, kind, announce_date, stage) 跟原行一致——upsert 是就地补数值，不会多出一行。
            refill.Add(PlanAnnouncementExtractor.Extract(
                old.Code, old.Name, old.Title, old.AnnounceDate, dd.Content, dd.ArtCode, old.SourceUrl));
            if (refill.Count >= YieldChunk) { yield return refill; refill = []; }
        }

        Report($"回补完成：{_refillOk}/{_refillTried} 条补上了正文");
        if (refill.Count > 0) yield return refill;
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
                  + (_refillTried > 0 ? $"回补旧空壳 {_refillOk}/{_refillTried} 条；" : "")
                  + $"库里现有 {rows} 条 / {stocks} 只票，**{open} 个方案还在进行中**。";
        return Task.FromResult<TaskRunResult?>(TaskRunResult.Ok(progress: msg));
    }
}
