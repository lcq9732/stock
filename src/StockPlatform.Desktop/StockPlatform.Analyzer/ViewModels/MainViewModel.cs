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
/// analysis state beyond the underlying data file. 界面 Tab 顺序（也就是这里各 Tab 属性希望呈现
/// 的顺序）：每日晨检（早上第一眼看的仪表盘）/ 我的交易（晨检看完就在这里执行、录买卖）——这两个
/// 是日常动线，放最前；然后才是各选股方法 三角收敛 / 峰哥法 / 耀哥法 / 彬哥法 / 金叉法 / …，
/// 最后是跨方法的自选股（算法验证样本，只用来统计各方法准不准）。类名仍叫
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
    public string TotalDbPath => _paths.TotalDb;

    public MorningCheckTabViewModel MorningCheckTab { get; }
    /// <summary>"我的交易"——打算买卖、每天要盯的那一小撮票（晨检只体检这些）。跟 WatchlistTab
    /// 共用同一个 watchlist.json，靠 WatchlistEntry.IsInTradePool 区分，见 TradePoolTabViewModel。
    /// 界面上紧跟在"每日晨检"后面：晨检看结论 → 这里执行/录买卖，是日常动线。</summary>
    public TradePoolTabViewModel TradePoolTab { get; }
    public FoundationTabViewModel FoundationTab { get; }
    public GoldenCrossTabViewModel GoldenCrossTab { get; }
    /// <summary>"回调法"——按用户自述的买股原则（盈利好/不追高/到价卖）建的方法，
    /// 条件取舍全部有回测依据，见 PullbackAnalysisEngine 的注释。</summary>
    public PullbackTabViewModel PullbackTab { get; }
    public BottomReboundTabViewModel BottomReboundTab { get; }
    public MidCapPullbackTabViewModel MidCapPullbackTab { get; }
    public TriangleConvergenceTabViewModel TriangleConvergenceTab { get; }
    public RisingLowsTabViewModel RisingLowsTab { get; }
    public ShortTermTabViewModel ShortTermTab { get; }
    public QueryTabViewModel QueryTab { get; }
    public BoardTabViewModel BoardTab { get; }
    public FactorTabViewModel FactorTab { get; }
    public WatchlistTabViewModel WatchlistTab { get; }

    private string _dataStatusText = "";
    public string DataStatusText { get => _dataStatusText; set => Set(ref _dataStatusText, value); }

    /// <summary>Where the Analyzer expects the database — no netdisk sync (see
    /// doc/data-platform-design.md section 6.5.1, automating that turned out not to be workable),
    /// the user manually copies the Fetcher's output here with this exact name.</summary>
    public string LocalDbPathText { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand SyncCommand { get; }

    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; private set => Set(ref _isSyncing, value); }

    private string _syncStatusText = "";
    /// <summary>"从GitHub更新数据"的进度/结果单行提示（会被下载百分比等不断覆盖）。</summary>
    public string SyncStatusText { get => _syncStatusText; set => Set(ref _syncStatusText, value); }

    public MainViewModel(AnalyzerPaths paths, IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository, INetInflowRepository netInflowRepository, IBoardRepository boardRepository, IShareholderRepository shareholderRepository, IMarginRepository marginRepository, IFinancialRepository financialRepository, IDividendRepository dividendRepository)
    {
        _paths = paths;
        _barRepository = barRepository;

        var watchlistStore = new JsonWatchlistStore(paths.WatchlistPath);
        MorningCheckTab = new MorningCheckTabViewModel(barRepository, shareholderRepository, watchlistStore);
        FoundationTab = new FoundationTabViewModel(paths, barRepository, watchlistStore);
        GoldenCrossTab = new GoldenCrossTabViewModel(paths, barRepository, watchlistStore);
        PullbackTab = new PullbackTabViewModel(paths, barRepository, financialRepository, dividendRepository, watchlistStore);
        BottomReboundTab = new BottomReboundTabViewModel(paths, barRepository, netInflowRepository, watchlistStore);
        MidCapPullbackTab = new MidCapPullbackTabViewModel(paths, barRepository, fundamentalRepository, shareholderRepository, marginRepository, watchlistStore);
        TriangleConvergenceTab = new TriangleConvergenceTabViewModel(paths, barRepository, watchlistStore);
        RisingLowsTab = new RisingLowsTabViewModel(paths, barRepository, watchlistStore);
        ShortTermTab = new ShortTermTabViewModel(paths, barRepository, netInflowRepository, fundamentalRepository, watchlistStore);
        QueryTab = new QueryTabViewModel(paths, barRepository, watchlistStore);
        BoardTab = new BoardTabViewModel(boardRepository, barRepository, paths);
        FactorTab = new FactorTabViewModel(paths, watchlistStore);
        WatchlistTab = new WatchlistTabViewModel(watchlistStore, barRepository, boardRepository);
        TradePoolTab = new TradePoolTabViewModel(watchlistStore, barRepository, boardRepository);

        // 交易池成员一变（自选页"加入交易池"/交易池页"移出"/查询页直接加入），另外那页要跟着刷新——
        // 几个页读的是同一份 watchlist.json，不联动就会出现"加进去了但那边还没有"的错觉。
        // 故意不在这里连带刷新每日晨检：晨检读全库+逐只体检，2026-07-31 已按用户要求改成纯手动
        // （只有点它自己的"刷新"才算），这里自动触发会把那份"开程序秒开、切Tab不卡"的收益又赔进去。
        WatchlistTab.TradePoolChanged = () => TradePoolTab.Reload();
        TradePoolTab.TradePoolChanged = () => WatchlistTab.Reload();
        QueryTab.TradePoolChanged = () => { TradePoolTab.Reload(); WatchlistTab.Reload(); };

        LocalDbPathText = $"本地数据文件：{_paths.TotalDb}（需要手动把 Fetcher 产出的数据库拷贝到这里，用这个文件名）";

        RefreshCommand = new RelayCommand(_ => RefreshDataStatus());
        OpenDataFolderCommand = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(_paths.LocalDir);
            Process.Start(new ProcessStartInfo(_paths.LocalDir) { UseShellExecute = true });
        });
        SyncCommand = new RelayCommand(async _ => await RunSyncAsync(), _ => !IsSyncing);

        RefreshDataStatus();
    }

    private async Task RunSyncAsync()
    {
        IsSyncing = true;
        try
        {
            var svc = new DataSyncService(_paths);
            var progress = new Progress<string>(s => SyncStatusText = s);
            await svc.UpdateAsync(progress);
            RefreshDataStatus();
        }
        catch (Exception ex)
        {
            SyncStatusText = $"更新失败：{ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
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
        if (!File.Exists(_paths.TotalDb))
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
