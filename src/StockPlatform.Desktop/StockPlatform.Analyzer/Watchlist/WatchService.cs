using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>跑一轮的结果，给界面报告用。</summary>
/// <param name="Detail">
/// 每条观察项**当前已知的内容**（观察项 Id → 人话），给左表显示。
///
/// ⚠ 只收"**不随时间变**"的那类：解禁多少股、预告预增多少——公告发布时就写明了，
/// 属于"要跟踪的是什么事"。像"已回购 80.2 亿元"这种会变的是**进展**，归右表，不进这里
/// （2026-09-11 为这条分界线返工过两次）。
/// </param>
/// <param name="Progress">
/// **每个事项的最近一次进展**，给右表显示。按事项发生日期倒序。
///
/// ⚠ 它不受触发窗口限制（2026-09-11 改）。原来右表直接显示 <c>Hits</c>，
/// 而 Hits 只收窗口内的（业绩预告 7 天、龙虎榜 3 天）——业绩预告一年才 4 次，
/// 于是右表一年里大部分时间是空的。**窗口该只决定"要不要提醒"，
/// 不该决定"右表显示什么"**：进展就是进展，三个月前公布的也是进展。
/// </param>
public sealed record WatchRunResult(
    int ItemCount, int Added, int Expired, int Evaluated, int Hits, int NewHits,
    IReadOnlyList<WatchHit> TopHits,
    IReadOnlyDictionary<Guid, string> Detail,
    IReadOnlyList<WatchHit> Progress,
    IReadOnlyDictionary<Guid, string> Priority);

/// <summary>
/// 观察项的协调服务（2026-09-11），见 doc/watch-item-design.md M3。
/// 把四个零件按顺序串起来，自己不做判断也不写 SQL：
///
/// <code>
/// ① 组装输入  自选/底仓 json + current.sqlite（OpenPlans、StockWatchIndicator）
/// ② 重算规则  WatchRuleEngine.Rebuild —— 派生项整组重建，手写项一行不碰
/// ③ 逐条求值  SqliteWatchReadingSource → WatchEvaluator
/// ④ 记录触发  JsonWatchItemStore.AppendHits（同项同交易日去重）
/// </code>
///
/// ⚠ 只读 current.sqlite（Fetcher 的产出），自己的状态写在 <c>watch/</c> 下。
/// 这条分界线见设计文档 §2.3：**关于标的的知识**归 current.sqlite，
/// **关于我的仓位和判断**归 Analyzer。
/// </summary>
public class WatchService
{
    private readonly AnalyzerPaths _paths;
    private readonly JsonWatchItemStore _store;
    private readonly JsonWatchlistStore _watchlist;
    private readonly JsonCorePositionStore _corePositions;

    public WatchService(AnalyzerPaths paths, JsonWatchlistStore watchlist, JsonCorePositionStore corePositions)
    {
        _paths = paths;
        _watchlist = watchlist;
        _corePositions = corePositions;
        _store = new JsonWatchItemStore(paths.WatchItemsPath, paths.WatchHitsPath);
    }

    public List<WatchItem> LoadItems() => _store.LoadItems();
    public List<WatchHit> LoadHits(int year) => _store.LoadHits(year);

    /// <summary>把若干条触发标成已处理/未处理。见 <see cref="JsonWatchItemStore.MarkHandled"/>。</summary>
    public int MarkHandled(IEnumerable<Guid> hitIds, bool handled) => _store.MarkHandled(hitIds, handled);

