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

/// <summary>"彬哥法"（原名中盘起爆法，类名沿用 MidCapPullback）tab — see
/// doc/analysis-app-design.md section 3.2.4. Fixed to daily bars for most conditions (plus
/// week/month for the two MACD rules). No user-adjustable parameters — all 10 conditions'
/// thresholds are fixed per the original spec.</summary>
public class MidCapPullbackTabViewModel : INotifyPropertyChanged
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
    private readonly IFundamentalMetricRepository _fundamentalRepository;
    private readonly IShareholderRepository _shareholderRepository;
    private readonly IMarginRepository _marginRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        "彬哥法 — 入选条件（12条必须全部满足，固定用日线+周线+月线）：\n\n" +
        "1. 上市板块不包含科创板\n" +
        "2. 股票市场类型不包含北交所\n" +
        "3. 股票简称不包含ST、*ST\n" +
        "4. 最新交易日流通市值（不含限售股）大于80亿元且小于300亿元\n" +
        "5. 最近15个交易日（含当前交易日）内，涨停次数大于1次（按收盘涨停统计，" +
        "涨跌停幅度按板块+ST状态区分：主板10%/创业板科创板20%/北交所30%/ST股5%）\n" +
        "6. 月线MACD采用默认参数(12,26,9)，MACD柱值大于0\n" +
        "7. 周线MACD采用默认参数(12,26,9)，MACD柱值大于0\n" +
        "8. 当前交易日开盘价低于MA15\n" +
        "9. 当前交易日收盘价高于MA15\n" +
        "10. 前一交易日收盘价低于前一交易日MA15\n" +
        "11. 最新报告期股东户数环比上一报告期下降（筹码集中方向）；\"是否接近近两年最低户数\"只作参考、不作硬性门槛\n" +
        "12. 最新交易日融资余额较上一交易日增长（资金加杠杆流入方向）";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public MidCapPullbackTabViewModel(
        AnalyzerPaths paths, IBarRepository barRepository, IFundamentalMetricRepository fundamentalRepository, IShareholderRepository shareholderRepository, IMarginRepository marginRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _fundamentalRepository = fundamentalRepository;
        _shareholderRepository = shareholderRepository;
        _marginRepository = marginRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            TextDetailWindow.Show("彬哥法 — 分析条件说明", "彬哥法 — 入选条件与依据说明", CriteriaInfoText));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "彬哥法", Granularity.Day, lookback: null);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("彬哥法", Results));
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

            var engine = new MidCapPullbackAnalysisEngine(_barRepository, _fundamentalRepository, _shareholderRepository, _marginRepository);
            int passedCount = 0, errorCount = 0;
            var missingDataCounts = new Dictionary<string, int>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var name = names.GetValueOrDefault(code, code);
                    var result = engine.Analyze(code, name);

                    if (result.Error != null) { errorCount++; continue; }
                    foreach (var c in result.Criteria.Where(c => c.DataMissing))
                        missingDataCounts[c.Name] = missingDataCounts.GetValueOrDefault(c.Name) + 1;
                    if (!result.Passed) continue; // results list only shows candidates that satisfy all 10 rules

                    passedCount++;
                    App.Current.Dispatcher.Invoke(() => Results.Add(ResultRowViewModel.From(result)));
                }
            });
            var missingSummary = missingDataCounts.Count > 0
                ? "；" + string.Join("；", missingDataCounts.Select(kv => $"「{kv.Key}」这条条件因缺数据被跳过（不计入该条件，不代表股票被跳过，其余条件正常判断）：{kv.Value} 只涉及"))
                : "";
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passedCount} 只满足全部条件" +
                (errorCount > 0 ? $"，{errorCount} 只因日/周/月线历史数据不足被跳过" : "") + missingSummary);
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
