using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// Root view model — holds what's shared across all analysis methods (the local data file's
/// location/freshness) and exposes each method's own tab view model. See
/// doc/analysis-app-design.md section 3.2 for why there are five methods and why they don't share
/// analysis state beyond the underlying data file.
///
/// 界面 Tab 顺序（2026-08-11 按用户要求调整，权威顺序看 MainWindow.xaml 里 TabItem 的排列，这里
/// 各属性的声明顺序尽量跟它保持一致，方便对照）：
///   主动仓 / 短线法 / 底仓 / 底仓法  ← 2026-08-20 按用户要求提到最前：两层仓位各自
///   紧跟自己的选股法（主动仓←短线法、底仓←底仓法），日常动线就是这四个来回切
///   然后是每日晨检 / 自选股 / 查询，再是其余选股方法与工具，保持原有相对顺序：
///   三角收敛 / 峰哥法 / 耀哥法 / 彬哥法 / 金叉法 / 阶梯低点法 / 因子法 / 板块热度。
/// 类名仍叫
/// TriangleConvergence/Foundation/BottomRebound/MidCapPullback/GoldenCross——描述的是算法本身，
/// 跟人名/Tab 中文名无关。
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly AnalyzerPaths _paths;
    private readonly IBarRepository _barRepository;

    public IBarRepository BarRepository => _barRepository;

    /// <summary>本地数据库文件的完整路径——"行情详情"里的"其他数据"要按表直接读十几张表
    /// （见 SqliteStockDossierReader），那些表在这里没有对应仓储被注入，给路径最直接。</summary>
    public string CurrentDbPath => _paths.CurrentDb;

    public MorningCheckTabViewModel MorningCheckTab { get; }
    /// <summary>"主动仓"——打算买卖、每天要盯的那一小撮票（晨检只体检这些）。跟 WatchlistTab
    /// 共用同一个 watchlist.json，靠 WatchlistEntry.IsInTradePool 区分，见 TradePoolTabViewModel。
    /// 界面上紧跟在"每日晨检"后面：晨检看结论 → 这里执行/录买卖，是日常动线。</summary>
    public TradePoolTabViewModel TradePoolTab { get; }
    public FoundationTabViewModel FoundationTab { get; }
    public GoldenCrossTabViewModel GoldenCrossTab { get; }
    /// <summary>"底仓法"（2026-08-20 由"回调法"改造而来）——筛选能长期拿着吃分红的股票。
    /// 原回调法里的主动仓那一路已交给 <see cref="ShortTermTab"/>。⚠ 这个方法没有回测支持，
    /// 条件是按"拿分红"的目的推的，理由见 CorePositionAnalysisEngine 的注释。</summary>
    public CorePositionScreenTabViewModel CorePositionScreenTab { get; }

    /// <summary>【底仓】页——记录"用来拿分红、基本不动"的那部分持仓。跟 <see cref="TradePoolTab"/>
    /// （主动仓、走短线纪律）是两套数据、两个文件，界面上也刻意用不同的列：那边看止亏价和±10%，
    /// 这边看股息率、连续分红、累计已收股息、免税到期日。**本页不进晨检**（2026-08-20 用户确认）。</summary>
    public CorePositionTabViewModel CorePositionTab { get; }
    public BottomReboundTabViewModel BottomReboundTab { get; }
    public MidCapPullbackTabViewModel MidCapPullbackTab { get; }
    public TriangleConvergenceTabViewModel TriangleConvergenceTab { get; }
    public RisingLowsTabViewModel RisingLowsTab { get; }
    public ShortTermTabViewModel ShortTermTab { get; }
    public QueryTabViewModel QueryTab { get; }
    public BoardTabViewModel BoardTab { get; }
    public FactorTabViewModel FactorTab { get; }
    public WatchlistTabViewModel WatchlistTab { get; }

    /// <summary>【观察项】页（2026-09-11）——L0/L1/L2 三层观察项的清单和触发，见 doc/watch-item-design.md。</summary>
    public WatchTabViewModel WatchTab { get; }

    /// <summary>观察项服务。各列表页的【观察项】按钮直接拿它开单票详情窗。</summary>
    public WatchService WatchService { get; }

    /// <summary>【仓位计算器】的账户级参数（可投资总资金/凯利折扣/单票上限）——窗口由 MainWindow
    /// 打开，参数在这里持有，两个入口共用同一份（见 PositionSizingWindow）。</summary>
    public Watchlist.PositionSizingStore SizingStore { get; }

    /// <summary>个股分析笔记（2026-08-27 新增）——**库里存数据、笔记存判断**，见
    /// AnalyzerPaths.NotesDir。入口在【行情详情】窗口顶部的"分析笔记"按钮。</summary>
    public Watchlist.StockNoteStore NoteStore { get; }

    /// <summary>财务数据仓储——【财务分析】窗口要读某只票的全部报告期全部科目
    /// （GetAllByCode），在这里暴露出来给 MainWindow 用。</summary>
    public IFinancialRepository FinancialRepository { get; }

    /// <summary>分红数据仓储——【财务分析】算股息率要用。</summary>
    public IDividendRepository DividendRepository { get; }

    private string _dataStatusText = "";
    public string DataStatusText { get => _dataStatusText; set => Set(ref _dataStatusText, value); }

    /// <summary>分析程序读的那个库在哪——就是 Fetcher 写的 current.sqlite，两个程序装在同一个
    /// 目录、共用同一个 data/local，所以不用拷贝也不用下载（2026-08-21 起；此前是从 GitHub
    /// Releases 下载分发，库涨到 7GB 后传不上去也下不下来，那套上传/下载已删除）。</summary>
    public string LocalDbPathText { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public MainViewModel(AnalyzerPaths paths, IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository, INetInflowRepository netInflowRepository, IBoardRepository boardRepository, IShareholderRepository shareholderRepository, IMarginRepository marginRepository, IFinancialRepository financialRepository, IDividendRepository dividendRepository, IIndexConsRepository indexConsRepository)
    {
        _paths = paths;
        _barRepository = barRepository;
        FinancialRepository = financialRepository;
        DividendRepository = dividendRepository;

        var watchlistStore = new JsonWatchlistStore(paths.WatchlistPath);
        // 交易费率（佣金/过户费/印花税）全程序一份，"主动仓"页可改——两个自选相关的页共用同一个实例，
        // 否则改完这页那页还按老费率算盈亏。
        var feeStore = new TradeFeeStore(paths.TradeFeePath);
        // 仓位计算器的账户级参数（可投资总资金/凯利折扣/单票上限）——跟费率同理，全程序一份，
        // 两个入口（"主动仓"页顶部按钮、每行的【仓位】按钮）打开的是同一份设置。
        SizingStore = new PositionSizingStore(paths.PositionSizingPath);
        NoteStore = new StockNoteStore(paths.NotesDir);
        MorningCheckTab = new MorningCheckTabViewModel(barRepository, shareholderRepository, watchlistStore, indexConsRepository);
        FoundationTab = new FoundationTabViewModel(paths, barRepository, watchlistStore);
        GoldenCrossTab = new GoldenCrossTabViewModel(paths, barRepository, watchlistStore);
        // 底仓记录存在自己的 core-positions.json 里（不是 watchlist.json 加标记），理由见
        // AnalyzerPaths.CorePositionPath——最关键的是晨检那套短线纪律绝不能误伤底仓。
        var corePositionStore = new JsonCorePositionStore(paths.CorePositionPath);
        CorePositionTab = new CorePositionTabViewModel(corePositionStore, barRepository, dividendRepository, feeStore);
        CorePositionScreenTab = new CorePositionScreenTabViewModel(paths, barRepository, financialRepository, dividendRepository, watchlistStore, corePositionStore);
        // 底仓法页"加入底仓"后，底仓页要跟着刷新（同主动仓那对页的联动）。
        CorePositionScreenTab.CorePositionsChanged = () => CorePositionTab.Reload();
        // 【观察项】2026-09-11，见 doc/watch-item-design.md M3。跨主动仓和底仓两个列表——
        // 所以它两个 store 都要，也因此它自己的状态放在 watch/ 目录而不是塞进任一个 json。
        // NoteStore 传进去是为了读「个人观点」——它写在 notes/{code}.md 里以"观点："开头的那行，
        // 不在观察项数据里（判断归笔记、数据归库，见 AnalyzerPaths.NotesDir）。
        // 服务单独留一份引用：各列表页的【观察项】按钮要用它开单票详情窗，
        // 那条路径不经过页签的 ViewModel。
        WatchService = new WatchService(paths, watchlistStore, corePositionStore);
        WatchTab = new WatchTabViewModel(WatchService, NoteStore);
        BottomReboundTab = new BottomReboundTabViewModel(paths, barRepository, netInflowRepository, watchlistStore);
        MidCapPullbackTab = new MidCapPullbackTabViewModel(paths, barRepository, fundamentalRepository, shareholderRepository, marginRepository, watchlistStore);
        TriangleConvergenceTab = new TriangleConvergenceTabViewModel(paths, barRepository, watchlistStore);
        RisingLowsTab = new RisingLowsTabViewModel(paths, barRepository, watchlistStore);
        ShortTermTab = new ShortTermTabViewModel(paths, barRepository, financialRepository, watchlistStore);
        QueryTab = new QueryTabViewModel(paths, barRepository, watchlistStore, corePositionStore);
        BoardTab = new BoardTabViewModel(boardRepository, barRepository, paths);
        FactorTab = new FactorTabViewModel(paths, watchlistStore);
        WatchlistTab = new WatchlistTabViewModel(watchlistStore, barRepository, boardRepository, feeStore);
        TradePoolTab = new TradePoolTabViewModel(watchlistStore, barRepository, boardRepository, feeStore);

        // 主动仓成员一变（自选页"加入主动仓"/主动仓页"移出"/查询页直接加入），另外那页要跟着刷新——
        // 几个页读的是同一份 watchlist.json，不联动就会出现"加进去了但那边还没有"的错觉。
        // 故意不在这里连带刷新每日晨检：晨检读全库+逐只体检，2026-07-31 已按用户要求改成纯手动
        // （只有点它自己的"刷新"才算），这里自动触发会把那份"开程序秒开、切Tab不卡"的收益又赔进去。
        WatchlistTab.TradePoolChanged = () => TradePoolTab.Reload();
        TradePoolTab.TradePoolChanged = () => WatchlistTab.Reload();
        QueryTab.TradePoolChanged = () => { TradePoolTab.Reload(); WatchlistTab.Reload(); };
        // 查询页也能直接加底仓（2026-09-01），加完让【底仓】页跟着刷新——同底仓法那页的联动。
        QueryTab.CorePositionsChanged = () => CorePositionTab.Reload();

        LocalDbPathText = $"本地数据文件：{_paths.CurrentDb}（Fetcher 直接写这个文件，分析程序只读它）";

        RefreshCommand = new RelayCommand(_ => RefreshDataStatus());
        OpenDataFolderCommand = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(_paths.LocalDir);
            Process.Start(new ProcessStartInfo(_paths.LocalDir) { UseShellExecute = true });
        });
        RefreshDataStatus();
    }

    /// <summary>
    /// 刷新"本地数据最新到 X"那行状态文字——**放到后台线程跑**（2026-08-04改）。
    ///
    /// 原因：这行字要执行 <c>SELECT MAX(period_start) FROM Bar WHERE granularity='day'</c>，Bar 表的
    /// 主键是 (code, granularity, period_start)，前导列不是 granularity，所以这个查询只能把整个主键
    /// 覆盖索引扫一遍。库已经涨到 7GB+，实测暖状态约 2.6 秒、冷启动（开机后首次读这个文件）几十秒。
    /// 以前它是在构造函数里同步跑的，于是窗口要等它结束才出现——用户以为没启动就重复双击，开出
    /// 好几个实例（这也是加 <see cref="Shared.SingleInstanceGuard"/> 的直接原因）。
    ///
    /// 只有这一行字是慢的，其它 Tab 的数据加载合计约 0.5 秒，所以**不需要锁住界面**：窗口立刻可用，
    /// 这行字先显示"正在统计…"、几秒后自己变成结果。没有做成"建索引"是因为给 7GB 的表新建索引本身
    /// 要跑几分钟、还会让每次写入变慢，代价比收益大（用户 2026-08-04 确认按"先出界面"的思路解决）。
    /// </summary>
    private void RefreshDataStatus()
    {
        if (!File.Exists(_paths.CurrentDb))
        {
            DataStatusText = "本地还没有数据文件，请把 Fetcher 产出的数据库拷贝过来";
            return;
        }

        DataStatusText = "正在统计本地数据范围…（库较大，首次约需数秒，界面可正常使用）";
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                var latest = _barRepository.GetOverallLatestPeriodStart(Granularity.Day);
                text = latest == null
                    ? "数据文件存在，但里面没有任何日线数据"
                    : $"本地数据最新到 {latest:yyyy-MM-dd}";
            }
            catch (Exception ex)
            {
                text = $"读取数据范围失败：{ex.Message}";
            }
            // DataStatusText 的 setter 会触发 PropertyChanged → WPF 绑定必须在 UI 线程上更新
            System.Windows.Application.Current?.Dispatcher.Invoke(() => DataStatusText = text);
        });
    }
}
