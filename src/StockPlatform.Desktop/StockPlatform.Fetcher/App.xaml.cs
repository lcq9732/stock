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

        var orchestrator = new FetchOrchestrator(paths, manifestStore, fundamentalRepository, marketCapFetcher, netInflowFetcher, announcementOrchestrator, boardFetcher, boardRepository);

        var viewModel = new MainViewModel(paths, orchestrator, sources);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
