using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using StockPlatform.Analyzer.Export;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"峰哥法"（代码内部沿用旧名 Foundation）tab — 见 doc/analysis-app-design.md 3.2.2。
/// 2026-07-13 规则简化成只按"近N个交易日内出现过涨停"筛选（固定日线，N 可调、默认7），结果按涨停
/// 次数从多到少排序。</summary>
public class FoundationTabViewModel : INotifyPropertyChanged
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
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    /// <summary>涨停回看窗口 N（最近多少个交易日内出现过涨停），界面可调，默认7。
    /// 属性名沿用 Lookback，方便自选股沿用同一个字段存取（WatchlistEntry.Lookback）。</summary>
    private int _lookback = 7;
    public int Lookback { get => _lookback; set => Set(ref _lookback, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"峰哥法 — 入选条件（固定日线）：\n\n" +
        $"近 N 个交易日内出现过涨停（N={Lookback}，可调，默认7）\n" +
        "    最近N个交易日内(含今天)只要至少有一次收盘涨停就入选，按板块/ST区分涨停幅度(主板10%/双创20%/北交所30%/ST5%，带0.3%容差)。\n\n" +
        "结果按\"涨停次数\"从多到少排序；点\"条件详情\"可在K线上看到涨停标在哪几天。历史数据不足无法计算的会被跳过，数量在分析完成后的日志里汇总。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public FoundationTabViewModel(AnalyzerPaths paths, IBarRepository barRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "峰哥法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "峰哥法", Granularity.Day, Lookback);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("峰哥法", Results));
    }

    private void Log(string message) => LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private async Task RunAnalyzeAsync()
    {
        IsBusy = true;
        Results.Clear();
        try
        {
            var codes = _barRepository.GetAllCodes();
            if (codes.Count == 0)
            {
                Log("本地没有任何数据，请把 Fetcher 产出的数据库拷贝到本地数据目录后点击\"刷新\"");
                return;
            }

            var names = SqliteStockMetaUpsert.GetAll(_paths.TotalDb).ToDictionary(s => s.Code, s => s.Name);
            var engine = new FoundationAnalysisEngine(_barRepository);
            int n = Lookback;
            int errorCount = 0;
            var passed = new List<StockScreenResult>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, names.GetValueOrDefault(code, code), n);
                    if (result.Error != null) { errorCount++; continue; }
                    if (!result.Passed) continue;
                    passed.Add(result);
                }
            });

            // 按涨停次数（SortScore）从多到少展示。
            foreach (var r in passed.OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(ResultRowViewModel.From(r));

            Log($"分析完成，共扫描 {codes.Count} 只股票，{passed.Count} 只近{n}个交易日内出现过涨停（已按涨停次数从多到少排序）" +
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足被跳过" : ""));
        }
        catch (Exception ex)
        {
            Log($"分析失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }
}
