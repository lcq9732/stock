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
/// 2026-09-07 规则整体换成"一根K线从最低到最高贯穿 MA5/MA10/MA20 + 三线粘合 + 处于低位"
/// （固定日线，三个参数都可调），结果按"低位分"从高到低排序。三个默认值的实测依据见
/// FoundationAnalysisEngine 的类注释——纯贯穿一条每日会命中 681 只且没有超额，粘合+低位才有。</summary>
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

    /// <summary>回看窗口 N（最近多少根K线里出现过贯穿三线），界面可调，**默认1 = 只看今天这根**
    /// ——用户的用法是"今天收盘之后、明天开盘之前把票找出来"。调大只是为了回看最近几天核对形态。
    /// 属性名沿用 Lookback，方便自选股沿用同一个字段存取（WatchlistEntry.Lookback）。</summary>
    private int _lookback = 1;
    public int Lookback { get => _lookback; set => Set(ref _lookback, value); }

    /// <summary>三线间距上限（%），界面可调，默认1.5。见 FoundationAnalysisEngine.DefaultMaxSpreadPct。</summary>
    private double _maxSpreadPct = FoundationAnalysisEngine.DefaultMaxSpreadPct;
    public double MaxSpreadPct { get => _maxSpreadPct; set => Set(ref _maxSpreadPct, value); }

    /// <summary>低位上限（%），界面可调，默认20。见 FoundationAnalysisEngine.DefaultMaxLowPositionPct。</summary>
    private double _maxLowPositionPct = FoundationAnalysisEngine.DefaultMaxLowPositionPct;
    public double MaxLowPositionPct { get => _maxLowPositionPct; set => Set(ref _maxLowPositionPct, value); }

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
            TextDetailWindow.Show("峰哥法 — 分析条件说明", "峰哥法 — 入选条件与依据说明", CriteriaInfoText));
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

            var names = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).ToDictionary(s => s.Code, s => s.Name);
            var engine = new FoundationAnalysisEngine(_barRepository);
            int n = Lookback;
            double spread = MaxSpreadPct, lowPos = MaxLowPositionPct;
            int errorCount = 0;
            var passed = new List<StockScreenResult>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, names.GetValueOrDefault(code, code), n, spread, lowPos);
                    if (result.Error != null) { errorCount++; continue; }
                    if (!result.Passed) continue;
                    passed.Add(result);
                }
            });

            // 按"低位分"（SortScore = 100 − 60日位置%）从高到低展示，越靠近60日底部越前面。
            foreach (var r in passed.OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(ResultRowViewModel.From(r));

            Log($"分析完成，共扫描 {codes.Count} 只股票，{passed.Count} 只满足" +
                (n <= 1 ? "最新交易日" : $"近{n}个交易日") +
                $"贯穿三线 + 三线间距≤{spread:F1}% + 低位≤{lowPos:F0}%（已按低位分从高到低排序）" +
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
