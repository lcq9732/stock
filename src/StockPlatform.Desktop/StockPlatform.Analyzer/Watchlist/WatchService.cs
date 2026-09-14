using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 跑一轮的结果，给界面报告用——现在**只剩计数**。
///
/// ⚠ 两轮瘦身都记在这儿，免得以后有人以为是漏写的：
/// · 2026-09-14 上午删 <c>Detail</c> / <c>Progress</c> / <c>Priority</c>——左右两表时代的产物，
///   观察项页改成事件叙述之后一直算着却没人读。
/// · 2026-09-14 下午删 <c>TopHits</c> / <c>CodePriority</c>——「档」整套取消了（见
///   <see cref="WatchItem"/> 文件头）。TopHits 唯一的用途就是数"A 档几条"。
/// </summary>
public sealed record WatchRunResult(
    int ItemCount, int Added, int Expired, int Evaluated, int Hits, int NewHits);

/// <summary>
/// 观察项页看哪些票（2026-09-14）。
///
/// ════ 为什么要分范围 ════
/// 实测用户那三个清单：自选股 61 只、主动仓 30 只、底仓 6 只，但**真正有钱在里面的只有 9 只**
/// （主动仓买了没卖的 4 只 ＋ 底仓 6 只，华域汽车两边都有）。原来一律盯 67 只，差 7 倍。
///
/// 用户 2026-09-14 的判断："已建仓的每天关注重要事件；没建仓的，建仓前看一下就行"——
/// **建仓前是"查"（我主动去问），建仓后是"盯"（它主动来找我）**。
/// "查"那件事已经由【分析详情】覆盖（任何票随时能开、不用先加自选），
/// 所以这一页只服务"盯"。
///
/// ⚠ 自选股里**没进主动仓**的那 31 只，两个范围都不含——它们是各选股法丢进来的
/// 算法验证样本，页面上自己写着"只用来统计各方法准不准，不代表要买"。
/// </summary>
public enum WatchScope
{
    /// <summary>有钱在里面的：主动仓还持有的（部分卖出仍算）＋ 底仓全部。默认。</summary>
    Holding,

    /// <summary>主动仓全部 ＋ 底仓全部——包括打算买还没买的。</summary>
    Tracked,
}

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
        // ⚠ **只取主动仓**（2026-09-14 修）。原来这里取的是整份 watchlist.json，
        // 于是 61 只自选股全被当成主动仓——而规则引擎给主动仓挂的是**短线纪律**（跌破 MA20）。
        // 等于拿短线止损口径盯着一堆"只用来统计准确率、本来就不打算买"的验证样本。
        var active = new Dictionary<string, string>();
        foreach (var e in _watchlist.Load().Where(e => e.IsInTradePool))
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

        return new WatchRunResult(
            rebuilt.Items.Count, rebuilt.Added.Count, rebuilt.Expired.Count,
            evaluated, hits.Count, newHits);
    }

    /// <summary>
    /// 范围内的票（代码 → 名称）。
    ///
    /// ⚠ 「持仓」复用 <see cref="WatchlistEntry.IsHoldingPosition"/>——晨检用的就是它，
    /// 语义是"买过且没平完，**部分卖出仍然算持仓**"。这种判据只该有一份，
    /// 在这儿另写一个 <c>Shares &gt; 0 &amp;&amp; SellDate == null</c> 迟早会跟晨检说法不一。
    ///
    /// ⚠ 底仓**一律算持仓**：进了底仓清单就是打算长期拿的，哪怕还没录买入记录
    /// （实测 6 只底仓里 5 只 Lots 是空的）。
    /// </summary>
    private Dictionary<string, string> CodesInScope(WatchScope scope)
    {
        var names = new Dictionary<string, string>();

        var pool = _watchlist.Load().Where(e => e.IsInTradePool);
        if (scope == WatchScope.Holding) pool = pool.Where(e => e.IsHoldingPosition);
        foreach (var e in pool)
            if (!string.IsNullOrWhiteSpace(e.Code)) names[e.Code] = e.Name ?? "";

        foreach (var e in _corePositions.Load())
            if (!string.IsNullOrWhiteSpace(e.Code)) names[e.Code] = e.Name ?? "";

        return names;
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
    /// ⚠ 它**不依赖 <see cref="Run"/> 的结果**（2026-09-14 「档」取消后彻底独立）：
    /// 事件是现读库的，抓取程序刚落库的新公告，下一次打开就能看到，不用先重算。
    /// </summary>
    /// <param name="scope">看哪些票，见 <see cref="WatchScope"/>。默认只看有钱在里面的。</param>
    public (IReadOnlyList<StockWatchEvents> Stocks, IReadOnlyList<MarketWatchItem> Market)
        BuildEvents(WatchScope scope = WatchScope.Holding)
    {
        var names = CodesInScope(scope);

        var source = new SqliteStockEventSource(_paths.CurrentDb);
        var notes = new StockNoteStore(_paths.NotesDir);

        var stocks = new List<StockWatchEvents>();
        foreach (var (code, name) in names)
        {
            var events = source.Read(code);
            // 一件事都没有的票不占一行——面板是用来看"有什么动静"的，不是持仓清单
            if (events.Count == 0) continue;

            stocks.Add(new StockWatchEvents(code, name, events, notes.ReadOpinion(code) ?? ""));
        }

        // ⚠ 行序按"**离今天最近**"排，不是按最新日期排（那样会翻车）：
        // 解禁是日程表，库里躺着 2030 年的解禁计划。按 MAX(日期) 排的话，
        // 一只五年后才解禁、眼下什么都没发生的票会稳居第一页——它恰恰是最不用管的。
        // 取"到今天的距离"，明天解禁和昨天出的公告都排前面，2030 年和三年前都沉底，
        // 这才是"最近有什么动静"该有的读法。
        //
        // 一样近的时候按代码排——纯粹为了次序稳定，换个说法就是"别让相同的输入排出不同的页面"。
        var today = DateTime.Today;
        var ordered = stocks
            .OrderBy(s => Proximity(s, today))
            .ThenBy(s => s.Code, StringComparer.Ordinal)
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
