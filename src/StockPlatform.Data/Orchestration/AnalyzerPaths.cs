namespace StockPlatform.Data.Orchestration;

/// <summary>
/// Local folder/file layout for the analysis program, all rooted next to the executable.
///
/// 数据来源的演变（都失败了，最后回到最简单的方案）：网盘自动同步 → 做不通（见
/// doc/data-platform-design.md section 6.5.1）；改成手动把 Fetcher 的库拷过来 → 一次要搬 7GB；
/// 再改成 GitHub Releases 分发（全量基线 + 每日增量） → 库涨到 7.5GB 后压出来 1.8GB、贴着
/// GitHub 单资产 2GB 上限，传不上去也下不下来，2026-08-21 整套上传/下载已删除。
///
/// 现在：**分析程序直接只读 Fetcher 写的 current.sqlite**。两个 exe 就装在同一个目录里、
/// <see cref="BaseDir"/> 算出来是同一个 data 文件夹，所以这里的 <see cref="CurrentDb"/> 和
/// FetchPaths.CurrentDb 指的是同一个文件——不拷贝、不下载、不存在两份数据不一致。
/// 抓取时也能同时分析：库跑的是 WAL（见 SqliteSchema.EnsureSchema），读写不互斥。
/// </summary>
public class AnalyzerPaths
{
    public string BaseDir { get; }
    public string LocalDir => Path.Combine(BaseDir, "local");

    /// <summary>Fetcher 写的那个库，分析程序只读它（同一个文件，见类注释）。</summary>
    public string CurrentDb => Path.Combine(LocalDir, "current.sqlite");

    /// <summary>自选股列表——是分析程序自己的状态（用户手动挑选的、跨方法通用），不是从
    /// Fetcher 那边来的共享只读数据，所以特意放在 LocalDir 外面、跟 CurrentDb 分开，避免
    /// 以后被误当成"可以直接删了重新拷贝"的那类文件（见 JsonWatchlistStore）。</summary>
    public string WatchlistPath => Path.Combine(BaseDir, "watchlist.json");

    /// <summary>交易费率设置（佣金/过户费/印花税，用户在"主动仓"页填）——跟 <see cref="WatchlistPath"/>
    /// 同理：分析程序自己的本地状态，不是 Fetcher 的共享数据，放在 LocalDir 外面。</summary>
    public string TradeFeePath => Path.Combine(BaseDir, "trade-fees.json");

    /// <summary>仓位计算器的参数（可投资总资金、凯利折扣、单票上限等，用户在【仓位计算器】窗口填）——
    /// 同样是分析程序自己的本地状态，放在 LocalDir 外面。</summary>
    public string PositionSizingPath => Path.Combine(BaseDir, "position-sizing.json");

    /// <summary>底仓持仓记录（2026-08-20 新增，用户在【底仓】页填）——**故意跟
    /// <see cref="WatchlistPath"/> 分开成两个文件**，不是塞进 watchlist.json 加个标记。
    ///
    /// 三个理由：① 字段几乎不重叠——WatchlistEntry 一大半是信号来源字段（Method/Lookback/
    /// PriceAtPick/Criteria 快照），底仓不来自任何选股法，那些全是空值；底仓要的股息率、连续分红
    /// 年数、累计已收股息，WatchlistEntry 一个都没有。② 两个列表的标的本来就不该重叠——红利税按
    /// 先进先出认定，同一只票两头做会把底仓的免税时钟打乱（见 DividendMetrics）。③ 最关键：混一份
    /// 数据会污染晨检——晨检那页是短线纪律（跌破MA5就卖、20天时间止损、15%回撤止损），底仓绝对
    /// 不能进这套检查，共用一份 json 的话每处筛选都要多挂一个"排除底仓"条件，漏一处就会有一天
    /// 早上跳出来叫你把底仓砍掉。分开文件从物理上不会漏。</summary>
    public string CorePositionPath => Path.Combine(BaseDir, "core-positions.json");

    /// <summary>
    /// 个股分析笔记的目录（2026-08-27 新增），一票一个 <c>{code}.md</c>。
    ///
    /// 为什么要跟数据库分开存：**数据能重算，判断不能**。财务科目扩到 52 个之后，营运资金拆解、
    /// 净利率归因、收现比这些都能从 <c>FinancialReport</c> 直接算出来，不必留档；但"这次利润下滑
    /// 里哪部分可逆"、"负债率改善其实来自转债转股而非经营"、"下次要盯什么"这类结论是推理产物，
    /// 算不出来，只能写下来。所以库存数据、这里存判断。
    ///
    /// 用 markdown 纯文本而不是数据库表：笔记是给人读写的，要能直接拿编辑器打开、能进 git、
    /// 能贴表格和链接；而且几十只票几十个小文件，上 SQLite 没有任何好处。
    /// </summary>
    public string NotesDir => Path.Combine(BaseDir, "notes");

    /// <summary>
    /// 观察项目录（2026-09-11 新增，见 doc/watch-item-design.md §4.2）——
    /// <c>items.json</c> 存观察项定义，<c>hits-{yyyy}.json</c> 按年存触发记录。
    ///
    /// 为什么跟 <see cref="WatchlistPath"/>／<see cref="CorePositionPath"/> 并列而不是塞进去：
    /// 观察项是**跨两个列表**的（主动仓和底仓的票都会有观察项），塞进任一个都得给另一个再来一份。
    ///
    /// 为什么不上 SQLite：A 档触发一年也就几十条，量级跟 trade-fees.json 同级；
    /// 而 Analyzer 侧已有一整套 json store 的惯例，不值得为此引入第二个数据库文件。
    /// （观察项**依赖**的数据——PlanAnnouncement、StockWatchIndicator——在 current.sqlite 里，
    /// 那些是"关于标的的知识"；这里存的是"我要盯什么"，是关于我的。见设计文档 §2.3。）
    /// </summary>
    public string WatchDir => Path.Combine(BaseDir, "watch");

    /// <summary>观察项定义（L0/L1/L2 全在这一个文件里，靠 Origin 区分）。</summary>
    public string WatchItemsPath => Path.Combine(WatchDir, "items.json");

    /// <summary>触发记录，按年切——只增不删，它是"当时确实报过"的证据。</summary>
    public string WatchHitsPath(int year) => Path.Combine(WatchDir, $"hits-{year}.json");

    /// <summary>某只票的笔记文件路径。</summary>
    public string NotePath(string code) => Path.Combine(NotesDir, $"{code}.md");

    public AnalyzerPaths(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(LocalDir);
    }
}
