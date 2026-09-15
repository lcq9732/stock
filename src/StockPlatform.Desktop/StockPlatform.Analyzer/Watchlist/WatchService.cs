using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.Watchlist;

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
/// 观察项的数据服务（2026-09-11 建，2026-09-15 瘦成只读）。
///
/// 现在只做一件事：**把一只票身上发生过什么，从 <c>current.sqlite</c> 读成事件叙述**。
///
/// ════ 曾经的那台机器已经拆了（2026-09-15）════
/// 原设计（doc/watch-item-design.md M1/M3）在这里跑一整套：规则引擎重算派生观察项（自动挂/摘）
/// → 取值器逐条求值 → 判触发 → 落 <c>watch/items.json</c> 和 <c>watch/hits-yyyy.json</c>。
/// 那套是为**推送 / 日报**造的：档位 A=推送、B=进日报、C=只落库，一个字母决定走哪个出口。
///
/// **那三个出口一个都没做过。** 而观察项页改成事件叙述之后，"在盯什么"和"报过什么"
/// 这两份清单也都没了界面——叙述本身已经把内容摆出来了，中间那层只剩下没人读的 json。
/// 所以整套删掉：`WatchRuleEngine` / `WatchEvaluator` / `SqliteWatchReadingSource` /
/// `JsonWatchItemStore` / `WatchItem` / `WatchHit` / `WatchItemDisplay`。
///
/// 哪天真要做晨间提醒，按 doc/watch-item-design.md 里记下的 `first_seen` 方案重做——
/// 那条路比恢复这套旧机器更直：**"这行第一次进库是什么时候"是数据的事实，
/// 而旧机器把事实和"我看过什么"混在一起存，重抓/补历史/中断续抓三件事上都会骗人。**
///
/// ⚠ **只读 <c>current.sqlite</c>**（Fetcher 的产出），自己不写任何文件。
/// 唯一会写的是【分析笔记】，那是人主动写的，走 <see cref="StockNoteStore"/>。
/// </summary>
public class WatchService
{
    private readonly AnalyzerPaths _paths;
    private readonly JsonWatchlistStore _watchlist;
    private readonly JsonCorePositionStore _corePositions;

    public WatchService(AnalyzerPaths paths, JsonWatchlistStore watchlist, JsonCorePositionStore corePositions)
    {
        _paths = paths;
        _watchlist = watchlist;
        _corePositions = corePositions;
    }

    /// <summary>
    /// 读**单只票**的事件叙述，给【分析详情】右上角那块用。
    ///
    /// ⚠ 不挂观察项、不写任何文件：没跟踪的票也能点开看一眼，
    /// 但"想长期盯"该走加入主动仓那条正门。
    /// </summary>
    public IReadOnlyList<WatchEvent> ReadEvents(string code)
    {
        try { return new SqliteStockEventSource(_paths.CurrentDb).Read(code); }
        catch { return []; }
    }

    /// <summary>
    /// 范围内每只票的事件叙述 ＋ 市场普遍现象。
    ///
    /// 事件是**现读库**的，抓取程序刚落库的新公告，下一次打开就看得到，不需要先"重算"。
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
}
