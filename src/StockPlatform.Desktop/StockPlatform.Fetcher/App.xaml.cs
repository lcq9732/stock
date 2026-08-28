using System.Windows;
// UseWindowsForms=true（为了托盘图标NotifyIcon，见MainWindow.xaml.cs）会让项目里同时能看到
// System.Windows.Forms.Application，跟这里要用的System.Windows.Application同名——显式取别名
// 消歧义，不然连这个partial class的基类都会报"ambiguous reference"。
using Application = System.Windows.Application;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Fetcher.ViewModels;
using StockPlatform.Logic.Abstractions;

namespace StockPlatform.Fetcher;

/// <summary>
/// Composition root — wires the data layer's concrete implementations together.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 只允许开一个实例（2026-08-04新增，见 SingleInstanceGuard 的类注释）——两个 Fetcher 同时抓取
        // 会往同一个 SQLite 写、互相锁表，历史上因为启动要等几十秒、用户重复双击真的开出过多个。
        if (!Desktop.Shared.SingleInstanceGuard.TryAcquire("Fetcher", "A股历史数据获取程序"))
        {
            Shutdown();
            return;
        }

        var paths = new FetchPaths();

        // Each source gets its own rate limiter — they're independent servers with independent
        // budgets. Each source also bundles the stock-list provider it should use for the
        // "获取全市场股票列表" step, so switching sources routes BOTH steps away from a vendor
        // that's blocked/rate-limited on this machine, not just bar fetching — see
        // doc/data-platform-design.md section 3.4. Tencent has no public full-list endpoint
        // (only per-code lookups), so its list step is paired with Sina instead — a different
        // domain from eastmoney.com either way, which is what actually matters here.
        // "Sina" 是2026-07-08新增的第三个源——跟Tencent一样配对SinaStockListProvider，但这次是
        // 同一个厂商自己的K线+列表，不用再跨厂商拼（见 SinaBarFetcher 类注释里未验证是否前复权的
        // 说明）。资金净流入/流通市值已经默认走新浪/腾讯。
        //
        // "Tencent" 这一项（同一天晚些时候）改成了 TencentThenSinaBarFetcher——用户反馈实际使用
        // 中新浪的抓取稳定性不如腾讯，所以腾讯仍是主力，只有某只股票腾讯拿不到时才回退到新浪重试
        // 这一只，不是两个平级选项。独立的"Sina"选项不受影响，仍然是纯新浪、无回退。
        var sources = new List<NamedBarSource>
        {
            new("EastMoney", new EastMoneyBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))), new EastMoneyStockListProvider()),
            new("Tencent", new TencentThenSinaBarFetcher(
                new TencentBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))),
                new SinaBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)))),
                new SinaStockListProvider()),
            new("Sina", new SinaBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))), new SinaStockListProvider()),
        };

        var manifestStore = new JsonManifestStore(paths.ManifestPath);
        var fundamentalRepository = new SqliteFundamentalMetricRepository(paths.CurrentDb);
        fundamentalRepository.EnsureSchema();
        // 流通市值现在"顺便"从新浪的股票列表扫描里拿（SinaListMarketCapFetcher，2026-07-08起
        // 默认），不再逐只股票单独发请求——新浪的股票列表接口本来就带流通市值字段（见
        // SinaStockListProvider/doc/data-platform-design.md 3.5节）。代价是"拉取当天"现在也会
        // 完整扫一遍全市场列表（只为了刷新市值，K线抓取本身还是只用本地已知的股票，不受影响）——
        // 用户已确认接受这个变慢（2026-07-08）。TencentMarketCapFetcher 保留在代码里但不再使用。
        var marketCapFetcher = new SinaListMarketCapFetcher();
        // Own rate limiter, separate from market cap/bar fetching above——资金净流入走的是新浪财经
        // 的资金流向接口（不是东方财富，见 SinaNetInflowFetcher 类注释：东方财富在实际使用环境里
        // 基本连不上，新浪/腾讯才是真正能用的），是完全独立的接口，各自独立限流。
        var netInflowFetcher = new SinaNetInflowFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));

        // Own rate limiters, separate from the bar-fetch sources above — cninfo and EastMoney's
        // announcement API are different endpoints from the bar/list APIs and shouldn't share a
        // budget with them. 现在整合进 FetchOrchestrator 内部自动跑（见
        // FetchOrchestrator.FetchAnnouncementsAsync），不再是 MainViewModel 自己触发的独立按钮/流程。
        var announcementSearchProvider = new CninfoAnnouncementSearchProvider(new RateLimiter(maxConcurrency: 2, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var announcementDetailFetcher = new EastMoneyAnnouncementDetailFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var announcementRepository = new SqliteAnnouncementRepository(paths.CurrentDb);
        var announcementOrchestrator = new AnnouncementFetchOrchestrator(announcementSearchProvider, announcementDetailFetcher, announcementRepository);

        // 板块（概念/题材 + 行业）数据走新浪，独立限流；单独的"拉取板块"按钮触发（见
        // FetchOrchestrator.RunFetchBoardsAsync），不掺进主抓取流程。
        var boardFetcher = new SinaBoardFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var boardRepository = new SqliteBoardRepository(paths.CurrentDb);
        boardRepository.EnsureSchema();

        // ETF 列表走新浪的 ETF 节点（node=etf_hq_fund），跟股票列表同一个接口不同 node；东财在用户
        // 环境不可用，所以固定用新浪（ETF 日K本身仍走上面所选数据源的 BarFetcher）。见 SinaEtfListProvider。
        var etfListProvider = new SinaEtfListProvider();

        // 指数成分名单(新浪 vII_NewestComponent)、成分权重(中证 closeweight.xls)、龙虎榜(新浪)——各自
        // 独立限流，独立按钮触发（见 FetchOrchestrator RunFetchIndexConsAsync / RunFetchLhbAsync），不掺
        // 进主抓取流程。中证权重源偏不稳、失败进 Manifest 可用"重新拉取失败股票"重试。三张表(IndexCons/
        // IndexWeight/Lhb/EtfIndexMap)都写进同一个 current.sqlite。
        var indexConsProvider = new SinaIndexConsProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var indexWeightProvider = new CsindexWeightProvider(new RateLimiter(maxConcurrency: 2, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var lhbProvider = new SinaLhbProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var indexRepository = new SqliteIndexRepository(paths.CurrentDb);
        indexRepository.EnsureSchema();
        var lhbRepository = new SqliteLhbRepository(paths.CurrentDb);
        lhbRepository.EnsureSchema();

        // 股东数据(股东户数+十大股东+十大流通股东)——新浪股本股东页,逐只抓,独立按钮。写 ShareholderCount/
        // TopShareholder 两张表。逐只失败进 Manifest 可重试。
        var shareholderProvider = new SinaShareholderProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var shareholderRepository = new SqliteShareholderRepository(paths.CurrentDb);
        shareholderRepository.EnsureSchema();

        // 融资余额(融资融券明细)——交易所官方源(上交所JSON+深交所xlsx)。每日数据,已并入"拉取全部/当天",
        // 另有"回补融资余额"按钮补历史。写 MarginDetail 表。
        var marginProvider = new ExchangeMarginProvider(new RateLimiter(maxConcurrency: 2, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var marginRepository = new SqliteMarginRepository(paths.CurrentDb);
        marginRepository.EnsureSchema();

        // 退市名单(沪深两所官网)——"拉取退市股"用，一次性快照、量小，不需要限流器。
        var delistedListProvider = new ExchangeDelistedListProvider();

        // 财务报表(新浪，三张表全历史)——基本面因子的数据基础，并入"一键拉取定期数据"，也有独立按钮。
        // 财务报表**单独用一套保守得多的限速**（2026-08-27）——不能跟其它抓取共用 3并发/1秒。
        // 原因：vDOWN 报表下载接口一次返回整张表（几十KB、几十个报告期），新浪对它的配额比行情
        // 查询严得多。实测 2026-08-27：按 3并发/1秒（实际约 1.1 请求/秒）跑到第 100 多个请求就被
        // 返回 HTTP 456（新浪的反爬码），整轮 350 个请求零成功、一条数据都没写进库；而同样的
        // 3并发/1秒 用在 K线/板块/股东/龙虎榜上天天全市场 5000+ 只都没事。
        // 单并发 + 4 秒间隔 + 每 30 个请求歇 60 秒 ≈ 10 请求/分钟，配合下面的每轮上限和
        // FinancialKeys.Version 的断点续传，分多天补齐。
        var financialProvider = new SinaFinancialProvider(new RateLimiter(
            maxConcurrency: 1,
            delayBetweenRequests: TimeSpan.FromSeconds(4),
            batchSize: 30,
            restDuration: TimeSpan.FromSeconds(60)));

        // 分红送配(新浪分红派息页 vISSUE_ShareBonus)——库里原本没有分红明细,做股息率因子/核对除权除息日的数据
        // 基础。逐只抓全历史,并入"一键拉取定期数据",也有独立按钮。写 Dividend 表。
        var dividendProvider = new SinaDividendProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var dividendRepository = new SqliteDividendRepository(paths.CurrentDb);
        dividendRepository.EnsureSchema();

        // 证监会行业分类(两所门类+新浪大类)——因子法显示"板块"、FactorLab 行业中性化都用它。
        var industryProvider = new ExchangeSinaIndustryProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));

        var orchestrator = new FetchOrchestrator(paths, manifestStore, fundamentalRepository, marketCapFetcher, netInflowFetcher, announcementOrchestrator, boardFetcher, boardRepository, indexConsProvider, indexWeightProvider, lhbProvider, indexRepository, lhbRepository, shareholderProvider, shareholderRepository, marginProvider, marginRepository, etfListProvider, delistedListProvider, financialProvider, dividendProvider, dividendRepository, industryProvider);

        var viewModel = new MainViewModel(paths, orchestrator, sources);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Desktop.Shared.SingleInstanceGuard.Release();
        base.OnExit(e);
    }
}
