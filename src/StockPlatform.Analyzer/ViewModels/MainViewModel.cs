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
/// 的顺序）：三角收敛 / 峰哥法 / 耀哥法 / 彬哥法 / 金叉法，最后是跨方法的自选股。类名仍叫
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

    public FoundationTabViewModel FoundationTab { get; }
    public GoldenCrossTabViewModel GoldenCrossTab { get; }
    public BottomReboundTabViewModel BottomReboundTab { get; }
    public MidCapPullbackTabViewModel MidCapPullbackTab { get; }
    public TriangleConvergenceTabViewModel TriangleConvergenceTab { get; }
    public RisingLowsTabViewModel RisingLowsTab { get; }
    public ShortTermTabViewModel ShortTermTab { get; }
    public QueryTabViewModel QueryTab { get; }
    public BoardTabViewModel BoardTab { get; }
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

    public MainViewModel(AnalyzerPaths paths, IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository, INetInflowRepository netInflowRepository, IBoardRepository boardRepository)
    {
        _paths = paths;
        _barRepository = barRepository;

        var watchlistStore = new JsonWatchlistStore(paths.WatchlistPath);
        FoundationTab = new FoundationTabViewModel(paths, barRepository, watchlistStore);
        GoldenCrossTab = new GoldenCrossTabViewModel(paths, barRepository, watchlistStore);
        BottomReboundTab = new BottomReboundTabViewModel(paths, barRepository, netInflowRepository, watchlistStore);
        MidCapPullbackTab = new MidCapPullbackTabViewModel(paths, barRepository, fundamentalRepository, watchlistStore);
        TriangleConvergenceTab = new TriangleConvergenceTabViewModel(paths, barRepository, watchlistStore);
        RisingLowsTab = new RisingLowsTabViewModel(paths, barRepository, watchlistStore);
        ShortTermTab = new ShortTermTabViewModel(paths, barRepository, netInflowRepository, fundamentalRepository, watchlistStore);
        QueryTab = new QueryTabViewModel(paths, barRepository);
        BoardTab = new BoardTabViewModel(boardRepository, barRepository, paths);
        WatchlistTab = new WatchlistTabViewModel(watchlistStore, barRepository, boardRepository);

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

    private void RefreshDataStatus()
    {
        if (!File.Exists(_paths.TotalDb))
        {
            DataStatusText = "本地还没有数据文件，请把 Fetcher 产出的数据库拷贝过来";
            return;
        }

        var latest = _barRepository.GetOverallLatestPeriodStart(Granularity.Day);
        DataStatusText = latest == null
            ? "数据文件存在，但里面没有任何日线数据"
            : $"本地数据最新到 {latest:yyyy-MM-dd}";
    }
}
