using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using StockPlatform.Data.Orchestration;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// Root view model — holds what's shared across both analysis methods (the local data file's
/// location/freshness) and exposes each method's own tab view model. See
/// doc/analysis-app-design.md section 3.2 for why there are two methods (筑基法/金叉法) and why
/// they don't share analysis state beyond the underlying data file.
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

    private string _syncStatusText = "";
    public string SyncStatusText { get => _syncStatusText; set => Set(ref _syncStatusText, value); }

    /// <summary>Where the Analyzer expects the database — no netdisk sync (see
    /// doc/data-platform-design.md section 6.5.1, automating that turned out not to be workable),
    /// the user manually copies the Fetcher's output here with this exact name.</summary>
    public string LocalDbPathText { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public MainViewModel(AnalyzerPaths paths, IBarRepository barRepository)
    {
        _paths = paths;
        _barRepository = barRepository;

        FoundationTab = new FoundationTabViewModel(paths, barRepository);
        GoldenCrossTab = new GoldenCrossTabViewModel(paths, barRepository);

        LocalDbPathText = $"本地数据文件：{_paths.TotalDb}（需要手动把 Fetcher 产出的数据库拷贝到这里，用这个文件名）";

        RefreshCommand = new RelayCommand(_ => RefreshSyncStatus());
        OpenDataFolderCommand = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(_paths.LocalDir);
            Process.Start(new ProcessStartInfo(_paths.LocalDir) { UseShellExecute = true });
        });

        RefreshSyncStatus();
    }

    private void RefreshSyncStatus()
    {
        if (!File.Exists(_paths.TotalDb))
        {
            SyncStatusText = "本地还没有数据文件，请把 Fetcher 产出的数据库拷贝过来";
            return;
        }

        var latest = _barRepository.GetOverallLatestPeriodStart(Granularity.Day);
        SyncStatusText = latest == null
            ? "数据文件存在，但里面没有任何日线数据"
            : $"本地数据最新到 {latest:yyyy-MM-dd}";
    }
}
