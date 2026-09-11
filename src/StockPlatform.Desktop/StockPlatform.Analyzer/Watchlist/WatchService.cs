using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>跑一轮的结果，给界面报告用。</summary>
public sealed record WatchRunResult(
    int ItemCount, int Added, int Expired, int Evaluated, int Hits, int NewHits,
    IReadOnlyList<WatchHit> TopHits);

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
        int evaluated = 0;

        foreach (var item in rebuilt.Items.Where(i => i.Enabled))
        {
            evaluated++;
            var reading = source.Read(item, lastStages);
            if (WatchEvaluator.Evaluate(item, reading) is { } hit) hits.Add(hit);
        }

        // ── ④ 记录 ──
        var newHits = _store.AppendHits(hits);

        var top = hits
            .OrderBy(h => h.Priority)               // A < B < C
            .ThenByDescending(h => h.TriggerTradeDate)
            .Take(20).ToList();

        return new WatchRunResult(
            rebuilt.Items.Count, rebuilt.Added.Count, rebuilt.Expired.Count,
            evaluated, hits.Count, newHits, top);
    }

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
