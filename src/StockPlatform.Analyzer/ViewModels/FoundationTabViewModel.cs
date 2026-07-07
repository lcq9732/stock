using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

public class GranularityOption
{
    public string Display { get; init; } = "";
    public string Value { get; init; } = "";
    public override string ToString() => Display;
}

/// <summary>"筑基法" tab — see doc/analysis-app-design.md section 3.2.1.</summary>
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

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    public List<GranularityOption> TopLevelGranularities { get; } = new()
    {
        new() { Display = "分时", Value = "min" },
        new() { Display = "日", Value = Granularity.Day },
        new() { Display = "周", Value = Granularity.Week },
        new() { Display = "月", Value = Granularity.Month },
    };

    public List<GranularityOption> MinutePeriods { get; } = new()
    {
        new() { Display = "1分钟", Value = Granularity.Min1 },
        new() { Display = "5分钟", Value = Granularity.Min5 },
        new() { Display = "15分钟", Value = Granularity.Min15 },
        new() { Display = "30分钟", Value = Granularity.Min30 },
        new() { Display = "60分钟", Value = Granularity.Min60 },
    };

    private GranularityOption _selectedTopLevel;
    public GranularityOption SelectedTopLevel
    {
        get => _selectedTopLevel;
        set { Set(ref _selectedTopLevel, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMinuteGranularity))); }
    }

    public bool IsMinuteGranularity => SelectedTopLevel.Value == "min";

    private GranularityOption _selectedMinutePeriod;
    public GranularityOption SelectedMinutePeriod { get => _selectedMinutePeriod; set => Set(ref _selectedMinutePeriod, value); }

    // 60 matches FoundationBreakoutDetector.MaxSearchWindow — anything smaller leaves the
    // four-stage pattern (最多5+3+5根本身 + 阶段间的过渡K线) too little room to ever be found;
    // verified against real data: N=20 found ~0 matches across 5202 stocks, N=60 found 107.
    private int _lookback = 60;
    public int Lookback { get => _lookback; set => Set(ref _lookback, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    /// <summary>Rule text shown via the "ⓘ" info button next to "开始分析" (see
    /// doc/analysis-app-design.md section 3.2.1) so the criteria are always one click away
    /// instead of only living in the design doc.</summary>
    public string CriteriaInfoText =>
        $"筑基法 — 入选条件（3条必须全部满足，N={Lookback}，基于当前选定的粒度）：\n\n" +
        "1. 低位盘整-高位滞涨-破位新低-再破高点（四段式形态）\n" +
        $"    在最近{Math.Min(Lookback, 60)}根K线内寻找按时间顺序排列的4个阶段：\n" +
        "    ①低位平台：≤5根，区间振幅(最高-最低)/最低 ≤5%\n" +
        "    ②高位平台：晚于①，≤3根，振幅≤5%，且最低价 ≥ ①最高价×1.10\n" +
        "    ③破位新低：晚于②，≤5根，区间最低价 ≤ ①最低价×0.95\n" +
        "    ④再突破：晚于③，且必须是最新一根K线——最新收盘价 > ②高位平台的最高价\n\n" +
        "2. 收盘价在BOLL中线上方\n" +
        $"    最新收盘价 > 布林带中轨（收盘价的{Lookback}期简单移动平均）\n\n" +
        "3. MACD零轴之上\n" +
        "    DIF（快线）和 DEA（慢线）都大于0\n\n" +
        "结果列表只显示3条全部满足的股票；因历史数据不足无法计算的会被跳过，数量会在分析完成后的日志里汇总。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }

    public FoundationTabViewModel(AnalyzerPaths paths, IBarRepository barRepository)
    {
        _paths = paths;
        _barRepository = barRepository;

        _selectedTopLevel = TopLevelGranularities[1]; // default "日"
        _selectedMinutePeriod = MinutePeriods[2]; // default 15分钟

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "筑基法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
    }

    private void Log(string message) => LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private string ResolveGranularity() => IsMinuteGranularity ? SelectedMinutePeriod.Value : SelectedTopLevel.Value;

    private async Task RunAnalyzeAsync()
    {
        IsBusy = true;
        Results.Clear();
        try
        {
            var granularity = ResolveGranularity();
            var codes = _barRepository.GetAllCodes();
            if (codes.Count == 0)
            {
                Log("本地没有任何数据，请把 Fetcher 产出的数据库拷贝到本地数据目录后点击\"刷新\"");
                return;
            }

            // Every other place identifies a stock by its code — look the real name up once from
            // StockMeta so results show "贵州茅台", not "600519" twice (the engine only ever
            // deals in codes, it doesn't know names).
            var names = SqliteStockMetaUpsert.GetAll(_paths.TotalDb).ToDictionary(s => s.Code, s => s.Name);

            var engine = new FoundationAnalysisEngine(_barRepository);
            int passedCount = 0, errorCount = 0;
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, granularity, Lookback);
                    result.Granularity = granularity;
                    result.Name = names.GetValueOrDefault(code, code);

                    if (result.Error != null) { errorCount++; continue; }
                    if (!result.Passed) continue; // results list only shows candidates that satisfy all 3 rules

                    passedCount++;
                    App.Current.Dispatcher.Invoke(() => Results.Add(ResultRowViewModel.From(result)));
                }
            });
            Log($"分析完成，粒度={granularity}，共扫描 {codes.Count} 只股票，{passedCount} 只满足全部条件" +
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
