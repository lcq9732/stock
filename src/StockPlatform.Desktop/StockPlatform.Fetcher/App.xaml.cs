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

        // 界面上的意外别把整个程序带走——尤其是抓取跑了几个小时的时候，
        // 因为点错一个地方就退出、整轮进度作废，代价太大（2026-09-01 真的发生过：
        // 点 ⓘ 弹出的说明文字，事件处理器里抛了异常，程序直接没了）。
        // 抓取本身的失败有自己的重试/记名单机制，走不到这儿；能到这儿的基本都是 UI 层的意外。
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;           // 先保住进程，再告诉用户
            try
            {
                var log = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "local", "logs");
                System.IO.Directory.CreateDirectory(log);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(log, $"crash-{DateTime.Now:yyyy-MM-dd}.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] {args.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* 连日志都写不了就算了，别在异常处理里再抛 */ }

            System.Windows.MessageBox.Show(
                $"界面上出了个意外，已经拦下来了，程序继续运行：{Environment.NewLine}{Environment.NewLine}" +
                $"{args.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                "详情记在 data\\local\\logs\\crash-*.txt。正在进行的抓取不受影响。",
                "出了点小问题", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        };

        // 只允许开一个实例（2026-08-04新增，见 SingleInstanceGuard 的类注释）——两个 Fetcher 同时抓取
        // 会往同一个 SQLite 写、互相锁表，历史上因为启动要等几十秒、用户重复双击真的开出过多个。
        //
        // ⚠ Debug 构建不设这道限制（2026-09-04）：开发时得能让调试版跟正在跑的正式版并存，
        //    否则每验证一次改动都要先把正式版关掉——那正是抓取跑到一半最不该做的事。
        //    Debug 版走的是自己 bin 目录下的库，不会跟正式版抢同一个 SQLite。
#if !DEBUG
        if (!Desktop.Shared.SingleInstanceGuard.TryAcquire("Fetcher", "A股历史数据获取程序"))
        {
            Shutdown();
            return;
        }
#endif

        // 界面主题（2026-08-29 新增）——必须在任何窗口创建之前应用，否则窗口先按默认浅色画一遍
        // 再跳成深色，启动时会闪一下白。见 ThemeManager 类注释。
        Desktop.Shared.Theme.ThemeManager.Initialize();

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

        // 财报预约披露日（2026-09-01）。翻页是串行的（要先拿到 totalPages），所以并发给 1 就够，
        // 真正决定耗时的是间隔：某一期首抓要翻 555 页，1 秒间隔要 16 分钟、两期半小时，太久。
        // 实测这个接口 0.15 秒间隔连抓 60 页没被拒，取 0.35 秒留足余量 → 一期约 4 分钟。
        var prebookProvider = new CninfoPrebookProvider(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromMilliseconds(350)));

        // 板块（概念/题材 + 行业）——2026-09-03 从新浪整体换成东财，**不保留新浪回退**。
        //
        // 换的原因是新浪的概念分类严重老化：175 个概念板块里没有存储芯片/算力/液冷/AI芯片/CPO/
        // 先进封装/人形机器人，占着位置的却是"融资融券""社保重仓""成渝特区"这类根本不是产业链的东西。
        // 东财实测 1031 个板块、5656 只股票、93867 条归属关系，上述主题一个不缺。
        //
        // 为什么不留新浪当备胎：板块是**快照**，抓取失败时库里上一次的数据还在，本来就不会"没数据"
        // （SqliteBoardRepository.ReplaceAll 对空集合是空操作）。所以回退实际会做的事，是拿 224 个
        // 没有存储/算力/液冷的老板块去覆盖 1031 个好板块——那是负价值。何况成分股走的是 datacenter，
        // 真到它挂了的时候，业绩预告/龙虎榜/大宗交易全都没有回退源，单给板块留备胎也不自洽。
        //
        // 成分股走 push2 的**官方成分名单**，不能用 datacenter 的 F10 报表替代——
        // 实测 F10 会系统性漏股（液冷服务器 170 只漏 4 只，含美的集团、拓普集团这种链上有实际
        // 业务的大票；PCB 漏 2 只；两次都是 F10 ⊂ 官方名单、多出 0），而且漏了不报错，会一路
        // 带进板块营收中位数这类指标。详见 EastMoneyBoardFetcher 类注释。
        //
        // 代价是 1031 个板块 ≈ 2500 个请求，而 push2 限流极敏感。所以限流器给到 2 秒间隔、
        // 单并发，并且整个抓取设计成**跑不完也没关系**：逐板块落库记进度，下一轮跳过已成功的，
        // 连续失败 10 个就判定被限流、提前收尾（见 FetchBoardsCoreAsync）。
        //
        // 2026-09-04：push2 在本机有线上被网关按域名拦了——TCP 和 TLS 都通，一发 HTTP 请求就被
        // 切断（0 字节），而同为东财的 datacenter 一直正常，所以不是东财在限流，是本地网络。
        // 换一条没限制的链路（另一个 Wi-Fi、或手机热点）就能通，实测两条链路的出口公网 IP 确实
        // 不同。配置 data/fetcher-settings.json 里的 "Push2NetworkInterface": "Wi-Fi" 就让 push2
        // 的请求从那块网卡出去，其余任务照旧走默认路由（有线更快更稳）。留空＝保持原样。
        //
        // 2026-09-04：push2 的请求改从**浏览器内核**发出去（WebView2＝Edge/Chromium）。
        // 实测同一 IP、同一接口、相近时间：真浏览器 18/20 成功、27 个/分钟，
        // 而 HttpClient 第 7 个请求就被切、折算 4.8 个/分钟。请求头、Cookie、连接复用、HTTP/2
        // 全都单独排除过，差别在 TLS 指纹——Chrome 的 ClientHello 跟 .NET 的 Schannel 不同，
        // 而 .NET 改不了这个。取数方式是直接把 WebView2 导航到接口地址、读页面上的文本，
        // 跟人在地址栏敲那个 URL 完全一样；要验证的话程序自己等 JS 挑战跑完再重发，不用人点。
        // WebView2 用不了（没装运行时、被组策略拦）会自动退回 HttpClient，不影响能不能抓。
        // Cookie 存 data/local/webview2，跨次启动留着，省得每次开程序都被当成生面孔。
        var browserChannel = new Services.WebView2JsonFetcher(
            System.IO.Path.Combine(AppContext.BaseDirectory, "data", "local", "webview2"));

        // 限流参数按 2026-09-04 的实测日志定（跟"能不能访问"是两回事——那个由本地网关决定，
        // 这里说的是访问得到之后东财自己的限流）：
        //   · 连发 16~35 个请求就被切断。用库里 fetched_at 反推出三串成功：
        //     25只/61秒、16只/31秒、33只/97秒；间隔 2.1 秒和 3.0 秒撑的个数差不多，
        //     所以**触发点是累计请求数，不是速率**——光降速没用，得在被切之前主动歇。
        //   · 频率按**人翻页的节奏**来（2026-09-04 改）：4 秒一个、上下抖 ±30%（2.8~5.2 秒），
        //     每 30 个歇 60 秒。摊下来 6 秒/请求＝10 个/分钟。
        //     依据是浏览器实测能持续到 27 个/分钟（1.5 秒间隔 20 个，成功 18 个），
        //     10 个/分钟只有它的三分之一，留足余量；同时又比原来给 HttpClient 定的
        //     "5 秒 + 每 15 个歇 2 分钟"（4.6 个/分钟）快一倍——那套是为成功率不到 20% 的
        //     通道调的，浏览器通道用不着那么憋。
        //   · **抖动比固定间隔重要**：人不会精确每 5.000 秒点一次，固定节奏是机器行为里最好认的。
        //     既然整条通道都在模仿浏览器，节奏也该像人。
        //   · 导航取数本身还要等页面加载（1~3 秒），所以实际间隔比设定值更宽。
        //
        // 1350 个成分股请求约 2.5 小时。板块是逐个落库的，跨几轮抓完没关系。
        var boardFetcher = new EastMoneyBoardFetcher(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(4),
                            batchSize: 30, restDuration: TimeSpan.FromSeconds(60),
                            jitter: 0.3,
                            // ⚠ 这里**不重试**（2026-09-04 实测踩到）：浏览器通道内部已经自己重试
                            // 3 次了（5 秒、15 秒，为的是等验证脚本跑完）。外层再重试 3 次的话，
                            // 一个逻辑请求会变成 3×3＝9 个实际请求——被限流的时候等于火上浇油，
                            // 而且日志里看着像"试了很多次"，其实全是自己打自己。
                            retryDelays: []),
            bindNetworkInterface: ReadSetting(paths.SettingsPath, "Push2NetworkInterface"),
            browser: browserChannel);

        // datacenter 客户端给业绩预告/龙虎榜席位等报表用（跟 push2 是不同域名、独立限流）
        var emDataCenter = new EastMoneyDataCenterClient(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(1)));
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
        // 中证 OSS 很容易触发反爬（2026-09-02 用户反馈）：原来是 2 并发 + 1 秒 ≈ 2 请求/秒，
        // 而这一步一轮要问几百个指数、其中大半是注定 404 的非中证系。降到单并发 + 2 秒 +
        // 每 30 个歇 60 秒 ≈ 0.4 请求/秒；配合 RunStepIndexWeightOnlyAsync 里那两道筛子
        // （本地已是最新一期的、确认没有文件的都不问），稳态下每轮实发请求接近 0。
        // 权重是月度数据，慢一点完全无所谓——撞醒反爬要停一整天才是真损失。
        var indexWeightProvider = new CsindexWeightProvider(new RateLimiter(
            maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2),
            batchSize: 30, restDuration: TimeSpan.FromSeconds(60)));
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

        // 业绩预告/快报（2026-09-03，东财）——本地此前完全没有这两份数据，且没有回退源：
        // 新浪/腾讯/交易所都不提供结构化预告，巨潮只有公告原文。复用上面那个 datacenter 客户端
        // （跟板块共享同一套 1 秒间隔的限流器，这是同一个域名的同一份配额，分开配反而会打架）。
        var forecastProvider = new EastMoneyEarningsForecastProvider(emDataCenter);
        var forecastRepository = new SqliteEarningsForecastRepository(paths.CurrentDb);
        forecastRepository.EnsureSchema();

        // 龙虎榜营业部席位明细（2026-09-03，东财）——跟已有的【龙虎榜】(新浪)是不同粒度、不替换它：
        // 那张表没有买卖前五营业部名单，而龙虎榜真正的信息量就在"是谁在买"。
        // 264 万行，走流式回调落库（见 RunFetchLhbSeatAsync），共用同一个 datacenter 客户端。
        var lhbSeatProvider = new EastMoneyLhbSeatProvider(emDataCenter);
        var lhbSeatRepository = new SqliteLhbSeatRepository(paths.CurrentDb);
        lhbSeatRepository.EnsureSchema();

        // 分档资金流（2026-09-03，东财 push2his）——跟已有的【资金净流入】(新浪)是同一件事的
        // 不同精度：那张表每行只有主力净额合计，这里拆成超大/大/中/小单的净额+净占比。
        // push2his 跟 push2 是不同域名、独立限流，且不需要人工验证；但全市场 5500+ 个请求。
        //
        // 限流参数跟板块那边同一套依据（2026-09-04 实测，见上面 boardFetcher 的注释）：
        // 那三串成功记录（25只/61秒、16只/31秒、33只/97秒）量的就是这个接口——**连发 16~35 个
        // 就被切**，触发点是累计请求数不是速率。所以每 15 个主动歇 2 分钟，别撞到被切。
        // 摊下来约 11 秒/只，全市场 5500 只要跨很多轮才抓得完；但它是"空闲时补"的定期项，
        // 且按"这只票今天抓过没有"断点续传，慢慢攒就行——总比现在每轮 0~33 只然后被封强。
        // ⚠ 分档资金流**暂时不接浏览器通道**（2026-09-04）：实测 push2his 在东财页面上下文里
        //    同样能通（贵州茅台 120 条一次拿全），改法跟板块一模一样——但先等板块那条线在真实
        //    环境里跑顺了再说，别两处一起动、出问题分不清是谁的锅。
        var moneyFlowProvider = new EastMoneyMoneyFlowProvider(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(5),
                            batchSize: 15, restDuration: TimeSpan.FromMinutes(2)));
        var moneyFlowRepository = new SqliteNetInflowDetailRepository(paths.CurrentDb);
        moneyFlowRepository.EnsureSchema();

        // 市场事件四项（2026-09-03，东财）：大宗交易/机构调研/限售解禁/股东增减持，本地此前全没有。
        // 共用 datacenter 客户端和它的配额——它们跟业绩预告、龙虎榜席位打的是同一个域名，
        // 各配一个限流器只会互相打架（见 QuotaGroup 的注释：限流器独立 ≠ 配额独立）。
        var marketEventProvider = new EastMoneyMarketEventProvider(emDataCenter);
        var marketEventRepository = new SqliteMarketEventRepository(paths.CurrentDb);
        marketEventRepository.EnsureSchema();

        // 个股行业/题材归属（2026-09-03）：补证监会分类的粒度不足，两份并存不替换。
        var boardMapProvider = new EastMoneyStockBoardMapProvider(emDataCenter);
        var boardMapRepository = new SqliteStockBoardMapRepository(paths.CurrentDb);
        boardMapRepository.EnsureSchema();

        var orchestrator = new FetchOrchestrator(paths, manifestStore, fundamentalRepository, marketCapFetcher, netInflowFetcher, announcementOrchestrator, boardFetcher, boardRepository, indexConsProvider, indexWeightProvider, lhbProvider, indexRepository, lhbRepository, shareholderProvider, shareholderRepository, marginProvider, marginRepository, etfListProvider, delistedListProvider, financialProvider, dividendProvider, dividendRepository, industryProvider, prebookProvider, forecastProvider, forecastRepository, lhbSeatProvider, lhbSeatRepository, moneyFlowProvider, moneyFlowRepository, marketEventProvider, marketEventRepository, boardMapProvider, boardMapRepository);

        var viewModel = new MainViewModel(paths, orchestrator, sources, browserChannel);
        var window = new MainWindow { DataContext = viewModel };
        // 显式认定主窗口：ShutdownMode=OnMainWindowClose 全靠它认对是哪一个。
        // 不设的话 WPF 会拿"第一个 Show 出来的窗口"当主窗口——现在还轮得到它，
        // 但将来谁在它之前 Show 了别的窗口（比如浏览器通道提前初始化），退出逻辑就悄悄错位了。
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 退出时要销毁的东西（2026-09-04 随浏览器通道加的）。
    /// 不销毁的话 msedgewebview2 子进程会一直留着——实测残留过一批。
    /// </summary>
    private static IAsyncDisposable? _browserChannelToDispose;

    protected override void OnExit(ExitEventArgs e)
    {
        // ⚠ 浏览器通道一定要显式销毁：它内部那个隐藏窗口 + 一串 msedgewebview2 子进程
        //    不会自己走。2026-09-04 踩过——主窗口关了进程还赖着，发布时提示"文件被占用"。
        try { _browserChannelToDispose?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { /* 关不掉也别拦着退出 */ }

        Desktop.Shared.SingleInstanceGuard.Release();
        base.OnExit(e);

        // 兜底硬退（2026-09-04）。走到这一步说明用户已经在关闭确认框上点过"确定"，
        // 关闭这件事已经定了；剩下的只是别让进程赖在那儿。
        //
        // 为什么要这一手：WebView2 会拉起一串 msedgewebview2 子进程，还往进程里塞了
        // 非托管资源和它自己的消息循环；只要有一个非后台线程没退，整个进程就卡住不走。
        // 用户看到的后果很实在——任务管理器里还挂着，发布时报"文件被占用"，只能去强杀。
        // 已经落库的数据不受影响（SQLite 是同步事务，走到这儿早提交完了）。
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>
    /// 读 data/fetcher-settings.json 里的一个字符串设置。读不到就返回 null——
    /// 配置文件坏了不该拦住程序启动（跟 MainViewModel.ResolveBarSource 一个路子）。
    /// </summary>
    private static string? ReadSetting(string settingsPath, string key)
    {
        try
        {
            if (!System.IO.File.Exists(settingsPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(settingsPath));
            return doc.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }
}
