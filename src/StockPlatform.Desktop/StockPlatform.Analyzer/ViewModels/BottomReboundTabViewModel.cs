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

/// <summary>"耀哥法"（原名触底回升法，类名沿用 BottomRebound）tab — see
/// doc/analysis-app-design.md section 3.2.3. Always daily bars, like 金叉法 (the 20-day bottom
/// window and MA20 only mean what's intended on a daily calendar).</summary>
public class BottomReboundTabViewModel : INotifyPropertyChanged
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
    private readonly INetInflowRepository _netInflowRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private double _difThreshold = 0;
    public double DifThreshold { get => _difThreshold; set => Set(ref _difThreshold, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"耀哥法 — 入选条件（5条必须全部满足，固定用日线，DIF阈值={DifThreshold:F3}）：\n\n" +
        "1. 日线MACD\n" +
        "    MACD采用默认参数(12,26,9)\n" +
        $"    DIF（快线）≥ -{DifThreshold:F3}（0表示DIF必须≥0；调大阈值允许DIF略低于0轴也算\"接近0轴\"）\n\n" +
        "2. 均线条件\n" +
        "    当前交易日收盘价同时高于MA5、MA10、MA20\n\n" +
        "3. 底部上升形态\n" +
        "    以最近20个交易日（含今天）为观察窗口\n" +
        "    窗口内最低价(Low)所在那天视为本轮底部\n" +
        "    该底部之后到今天之间，出现过至少一段≥3天连续收盘价一天比一天高（不要求紧贴底部）\n" +
        "    且当前股价已重新站上MA5、MA10、MA20\n\n" +
        "4. 最近三天资金净流入\n" +
        "    最近3个交易日（含今天）的资金净流入（新浪财经资金流向数据）都必须为正\n" +
        "    需要Fetcher先抓取过资金净流入历史数据，否则这条判不满足\n\n" +
        "5. 20天内MACD金叉\n" +
        "    以最近20个交易日（含今天）为观察窗口\n" +
        "    窗口内DIF（快线）只要出现过至少一次由下向上穿过DEA（慢线），即算满足\n\n" +
        "结果列表只显示5条全部满足的股票；因历史数据不足无法计算的会被跳过，数量会在分析完成后的日志里汇总。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public BottomReboundTabViewModel(AnalyzerPaths paths, IBarRepository barRepository, INetInflowRepository netInflowRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _netInflowRepository = netInflowRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "耀哥法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "耀哥法", Granularity.Day, difThreshold: DifThreshold);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("耀哥法", Results));
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

            var engine = new BottomReboundAnalysisEngine(_barRepository, _netInflowRepository);
            var threshold = DifThreshold;
            int passedCount = 0, errorCount = 0;
            var missingDataCounts = new Dictionary<string, int>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, threshold);
                    result.Name = names.GetValueOrDefault(code, code);

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
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足被跳过" : "") + missingSummary);
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
