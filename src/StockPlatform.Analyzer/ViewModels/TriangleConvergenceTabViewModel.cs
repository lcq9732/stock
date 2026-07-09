using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"三角收敛" tab — detects a converging-triangle trendline shape (resistance down /
/// support up) plus a MACD-confirmed touch/breakout signal. Fixed to daily bars. Lookback（形态
/// 搜索窗口）和 SwingWindow（局部高低点判定窗口，每侧天数）都做成用户可调——见
/// TriangleConvergenceAnalysisEngine 的类注释，跟其它三个方法基本固定阈值的做法不同。</summary>
public class TriangleConvergenceTabViewModel : INotifyPropertyChanged
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

    // 形态搜索窗口：60天（约3个月），足够收集多个摆动点去拟合两条趋势线，又不会把太久远、已经
    // 跟当前走势脱节的高低点也算进来。
    private int _lookback = 60;
    public int Lookback { get => _lookback; set => Set(ref _lookback, value); }

    // 局部高低点判定窗口：每侧3天（共7天窗口内最高/最低才算一个摆动点）——常见的"分型"判定
    // 窗口，太小容易把噪音当成摆动点，太大会错过最近才形成的点。
    private int _swingWindow = 3;
    public int SwingWindow { get => _swingWindow; set => Set(ref _swingWindow, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"三角收敛 — 入选条件（3条必须全部满足，固定用日线，形态窗口N={Lookback}天，摆动点窗口±{SwingWindow}天）：\n\n" +
        "1. 三角收敛形态成立\n" +
        $"    在最近{Lookback}天内找出所有\"摆动高点\"（前后各{SwingWindow}天都不比它高）和" +
        $"\"摆动低点\"（前后各{SwingWindow}天都不比它低），分别对高点、低点做线性拟合：\n" +
        $"    压力线（连接摆动高点）斜率<0，支撑线（连接摆动低点）斜率>0，且两条线的拟合优度R²均≥{TriangleConvergenceAnalysisEngine.MinR2:F1}\n\n" +
        "2. 当前收盘价触及支撑线或突破压力线\n" +
        $"    收盘价超出压力线当前位置{TriangleConvergenceAnalysisEngine.BreakoutPct:F1}%以上视为突破；" +
        $"贴近支撑线当前位置{TriangleConvergenceAnalysisEngine.TouchTolerancePct:F1}%以内视为触线企稳\n\n" +
        "3. MACD配合确认\n" +
        "    触支撑线时：DIF上穿DEA，或MACD柱状图由缩短转为放大\n" +
        "    破压力线时：MACD柱状图转正，或DIF上穿DEA\n\n" +
        "结果列表只显示3条全部满足的股票；因历史数据不足或摆动点不够（无法拟合趋势线）而无法计算的会被跳过，数量会在分析完成后的日志里汇总。\n\n" +
        "注：目前只识别\"标准收敛三角形\"（压力线向下、支撑线向上），不识别单边走平的上升/下降三角形。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }

    public TriangleConvergenceTabViewModel(AnalyzerPaths paths, IBarRepository barRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "三角收敛 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "三角收敛", Granularity.Day, lookback: Lookback);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
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

            var engine = new TriangleConvergenceAnalysisEngine(_barRepository);
            int lookback = Lookback, swingWindow = SwingWindow;
            int passedCount = 0, errorCount = 0;
            var missingDataCounts = new Dictionary<string, int>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var name = names.GetValueOrDefault(code, code);
                    var result = engine.Analyze(code, name, lookback, swingWindow);

                    if (result.Error != null) { errorCount++; continue; }
                    foreach (var c in result.Criteria.Where(c => c.DataMissing))
                        missingDataCounts[c.Name] = missingDataCounts.GetValueOrDefault(c.Name) + 1;
                    if (!result.Passed) continue; // results list only shows candidates that satisfy all 3 rules

                    passedCount++;
                    App.Current.Dispatcher.Invoke(() => Results.Add(ResultRowViewModel.From(result)));
                }
            });
            var missingSummary = missingDataCounts.Count > 0
                ? "；" + string.Join("；", missingDataCounts.Select(kv => $"「{kv.Key}」这条条件因缺数据被跳过（不计入该条件，不代表股票被跳过，其余条件正常判断）：{kv.Value} 只涉及"))
                : "";
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passedCount} 只满足全部条件" +
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足或摆动点不够被跳过" : "") + missingSummary);
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
