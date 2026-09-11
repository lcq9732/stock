using System.Windows;
// UseWindowsForms=true（为了托盘图标NotifyIcon，见MainWindow.xaml.cs）会让项目里同时能看到
// System.Windows.Forms.Application，跟这里要用的System.Windows.Application同名——显式取别名
// 消歧义，不然连这个partial class的基类都会报"ambiguous reference"。
using Application = System.Windows.Application;
using StockPlatform.Data.Local;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Fetcher.ViewModels;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;

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

        // 设置文件不存在/是空的就写一份**带注释的模板**（2026-09-05）——界面上没有这些开关，
        // 改配置就是改那个文件，可原来它光秃秃一片，人根本不知道有哪些旋钮可拧。
        // 已有内容一个字都不动。见 FetcherSettings 类注释。
        FetcherSettings.EnsureTemplate(paths.SettingsPath);

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
        // "Tencent" 曾经是 TencentThenSinaBarFetcher（腾讯拿不到就回退新浪重试这一只），
        // **2026-09-10 拆掉了回退**，改回纯腾讯。三个理由：
        //   ① 回退根本不是为"腾讯没有这只票"准备的——判据是 `catch (Exception)`，即请求失败。
        //      翻遍保留的日志，回退实际只触发过 1 次，原因是 "No such host is known"（DNS 抖动）。
        //   ② 而 RateLimiter 本来就已经重试 3 次（间隔 2 秒、10 秒）。三次都栽了还立刻换一家，
        //      多半也是本机网络的问题，换谁都一样。
        //   ③ 代价却很实在：新浪的成交量是**股**、腾讯主板是**手**，回退每触发一次就往那只票的
        //      历史里掺一段异口径的行，哪只票哪一段全凭当时的网络抖动（见 BarVolumeUnit）。
        // 现在三次都失败就让异常抛出去，ProcessOneStockAsync 记进失败名单，
        // 交给【重新拉取失败】重试——有名单、可追溯，比静默换源干净。
        //
        // 独立的 "Sina" 选项不受影响，仍是纯新浪（它的成交量换算在 SinaBarFetcher 里做）。
        var sources = new List<NamedBarSource>
        {
            new("EastMoney", new EastMoneyBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))), new EastMoneyStockListProvider()),
            new("Tencent", new TencentBarFetcher(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1))), new SinaStockListProvider()),
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
        // 建出来就登记给 OnExit 销毁——漏了这一句的话下面那段释放代码是空转，
        // msedgewebview2 子进程会残留（2026-09-05 发现：字段从来没被赋过值）。
        _browserChannelToDispose = browserChannel;

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
        // ── 成分股走哪条通道（2026-09-05）──
        // fetcher-settings.json 里的 "BoardMemberChannel"：
        //   · "browser"（默认）＝ WebView2 浏览器通道，稳妥，但**会弹图片验证码、要人守着**；
        //   · "http"           ＝ 纯 HttpClient，快、不弹验证。
        //
        // 为什么不直接把默认改成 http：2026-09-04 实测"HttpClient 第 7 个请求就被切"才有的
        // 浏览器通道；09-05 在同一台机器复测，普通 HttpClient 连发 110 个零失败——那个前提
        // 看着已经不成立了，**可那次是周六（非交易日）**，而验证码都是交易日撞上的。
        // 所以两条路并存、配置切换：交易日盘中跑一次 doc/push2-reachability-probe.ps1，
        // 全过就把这里的默认值改成 "http"，那 2500 个成分股请求从此不用人守着。
        // 构造抽进了 CreateBoardFetcher（2026-09-05）：【重新读取配置】按钮要在运行期照着
        // 新配置再造一个，两边必须是同一段代码，否则改了这里忘了那里，热重载出来的通道
        // 参数会跟启动时的悄悄不一样。
        IBoardFetcher boardFetcher = CreateBoardFetcher(paths, browserChannel);

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
        // 龙虎榜概要的数据源（2026-09-09 默认切到东财）。两套并存、配置切换，见
        // FetcherSettings.ReadLhbSource：东财给的上榜原因是交易所原文，跟龙虎榜席位表同源、
        // 能 join；新浪那份把原因归并成粗类，且对应值跟原因错配。新浪留着是出事时的退路。
        var lhbSource = FetcherSettings.ReadLhbSource(paths.SettingsPath);
        var emLhbProvider = new EastMoneyLhbProvider(emDataCenter);
        ILhbProvider lhbProvider = lhbSource == LhbSources.Sina
            ? new SinaLhbProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)))
            : emLhbProvider;
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
        var sinaFinancialProvider = new SinaFinancialProvider(new RateLimiter(
            maxConcurrency: 1,
            delayBetweenRequests: TimeSpan.FromSeconds(4),
            batchSize: 30,
            restDuration: TimeSpan.FromSeconds(60)));

        // 2026-09-10：财务报表可切东财（RPT_F10_FINANCE_*，固定英文列名，摆脱中文行名匹配）。
        // **默认仍是新浪**——字段映射逐值验过了，但 200 只的全量比对还没做，见
        // doc/financial-source-eastmoney-design.md §5.2。配 FinancialSource: "eastmoney" 可先试。
        //
        // 切到东财时走的是 FinancialSourceRouter：**保险那 5 家仍留在新浪**，因为东财整组不填
        // 保险的支出科目（赔付支出/退保金/保单红利/分保费用，三处全 null），而赔付率指标要用它。
        // datacenter 比新浪宽松得多（1 秒间隔连拉几百页无事），所以东财这条限流参数放宽。
        IFinancialProvider financialProvider =
            FetcherSettings.ReadFinancialSource(paths.SettingsPath) == "eastmoney"
                ? new FinancialSourceRouter(
                    new EastMoneyFinancialProvider(new RateLimiter(
                        maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(1))),
                    sinaFinancialProvider)
                : sinaFinancialProvider;

        // 分红送配(新浪分红派息页 vISSUE_ShareBonus)——库里原本没有分红明细,做股息率因子/核对除权除息日的数据
        // 基础。逐只抓全历史,并入"一键拉取定期数据",也有独立按钮。写 Dividend 表。
        var dividendProvider = new SinaDividendProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var dividendRepository = new SqliteDividendRepository(paths.CurrentDb);
        dividendRepository.EnsureSchema();

        // 证监会行业分类——因子法显示"板块"、FactorLab 行业中性化都用它。
        // 2026-09-10 默认改成东财（门类名+大类都由它给，门类字母仍来自两所）：大类覆盖
        // 3886 → 6006 只、门类叫法 32 种 → 19 个标准名，实测"库里有、东财无"为 0 只。
        // 配 IndustrySource: "sina" 可切回老路（两所门类 + 新浪大类），见 FetcherSettings。
        var industrySource = FetcherSettings.ReadIndustrySource(paths.SettingsPath);
        IIndustryProvider industryProvider = industrySource == IndustrySources.Sina
            ? new ExchangeSinaIndustryProvider(new RateLimiter(maxConcurrency: 3, delayBetweenRequests: TimeSpan.FromSeconds(1)))
            : new ExchangeEastMoneyIndustryProvider(new RateLimiter(maxConcurrency: 2, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var industryRepository = new SqliteIndustryRepository(paths.CurrentDb);
        industryRepository.EnsureSchema();

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

        // ════ 分档资金流：两条通道 ════
        // ① 全市场当日快照（2026-09-06，push2delay）——**日常增量全靠它**：约 60 个请求、
        //    一两分钟把当天全市场写全。排行接口 clist 本来在 push2 上，那个域名在这台机器上
        //    被网关拦着；挨个试镜像域名发现 push2delay 通且接口完整（延时行情，收盘后取到的
        //    就是当日终值）。跟 ② 的数据逐条比对过、零差异，见 provider 的类注释。
        // ② 逐股补历史（2026-09-03，push2his）——一只票一个请求、给最近 120 个交易日。
        //    快照只有当天，历史缺口只有它补得了，所以两条都留着。
        //
        // 限流参数为什么差这么多：
        //  · 快照 60 个请求就完事，1 秒间隔、每 30 个歇 20 秒，跑完约 1 分半。实测连发 60 页
        //    一次没被拒，这套参数是留了余量的。
        //  · 逐股那条是 2026-09-04 量出来的：push2his **连发 16~35 个就被切**，触发点是累计
        //    请求数不是速率，所以每 15 个主动歇 2 分钟。摊下来约 11 秒/只——补历史本来就是
        //    "空闲时慢慢补"的活，不赶时间。
        // 两条是不同域名、独立计数，谁被切都不影响另一条。
        var moneyFlowChannel = FetcherSettings.ReadMoneyFlowChannel(paths.SettingsPath);
        var moneyFlowProvider = moneyFlowChannel == "snapshot" ? null : new EastMoneyMoneyFlowProvider(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(5),
                            batchSize: 15, restDuration: TimeSpan.FromMinutes(2)));
        var moneyFlowSnapshotProvider = moneyFlowChannel == "perstock" ? null
            : new EastMoneyMoneyFlowSnapshotProvider(
                new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(1),
                                batchSize: 30, restDuration: TimeSpan.FromSeconds(20)),
                host: FetcherSettings.ReadMoneyFlowSnapshotHost(paths.SettingsPath),
                // 跟板块那边共用同一个开关：被网关按域名拦掉的时候，换块网卡出去就通了
                bindNetworkInterface: ReadSetting(paths.SettingsPath, "Push2NetworkInterface"));
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

        // 板块**名单**的主源（2026-09-05）：行情中心左侧菜单那份静态 JSON，一个请求拿全量。
        // 走普通 HttpClient——quote 域名在这台机器上是通的（被网关拦的只有 push2），
        // 所以这一项不再需要浏览器通道、也不会弹图片验证码。成分股仍旧走上面的 boardFetcher。
        // 不给它限流器：一轮就一个请求，没有需要节流的东西。
        var sideMenuBoardList = new EastMoneySideMenuBoardListProvider();

        // 板块的**父子关系**（2026-09-07）：东财终端落在本地的一份文件，不联网、不占配额。
        // 跟上面的 boardFetcher 通道选择无关——不管成分股走哪条路，层级树只有这一个来源。
        // 没装终端就是这一项没数据，Read() 自己会报一句，不影响板块名单本身。
        var boardHierarchy = new EastMoneyTerminalHierarchyProvider(
            FetcherSettings.ReadTerminalHierarchyFile(paths.SettingsPath));

        // 行业景气指标（2026-09-07）：周期品的价格和库存，日/周频，是**传统行业分析**那一路的输入。
        // 复用上面那个 emDataCenter 实例而不是新建——限流器要共享，否则两个东财任务各限各的、
        // 合起来照样能把源打爆。
        var indicatorProvider = new EastMoneyIndustryIndicatorProvider(emDataCenter);
        var indicatorRepository = new SqliteIndustryIndicatorRepository(paths.CurrentDb);
        indicatorRepository.EnsureSchema();

        // 前五大客户/供应商 + 公司档案（2026-09-07/09-08）：年报里的交易金额，
        // 供应商＝上游、客户＝下游；档案给它做对手方还原（年报写全称、本地只有简称）。
        // 三者复用同一个 emDataCenter —— 同一个源就必须是同一个限流器，
        // 各限各的合起来照样能把源打爆。
        var custSuppProvider = new EastMoneyCustomerSupplierProvider(emDataCenter);
        var custSuppRepository = new SqliteCustomerSupplierRepository(paths.CurrentDb);
        custSuppRepository.EnsureSchema();
        var companyProfileProvider = new EastMoneyCompanyProfileProvider(emDataCenter);
        var companyProfileRepository = new SqliteCompanyProfileRepository(paths.CurrentDb);
        companyProfileRepository.EnsureSchema();

        // ═══ 新式任务的注册表（2026-09-08 起）═══
        // 2026-09-08 定的规矩：**以后新加的任务都写成独立类**（继承 FetchTaskBase，放在
        // StockPlatform.Tasks），在这里注册一行就能用——不用再往 6,200 行的 orchestrator 里加
        // Run*Async、也不用再往 DispatchPlanActionAsync 那个 44 个 case 的 switch 里加分支
        // （那边只加了一条"registry 里有就走新路"的总分支）。老任务维持原样，理由见
        // doc/fetcher-task-refactor-design.md：迁移 6,200 行的风险不值得。
        var tradingDayRepository = new SqliteTradingDayRepository(paths.CurrentDb);
        tradingDayRepository.EnsureSchema();
        // 深交所官网，一个月一个请求。日常两个请求、首次 264 个，间隔给 1 秒足够温和。
        var tradingCalendarProvider = new SzseTradingCalendarProvider(
            new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(1)));
        var localTradingDays = new SqliteLocalTradingDaySource(new SqliteBarRepository(paths.CurrentDb));

        // 「确认这天就是没有」名单（2026-09-08）——龙虎榜/融资余额的逐日回补靠它跟交易日历一起
        // 把空请求砍掉。两张表都交给 orchestrator，老任务那边也能用上。
        var dailyNoDataRepository = new SqliteDailyFetchNoDataRepository(paths.CurrentDb);
        dailyNoDataRepository.EnsureSchema();

        var taskRegistry = new FetchTaskRegistry();
        taskRegistry.Register(FetchActionId.StepTradingCalendar,
            () => new TradingCalendarTask(tradingDayRepository, tradingCalendarProvider, localTradingDays));
        taskRegistry.Register(FetchActionId.StepCompanyProfile,
            () => new CompanyProfileTask(companyProfileRepository, companyProfileProvider));
        taskRegistry.Register(FetchActionId.StepCustomerSupplier,
            () => new CustomerSupplierTask(custSuppRepository, companyProfileRepository, custSuppProvider));
        taskRegistry.Register(FetchActionId.StepIndustryIndicator,
            () => new IndustryIndicatorTask(indicatorRepository, indicatorProvider));
        taskRegistry.Register(FetchActionId.StepFixVolumeUnit,
            () => new BarVolumeUnitFixTask(new SqliteBarVolumeUnitFixer(paths.CurrentDb)));
        // 【拉取行业分类】2026-09-10 从 orchestrator 迁过来（判据见
        // doc/full-audit-task-migration-design.md §0：迁移成本 + 维护成本，老方式耦合）。
        // 它只有一批（整表快照），MaxItems/Deadline 对它没意义，理由见 IndustryTask 类注释。
        taskRegistry.Register(FetchActionId.FetchIndustry,
            () => new IndustryTask(industryRepository, industryProvider));
        // 【拉取财务报表】2026-09-10 从 orchestrator 迁过来。一批＝一只票（抓 3 张报表后整只落库），
        // 所以框架的 MaxItems/Deadline 直接就是"本轮抓几只/到点收尾"，老那套自写的每轮 300 只上限删了。
        // ⚠ provider 跟 orchestrator 里那条【银行监管指标】前置补数路径**是同一个实例**，别各造一个。
        taskRegistry.Register(FetchActionId.FetchFinancials,
            () => new FinancialTask(paths, financialProvider));
        // 【全库数据体检】2026-09-09 从 orchestrator 迁过来（例外，判据见
        // doc/full-audit-task-migration-design.md §0）：它要长大，而且正需要框架的流式落账 +
        // 分批/截止——原来扫完才一次性 Save，三遍扫描半小时，中途停等于全白跑。
        taskRegistry.Register(FetchActionId.StepFullAudit,
            () => new FullAuditTask(paths.CurrentDb, manifestStore, dailyNoDataRepository));
        // 【重算回测序列】2026-09-10 从 orchestrator 迁过来：依赖只有一个 db 路径，
        // 不碰 manifest、不占数据源、调用点只有一个——老任务里最容易迁的一类。
        taskRegistry.Register(FetchActionId.RebuildAdjSeries,
            () => new AdjSeriesRebuildTask(paths.CurrentDb));
        // 【拉取分档资金流】2026-09-11 从 orchestrator 迁过来。迁的动因是它每天被静默看门狗
        // 掐一次：老实现每 100 只才报一句进度，而待办只剩 72 只时一句都报不出来（见任务类注释）。
        // 一批＝一只票，所以 MaxItems/Deadline 直接就是"补历史这一轮抓几只/到点收尾"；
        // 快照那一段整批落库、不占批额度。两个 provider 可能因配置只启用一条通道，故都可为 null。
        taskRegistry.Register(FetchActionId.FetchMoneyFlowDetail,
            () => new MoneyFlowDetailTask(paths, moneyFlowRepository, moneyFlowProvider,
                                          moneyFlowSnapshotProvider));

        var orchestrator = new FetchOrchestrator(paths, manifestStore, fundamentalRepository, marketCapFetcher, netInflowFetcher, announcementOrchestrator, boardFetcher, boardRepository, indexConsProvider, indexWeightProvider, lhbProvider, indexRepository, lhbRepository, shareholderProvider, shareholderRepository, marginProvider, marginRepository, etfListProvider, delistedListProvider, financialProvider, dividendProvider, dividendRepository, prebookProvider, forecastProvider, forecastRepository, lhbSeatProvider, lhbSeatRepository, moneyFlowProvider, moneyFlowRepository, marketEventProvider, marketEventRepository, boardMapProvider, boardMapRepository, sideMenuBoardList, moneyFlowSnapshotProvider, boardHierarchy, tradingDayRepository, dailyNoDataRepository);

        // 最后那个委托是给【重新读取配置】用的：按下时照当时的配置文件重造板块通道。
        // 传委托而不是把 App 的方法暴露出去，是为了让 MainViewModel 不用知道 browserChannel
        // 和限流参数这些装配细节——它只管"按现在的配置再给我一个"。
        var viewModel = new MainViewModel(paths, orchestrator, sources, browserChannel,
                                          () => CreateBoardFetcher(paths, browserChannel), taskRegistry);
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

        // 页面通道那个真浏览器同理——它是我们自己 Process.Start 起来的，
        // 不显式关掉就会留一个 Chrome 进程和被占着的 profile 目录。
        try { _pageScraper?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { }

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
    /// 照当前的 <c>fetcher-settings.json</c> 造一个板块成分股取数通道。
    ///
    /// 启动时装配走这里，运行期点【重新读取配置】也走这里（见
    /// <see cref="ViewModels.MainViewModel.ReloadConfig"/>）——**必须是同一段代码**，
    /// 不然两条路的限流参数会各改各的，热重载出来的通道跟启动时的悄悄不一样。
    ///
    /// 吃两个设置：
    ///   · <c>BoardMemberChannel</c>＝ <c>"http"</c>（默认）纯 HttpClient，快、不弹验证码；
    ///     <c>"browser"</c> 走 WebView2 浏览器通道，稳妥但**会弹图片验证码、要人守着**。
    ///     为什么两条路并存：2026-09-04 实测"HttpClient 第 7 个请求就被切"才做的浏览器通道；
    ///     09-05 同一台机器复测 HttpClient 连发 110 个零失败——可那天是周六，而验证码都是
    ///     交易日撞上的。所以交易日盘中先跑一次 doc/push2-reachability-probe.ps1 再决定。
    ///   · <c>Push2NetworkInterface</c>＝ 把请求钉在某块网卡上（公司网关按域名拦过 push2）。
    ///   · <c>BoardMemberHost</c>＝ 成分股打哪个域名，默认 pushguest（2026-09-05 换的，
    ///     就是板块页点翻页时真正打的那个）；出事能一行配置退回 push2。
    ///
    /// <paramref name="browser"/> 是**共享**的浏览器通道，重载时不重建：它拉着一串
    /// msedgewebview2 子进程和落盘的 Cookie，重建等于把攒下的"熟面孔"身份丢掉，
    /// 而这两个设置也压根不影响它。
    /// </summary>
    /// <summary>
    /// 页面通道用的浏览器（真 Chrome/Edge，CDP 驱动）——**全程只建一个**。
    ///
    /// 为什么要缓存：它拉着一个浏览器进程和落盘的 profile（Cookie、登录态都在里面）。
    /// 【重新读取配置】会重造 fetcher，但重造浏览器等于把攒下的"熟面孔"身份丢掉、
    /// 还会多出一个孤儿浏览器进程。
    /// </summary>
    private static ChromeCdpBoardPageScraper? _pageScraper;

    private static ChromeCdpBoardPageScraper PageScraper(FetchPaths paths) =>
        _pageScraper ??= new ChromeCdpBoardPageScraper(
            userDataDir: System.IO.Path.Combine(AppContext.BaseDirectory, "data", "local", "chrome-cdp"));

    private static IBoardFetcher CreateBoardFetcher(FetchPaths paths, Services.WebView2JsonFetcher browser)
    {
        var bindNic = ReadSetting(paths.SettingsPath, "Push2NetworkInterface");
        var boardChannel = FetcherSettings.ReadBoardChannel(paths.SettingsPath);
        var memberHost = FetcherSettings.ReadBoardMemberHost(paths.SettingsPath);

        // 让调度那边知道现在走的是哪条通道（2026-09-06）——terminal 时板块那两项只读盘、
        // 一个请求都不发，占用表就不该再把它们记成占着东财（见 FetchTaskCatalog.BoardChannel）。
        //
        // 写在这儿而不是 ReloadConfig 里，是因为**这里是唯一真的造出通道对象的地方**：
        // 启动装配和【重新读取配置】都走这一个方法，而"没换成"的那条路根本不会调它。
        // 于是这个值天然只会是**真正生效**的那个，不会出现"界面说不占源、实际还在打 push2"。
        StockPlatform.Scheduling.FetchTaskCatalog.BoardChannel = boardChannel;

        // 浏览器通道不重建（重建＝丢掉攒下的 Cookie 和"熟面孔"身份），但域名可能刚被改过，
        // 所以把新值塞进去——它拿这个决定预热探针探谁、seed 页回哪儿。
        // 漏了这一句的话，配置退回 push2 之后探针还在探 pushguest，而且不会报错。
        browser.MemberHost = memberHost ?? EastMoneyBoardFetcherBase.DefaultMemberHost;

        // ── 节奏按"人在网页上翻页"来（2026-09-05，跟着换 pushguest 一起加的）──
        // 两档而不是一档匀速：页与页之间快（RateLimiter 的间隔 ± 抖动），换板块时慢
        // （boardSwitchPause，实际 0.5~1.5 倍随机）。真人就是这样——同一个板块里连点几下
        // 下一页，换板块要回菜单重新点开、等首屏。原来从头到尾一个固定间隔，
        // 上千个请求排成完全等距的队列，恰恰是机器行为里最好认的特征。
        //
        // 账：1000 个板块、pz=100 之下平均一个板块一页出头 ≈ 1300 个请求，
        // 换板块 6 秒 × 1000 ≈ 1.7 小时，加上页间间隔和批次休息，一轮 3 小时上下——
        // 比浏览器通道原来的 4 小时还快些，而且不用人守着点验证码。
        const int BoardSwitchSeconds = 6;

        // ── page：操作页面取数（2026-09-05 起的默认）─────────────────────
        // 一个 URL 都不拼，全靠点表头/点页码，截页面自己发的请求。为什么，见
        // IBoardMemberPageScraper：同一时刻导航接口 URL 是 503、页面点页码是 200。
        //
        // 限流参数按"一次调用＝一个板块"来配（不是一个请求）：一个板块内部要开页面、
        // 点表头、翻 1~3 页，节奏归 scraper 管；这里管的是板块之间的间隔和熔断退避。
        // 每 30 个板块歇 60 秒；换板块本身还有 BoardSwitchSeconds 那一档。
        // 不做外层重试：scraper 内部已经对每一页退避重试过 2 次（5 秒、12 秒），
        // 外面再重试等于整个板块从开页面重来，白烧一轮配额。
        // ── terminal：读东财终端客户端下发到本地的文件（2026-09-06）────────
        // 一个请求都不发，一次读盘就是全量 1031 个板块 94,056 条，耗时以毫秒计，
        // 而且完全不受出口 IP 风控影响——前三条通道慢和被限流的根子都在这儿。
        //
        // 代价是换来一个运行时依赖：得有人定期开一次东方财富终端，文件才会刷新。
        // 这个依赖失效时没有任何征兆（文件还在、格式还对、只是停在几天前），所以
        // EastMoneyTerminalBoardFile 强制查文件时间，过期就整条通道不可用。
        //
        // 限流参数在这条通道上几乎没意义（成分股不走网络），只有板块列表那一两个请求
        // 会用到，给一组温和的值即可。板块之间也不歇——那一档是为了把节奏做得像真人翻页。
        if (boardChannel == "terminal")
            return new EastMoneyTerminalBoardFetcher(
                new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2),
                                batchSize: 50, restDuration: TimeSpan.FromSeconds(30),
                                jitter: 0.3, retryDelays: []),
                file: new EastMoneyTerminalBoardFile(
                    FetcherSettings.ReadTerminalBoardFile(paths.SettingsPath),
                    FetcherSettings.ReadTerminalMaxAge(paths.SettingsPath)),
                bindNetworkInterface: bindNic);

        if (boardChannel == "page")
            return new EastMoneyBoardPageFetcher(
                new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2),
                                batchSize: 30, restDuration: TimeSpan.FromSeconds(60),
                                jitter: 0.3, retryDelays: []),
                scraper: PageScraper(paths),
                bindNetworkInterface: bindNic,
                boardSwitchPause: TimeSpan.FromSeconds(BoardSwitchSeconds));

        return boardChannel == "http"
            // 纯 HttpClient：没有页面加载那 1~3 秒，也不用等验证脚本，所以能跑得比浏览器通道快。
            // 2 秒间隔比实测通过的 1.2 秒更保守——那次实测毕竟是非交易日。
            ? new EastMoneyBoardHttpFetcher(
                new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(2),
                                batchSize: 50, restDuration: TimeSpan.FromSeconds(60),
                                jitter: 0.3, retryDelays: []),
                bindNetworkInterface: bindNic,
                memberHost: memberHost,
                boardSwitchPause: TimeSpan.FromSeconds(BoardSwitchSeconds))
            : new EastMoneyBoardFetcher(
                new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.FromSeconds(4),
                                batchSize: 30, restDuration: TimeSpan.FromSeconds(60),
                                jitter: 0.3,
                                // ⚠ 这里**不重试**（2026-09-04 实测踩到）：浏览器通道内部已经自己重试
                                // 3 次了（5 秒、15 秒，为的是等验证脚本跑完）。外层再重试 3 次的话，
                                // 一个逻辑请求会变成 3×3＝9 个实际请求——被限流的时候等于火上浇油，
                                // 而且日志里看着像"试了很多次"，其实全是自己打自己。
                                retryDelays: []),
                bindNetworkInterface: bindNic,
                browser: browser,
                memberHost: memberHost,
                boardSwitchPause: TimeSpan.FromSeconds(BoardSwitchSeconds));
    }

    /// <summary>
    /// 读 data/fetcher-settings.json 里的一个字符串设置。读不到就返回 null——
    /// 配置文件坏了不该拦住程序启动。
    ///
    /// 实现挪进了 <see cref="Services.FetcherSettings"/>（2026-09-05）：那份文件现在是
    /// **带 // 注释的 JSONC**，得用允许注释的解析选项读，不然整份配置会被当成坏文件忽略掉。
    /// </summary>
    private static string? ReadSetting(string settingsPath, string key) =>
        FetcherSettings.ReadString(settingsPath, key);
}
