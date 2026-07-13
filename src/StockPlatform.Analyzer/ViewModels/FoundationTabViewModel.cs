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
/// 2026-07-10 规则整体换成"近N天内涨停 + 涨停后持续放量"，固定日线；两个可调参数：涨停回看天数 N
/// (1~15) 和 放量倍数 k。</summary>
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

    /// <summary>涨停回看窗口 N（最近多少个交易日内出现过涨停），界面可调，范围约1~15，默认15。
    /// 属性名沿用 Lookback，方便自选股沿用同一个字段存取（WatchlistEntry.Lookback）。</summary>
    private int _lookback = 15;
    public int Lookback { get => _lookback; set => Set(ref _lookback, value); }

    /// <summary>C2 放量倍数 k：涨停后平均量相对涨停前5日均量的倍数下限，默认1.5。</summary>
    private double _volumeMultiple = 1.5;
    public double VolumeMultiple { get => _volumeMultiple; set => Set(ref _volumeMultiple, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"峰哥法 — 入选条件（固定日线，2条必须全部满足）：\n\n" +
        $"C1. 近 N 个交易日内出现过涨停（N={Lookback}，可调1~15）\n" +
        "    最近N个交易日内(含今天)至少有一次收盘涨停，按板块/ST区分涨停幅度(主板10%/双创20%/北交所30%/ST5%，带0.3%容差)；取最近一次涨停日记为 L。\n\n" +
        $"C2. 涨停后持续放量（倍数 k={VolumeMultiple:F1}，可调）\n" +
        "    L 到今天的平均成交量 ≥ 涨停前5日均量 × k，且 L 之后没有任何一天缩回到涨停前5日均量以下（体现\"持续放量\"，不是放一天就歇）。\n" +
        "    若涨停就在今天(L=今天)，则退化为\"涨停当天本身放量 ≥ 前5日均量 × k\"。\n\n" +
        "结果列表只显示两条都满足的股票；历史数据不足无法计算的会被跳过，数量在分析完成后的日志里汇总。";

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
            double k = VolumeMultiple;
            int passedCount = 0, errorCount = 0;
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, names.GetValueOrDefault(code, code), n, k);
                    if (result.Error != null) { errorCount++; continue; }
                    if (!result.Passed) continue;

                    passedCount++;
                    App.Current.Dispatcher.Invoke(() => Results.Add(ResultRowViewModel.From(result)));
                }
            });
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passedCount} 只满足全部条件" +
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
