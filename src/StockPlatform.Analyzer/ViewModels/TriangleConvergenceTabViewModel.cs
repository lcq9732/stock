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

/// <summary>"三角收敛" tab — detects a symmetric converging triangle (resistance sloping down through
/// the post-peak swing highs / support sloping up through the post-trough swing lows, anchored the
/// way a human draws them — see TriangleConvergenceDetector) plus a MACD-confirmed touch/breakout
/// signal. Fixed to daily bars. Lookback（形态搜索窗口，默认90）、SwingWindow（局部高低点判定窗口，
/// 每侧天数）、MinR2（压力线拟合优度下限）三个参数都做成用户可调——见
/// TriangleConvergenceAnalysisEngine 的类注释，跟其它方法基本固定阈值的做法不同。结果按"收敛质量"
/// 从高到低排序展示。</summary>
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

    // 形态搜索窗口：90天（约4个多月）。这个方法要锚定"阶段最高峰"和"阶段最低谷"来分段画压力/支撑
    // 线（见 TriangleConvergenceDetector），窗口必须够长、能一直回溯到那波大涨的起点低谷，否则回溯
    // 不到真正的低点，支撑线会算成向下、三角形就不成立了（参照的 000066/000977 两个案例，低谷距今
    // 都在 70~85 个交易日左右）。太短会漏掉形态，所以默认给到 90。
    private int _lookback = 90;
    public int Lookback { get => _lookback; set => Set(ref _lookback, value); }

    // 局部高低点判定窗口：每侧3天（共7天窗口内最高/最低才算一个摆动点）——常见的"分型"判定
    // 窗口，太小容易把噪音当成摆动点，太大会错过最近才形成的点。
    private int _swingWindow = 3;
    public int SwingWindow { get => _swingWindow; set => Set(ref _swingWindow, value); }

    // 趋势线拟合优度(R²)下限：真实行情的摆动高/低点常常不是完美共线，门槛定死在某个数字
    // 对所有股票都不一定合适，所以做成用户可调，而不是像其它三个方法那样固定阈值。只设下限
    // 不设上限——R²越高说明趋势线拟合越准，没有理由因为"太准"而排除。
    private double _minR2 = TriangleConvergenceAnalysisEngine.MinR2;
    public double MinR2 { get => _minR2; set => Set(ref _minR2, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"三角收敛 — 入选条件（3条必须全部满足，固定用日线，形态窗口N={Lookback}天，摆动点窗口±{SwingWindow}天）：\n\n" +
        "1. 三角收敛形态成立（对称收敛三角形，画法贴合人工连线）\n" +
        $"    在最近{Lookback}天内先定位\"阶段最高峰\"和\"阶段最低谷\"，然后：\n" +
        $"    压力线——只连\"峰之后\"逐个走低的摆动高点（前后各{SwingWindow}天都不比它高），要求向下（斜率<0）且拟合优度R²≥{MinR2:F2}；\n" +
        $"    支撑线——只连\"谷之后\"逐个抬高的摆动低点（前后各{SwingWindow}天都不比它低），要求向上（斜率>0）；\n" +
        $"    两条线向右收口、且到今天为止尚未交叉。（这样画能避开大涨阶段峰前那些一路走高的高点，否则压力线会被带平/带涨，就不是人眼看到的三角形了。R²只卡压力线那条决定性的下行线，支撑线是价格下沿包络、常被中途回踩带得拟合较松，只要求向上不卡R²。）\n\n" +
        "2. 当前收盘价触及支撑线或突破压力线\n" +
        $"    收盘价超出压力线当前位置{TriangleConvergenceAnalysisEngine.BreakoutPct:F1}%以上视为突破；" +
        $"贴近支撑线当前位置{TriangleConvergenceAnalysisEngine.TouchTolerancePct:F1}%以内视为触线企稳\n\n" +
        "3. MACD配合确认\n" +
        "    触支撑线时：DIF上穿DEA，或MACD柱状图由缩短转为放大\n" +
        "    破压力线时：MACD柱状图转正，或DIF上穿DEA\n\n" +
        "结果列表只显示3条全部满足的股票，并按\"收敛质量\"（0~100，越高形态越标准：一半看两线收窄程度、一半看价格有多少时间被夹在两线之间）从高到低排序——三角收敛是模糊形态匹配，命中的可能有几百只，重点看排在前面的即可。因历史数据不足或摆动点不够（无法拟合趋势线）而无法计算的会被跳过，数量会在分析完成后的日志里汇总。\n\n" +
        "注：R²下限越低、形态窗口越大，越容易把拟合不太整齐的形态也算作\"收敛\"，命中会更多；想要更精的结果就调高R²下限或缩短形态窗口，看排名靠前的几只。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

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
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("三角收敛", Results, includeScore: true));
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
            double minR2 = MinR2;
            int errorCount = 0;
            var missingDataCounts = new Dictionary<string, int>();
            // 收集所有入选结果，扫描完成后按"收敛质量"从高到低排序再一次性填进表格——三角收敛是
            // 模糊形态匹配，命中的可能有几百上千只，靠排序把最标准的排前面，用户重点看头部即可。
            var passed = new List<StockScreenResult>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var name = names.GetValueOrDefault(code, code);
                    var result = engine.Analyze(code, name, lookback, swingWindow, minR2);

                    if (result.Error != null) { errorCount++; continue; }
                    foreach (var c in result.Criteria.Where(c => c.DataMissing))
                        missingDataCounts[c.Name] = missingDataCounts.GetValueOrDefault(c.Name) + 1;
                    if (!result.Passed) continue; // results list only shows candidates that satisfy all 3 rules

                    passed.Add(result);
                }
            });

            foreach (var r in passed.OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(ResultRowViewModel.From(r));

            var missingSummary = missingDataCounts.Count > 0
                ? "；" + string.Join("；", missingDataCounts.Select(kv => $"「{kv.Key}」这条条件因缺数据被跳过（不计入该条件，不代表股票被跳过，其余条件正常判断）：{kv.Value} 只涉及"))
                : "";
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passed.Count} 只满足全部条件（已按收敛质量从高到低排序，越靠前形态越标准）" +
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