    /// <summary>重算派生项 + 求值 + 记录触发。返回给界面报告的统计。</summary>
    public WatchRunResult Run()
    {
        var db = _paths.CurrentDb;

        // ── ① 组装输入 ──
        var active = new Dictionary<string, string>();
        foreach (var e in _watchlist.Load())
            if (!string.IsNullOrWhiteSpace(e.Code)) active[e.Code] = e.Name ?? "";

        var core = new Dictionary<string, string>();
        foreach (var e in _corePositions.Load())
            if (!string.IsNullOrWhiteSpace(e.Code)) core[e.Code] = e.Name ?? "";

        var planRepo = new SqlitePlanAnnouncementRepository(db);
        var openPlans = SafeOpenPlans(planRepo);

        var indRepo = new SqliteWatchIndicatorRepository(db);
        var indicators = new Dictionary<string, List<WatchIndicatorLink>>();
        foreach (var code in active.Keys.Concat(core.Keys).Distinct())
        {
            try
            {
                var links = indRepo.GetLinks(code);
                if (links.Count > 0) indicators[code] = links;
            }
            catch { /* 表还没建（M1 没跑过）时跳过，不影响其余规则 */ }
        }

        // ── ② 重算 ──
        var existing = _store.LoadItems();
        var rebuilt = WatchRuleEngine.Rebuild(existing, new WatchRuleInput(active, core, openPlans, indicators));
        _store.SaveItems(rebuilt.Items.ToList());

        // ── ③ 求值 ──
        // 上次记录到的 stage：用来判"跃迁"。取每条观察项最近一次触发时记下的状态文字。
        var lastStages = BuildLastStages(rebuilt.Items);
        var source = new SqliteWatchReadingSource(db);
        var hits = new List<WatchHit>();
        var detail = new Dictionary<Guid, string>();
        var priority = new Dictionary<Guid, string>();
        var progress = new List<WatchHit>();
        int evaluated = 0;

        foreach (var item in rebuilt.Items.Where(i => i.Enabled))
        {
            evaluated++;
            var reading = source.Read(item, lastStages);

            // ════ 两张表的分界线：**左＝预计要发生，右＝已经发生**（2026-09-11 定的）════
            // 这条线比"会不会随时间变"清楚，而且**天然不重复**：同一件事要么还没发生、
            // 要么已经发生，不会两边都出现。
            //
            // 左表（预计要发生）：解禁哪天多少股、这期财报哪天披露——都还没到，正等着。
            if (IsUpcoming(item.Kind) && !string.IsNullOrEmpty(reading.StageText))
            {
                detail[item.ItemId] = reading.StageText!;
                // 左表的档也按量重算——解禁 74 股在左表也不该标 A
                priority[item.ItemId] = WatchPriority.Resolve(item, reading.Magnitude);
            }

            // 右表（已经发生）：业绩预告发了、增减持做了、回购买了多少。
            // ⚠ **不看触发窗口**——窗口是决定"要不要提醒"的，不该决定"右表显示什么"：
            // 三个月前公布的业绩预告，依然是这个事项已经发生过的最新一件事。
            if (!IsUpcoming(item.Kind)
                && reading.TradeDate is { } td && !string.IsNullOrEmpty(reading.StageText))
                progress.Add(new WatchHit
                {
                    ItemId = item.ItemId,
                    Code = item.Code,
                    Name = item.Name,
                    ItemName = item.Reason,
                    TriggerTradeDate = td,
                    ObservedValue = reading.Value,
                    Message = reading.StageText!,
                    // 跟触发记录用同一套档位算法，否则同一件事在左右两表会显示不同的档
                    Priority = WatchPriority.Resolve(item, reading.Magnitude),
                });

            if (WatchEvaluator.Evaluate(item, reading) is { } hit) hits.Add(hit);
        }

        // ── ④ 记录 ──
        var newHits = _store.AppendHits(hits);

        var top = hits
            .OrderBy(h => h.Priority)               // A < B < C
            .ThenByDescending(h => h.TriggerTradeDate)
            .Take(20).ToList();

        // 右表按"事项发生的日期"倒序——最近的/最快要发生的排最前，
        // 未来的日程（解禁）自然排在已发生的事情之上，正好是待办的读法。
        progress.Sort((a, b) => b.TriggerTradeDate.CompareTo(a.TriggerTradeDate));

        // 进展是**每次重算实时生成**的，自带的 HitId 每轮都不一样；而"已处理"标记记在
        // **落库的触发记录**上。所以按 (观察项, 事项日期) 把两者对上，让界面上的进展行
        // 带回真正的 HitId 和标记状态——否则标了也白标，下次重算就回到未处理。
        var stored = _store.LoadHits(DateTime.Today.Year)
            .Concat(_store.LoadHits(DateTime.Today.Year - 1))
            .GroupBy(h => (h.ItemId, h.TriggerTradeDate.Date))
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var p in progress)
            if (stored.TryGetValue((p.ItemId, p.TriggerTradeDate.Date), out var s))
            {
                p.HitId = s.HitId;
                p.Handled = s.Handled;
                p.HandledAt = s.HandledAt;
            }

        return new WatchRunResult(
            rebuilt.Items.Count, rebuilt.Added.Count, rebuilt.Expired.Count,
            evaluated, hits.Count, newHits, top, detail, progress, priority);
    }

    /// <summary>
    /// 这件事是**还没发生**（进左表）还是**已经发生**（进右表）？
    ///
    /// 只有 <see cref="WatchKind.ScheduleAhead"/> 是未来：它查的就是
    /// <c>MIN(日期) WHERE 日期 &gt;= 今天</c>——下次解禁、下次预约披露。
    /// 其余都是已经发生的事：业绩预告发了、增减持做了、龙虎榜上了、回购买了、股价跌破了。
    ///
    /// 这条线把"重复显示"从根上消掉了：同一件事要么在左要么在右，不会两边都有。
    /// 解禁发生之后就不在"未来"里了，左表那条自然只剩名字。
    /// </summary>
    private static bool IsUpcoming(string kind) => kind is WatchKind.ScheduleAhead;

    private static Dictionary<string, PlanAnnouncement> SafeOpenPlans(SqlitePlanAnnouncementRepository repo)
    {
        try { return repo.GetOpenPlans(PlanKind.Buyback); }
        catch { return []; }   // 表还没建（M2 没跑过）——不该让整轮挂掉
    }

    /// <summary>
    /// 每条观察项"上次报到的状态"。从历史触发记录里还原，这样 stage 跃迁只在**真的变了**
    /// 的时候报一次，而不是每天报一次现状。
    /// </summary>
    private Dictionary<Guid, string> BuildLastStages(IReadOnlyList<WatchItem> items)
    {
        var map = new Dictionary<Guid, string>();
        var year = DateTime.Today.Year;
        var hits = _store.LoadHits(year).Concat(_store.LoadHits(year - 1))
            .OrderByDescending(h => h.TriggerTradeDate);

        foreach (var h in hits)
        {
            if (map.ContainsKey(h.ItemId)) continue;
            // 消息里 "→ **新状态**" 或 "→ 现状：X" 的那一段就是上次报的状态
            var idx = h.Message.LastIndexOf('→');
            if (idx < 0) continue;
            var tail = h.Message[(idx + 1)..].Trim().TrimStart('现', '状', '：').Trim('*', ' ');
            if (tail.Length > 0) map[h.ItemId] = tail;
        }
        return map;
    }
}
