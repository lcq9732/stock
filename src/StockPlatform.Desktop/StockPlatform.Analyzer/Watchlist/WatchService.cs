using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 跑一轮的结果，给界面报告用。
///
/// ⚠ 2026-09-14 瘦身：原来还带 <c>Detail</c>（左表"预计要发生"）、<c>Progress</c>（右表
/// "已经发生"）、<c>Priority</c>（逐条观察项的档）三项。观察项页改成一股一行的事件叙述之后
/// 那两张表没了，这三项一直算着却没人读——连同填它们的那段 SQL 回填一起删掉。
/// 叙述走的是另一条路（<see cref="WatchService.BuildEvents"/> → SqliteStockEventSource）。
/// </summary>
/// <param name="CodePriority">
/// 每只票**最紧要的那个档**（A&lt;B&lt;C）。一股一行的叙述视图要给整行定个档，
/// 而档位是按量算出来的（解禁 74 股不是 A 档），只有求值过程知道——
/// 所以在这里顺手攒出来，别让界面再算一遍。
/// </param>
public sealed record WatchRunResult(
    int ItemCount, int Added, int Expired, int Evaluated, int Hits, int NewHits,
    IReadOnlyList<WatchHit> TopHits,
    IReadOnlyDictionary<string, string> CodePriority);

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
        var codePriority = new Dictionary<string, string>();
        int evaluated = 0;

        foreach (var item in rebuilt.Items.Where(i => i.Enabled))
        {
            evaluated++;
            var reading = source.Read(item, lastStages);

            // 整只票的代表档＝它身上所有观察项里最高的那个
            if (reading.TradeDate is not null)
            {
                var p = WatchPriority.Resolve(item, reading.Magnitude);
                codePriority[item.Code] = codePriority.TryGetValue(item.Code, out var cur)
                    ? WatchEventComposer.TopPriority([cur, p])
                    : p;
            }

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
            evaluated, hits.Count, newHits, top, codePriority);
    }

    /// <summary>
    /// 这只票离今天最近的那件事有多远（天）。行序按它排。
    ///
    /// ⚠ **行业指标不算**（2026-09-14）：碳酸锂指数天天更新，把它算进来的话，
    /// 全部 107 只锂电池股每天都是"距今 0 天"，整页行序就被一条外部指标决定了。
    /// 它是影响这只票的环境，不是这只票出的事。
    /// 一只票要是**只有**指标事件，那就只能拿它当依据——总比没有次序强。
    /// </summary>
    private static double Proximity(StockWatchEvents s, DateTime today)
    {
        var own = s.Events.Where(e => e.Category != WatchCategory.Indicator).ToList();
        var pool = own.Count > 0 ? own : s.Events;
        return pool.Min(e => Math.Abs((e.Date.Date - today).TotalDays));
    }

    /// <summary>
    /// 读**单只票**的事件叙述，给详情窗用。
    ///
    /// ⚠ 不挂观察项、不写任何文件（2026-09-14 定的）：没跟踪的票也能点开看一眼，
    /// 但"想长期盯"该走加入自选那条正门。点一下详情就悄悄多出一堆观察项，
    /// 那清单很快就没人信了。
    /// </summary>
    public IReadOnlyList<WatchEvent> ReadEvents(string code)
    {
        try { return new SqliteStockEventSource(_paths.CurrentDb).Read(code); }
        catch { return []; }
    }

    /// <summary>
    /// 把一轮的结果摊成**一股一行的事件叙述**（2026-09-14 重构）。
    ///
    /// ════ 为什么不复用 Run() 里那两张表 ════
    /// 那两张表是**按事项**组织的：左边"要发生什么"、右边"发生了什么"。
    /// 用户的反馈是：从左边看到事件，还得去右边找它成没成事实——一件事被拆在两处。
    /// 所以叙述这条线**直接从库里重读**（<see cref="SqliteStockEventSource"/>），
    /// 一只票身上发生过什么按时间连成一条线，回购那种跨几个月的过程尤其需要。
    ///
    /// 两条线共用的只有**档位**：档是按量算出来的，那个算法在求值里，不重复实现。
    /// </summary>
    /// <param name="run">刚跑完的那一轮，用它的 <see cref="WatchRunResult.CodePriority"/> 定行档。</param>
    public (IReadOnlyList<StockWatchEvents> Stocks, IReadOnlyList<MarketWatchItem> Market)
        BuildEvents(WatchRunResult run)
    {
        var names = new Dictionary<string, string>();
        foreach (var e in _watchlist.Load())
            if (!string.IsNullOrWhiteSpace(e.Code)) names[e.Code] = e.Name ?? "";
        foreach (var e in _corePositions.Load())
            if (!string.IsNullOrWhiteSpace(e.Code)) names[e.Code] = e.Name ?? "";

        var source = new SqliteStockEventSource(_paths.CurrentDb);
        var notes = new StockNoteStore(_paths.NotesDir);

        var stocks = new List<StockWatchEvents>();
        foreach (var (code, name) in names)
        {
            var events = source.Read(code);
            // 一件事都没有的票不占一行——面板是用来看"有什么动静"的，不是持仓清单
            if (events.Count == 0) continue;

            run.CodePriority.TryGetValue(code, out var priority);
            stocks.Add(new StockWatchEvents(
                code, name, priority ?? WatchPriority.LogOnly, events,
                notes.ReadOpinion(code) ?? ""));
        }

        // ⚠ 行序按"**离今天最近**"排，不是按最新日期排（那样会翻车）：
        // 解禁是日程表，库里躺着 2030 年的解禁计划。按 MAX(日期) 排的话，
        // 一只五年后才解禁、眼下什么都没发生的票会稳居第一页——它恰恰是最不用管的。
        // 取"到今天的距离"，明天解禁和昨天出的公告都排前面，2030 年和三年前都沉底，
        // 这才是"最近有什么动静"该有的读法。
        //
        // 档位只在同样近的时候才做次序——不能让 C 档的今天沉到 A 档的三个月前下面。
        var today = DateTime.Today;
        var ordered = stocks
            .OrderBy(s => Proximity(s, today))
            .ThenBy(s => s.Priority)
            .ToList();

        IReadOnlyList<MarketWatchItem> market;
        try { market = new SqliteMarketWatchSource(_paths.CurrentDb).Read(); }
        catch { market = []; }

        return (ordered, market);
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
