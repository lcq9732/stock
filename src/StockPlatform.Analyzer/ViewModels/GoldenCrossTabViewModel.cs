using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"金叉法" tab — see doc/analysis-app-design.md section 3.2.2. Always daily bars, no
/// user-adjustable lookback (all 7 conditions use their own fixed internal windows).</summary>
public class GoldenCrossTabViewModel : INotifyPropertyChanged
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

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        "金叉法 — 入选条件（固定用日线；7条里至少满足5条即可入选，不要求全部满足）：\n\n" +
        "1. MA5 上穿 MA10\n    昨日 MA5≤MA10，今日 MA5>MA10\n\n" +
        "2. MA10 开始拐头向上\n    今日MA10>昨日MA10，且昨日MA10≤前日MA10\n\n" +
        "3. MACD 金叉\n    昨日 DIF≤DEA，今日 DIF>DEA\n\n" +
        "4. KDJ 在20~50区域金叉\n    昨日 K≤D，今日 K>D，且今日K落在[20,50]区间\n\n" +
        "5. RSI 从30附近向上突破50\n    近10日（不含今日）RSI最低值≤35，且昨日RSI<50、今日RSI≥50\n\n" +
        "6. 成交量≥5日均量的1.5倍\n    今日成交量 ≥ 昨日5日均量 × 1.5\n\n" +
        "7. 股价突破最近20日平台或压力位\n    今日收盘 > 前20日（不含今日）最高价\n\n" +
        "结果列表只显示满足数≥5的股票；因历史数据不足无法计算的会被跳过，数量会在分析完成后的日志里汇总。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }

    public GoldenCrossTabViewModel(AnalyzerPaths paths, IBarRepository barRepository)
    {
        _paths = paths;
        _barRepository = barRepository;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "金叉法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
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

            var engine = new GoldenCrossAnalysisEngine(_barRepository);
            int passedCount = 0, errorCount = 0;
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code);
                    result.Name = names.GetValueOrDefault(code, code);

                    if (result.Error != null) { errorCount++; continue; }
                    if (!result.Passed) continue; // results list only shows candidates with >=5/7 satisfied

                    passedCount++;
                    App.Current.Dispatcher.Invoke(() => Results.Add(ResultRowViewModel.From(result)));
                }
            });
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passedCount} 只满足≥5/7条件" +
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
