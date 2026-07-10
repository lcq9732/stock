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

/// <summary>"短线法" tab — daily-close short-term entry screen (see ShortTermAnalysisEngine). Fixed
/// to daily bars. 放量倍数/涨幅上限/流通市值区间 are user-adjustable; 近期涨停是加分项，结果按它
/// 从高到低排序（涨停多=资金关注多，排前面），不影响是否入选。</summary>
public class ShortTermTabViewModel : INotifyPropertyChanged
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
    private readonly IFundamentalMetricRepository _fundamentalRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private double _volumeSurgeRatio = 1.5;
    public double VolumeSurgeRatio { get => _volumeSurgeRatio; set => Set(ref _volumeSurgeRatio, value); }

    private double _maxDayGainPct = 7;
    public double MaxDayGainPct { get => _maxDayGainPct; set => Set(ref _maxDayGainPct, value); }

    private double _minCapYi = 30;
    public double MinCapYi { get => _minCapYi; set => Set(ref _minCapYi, value); }

    private double _maxCapYi = 300;
    public double MaxCapYi { get => _maxCapYi; set => Set(ref _maxCapYi, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"短线法 — 入选条件（固定用日线；前7条为必须满足，第8条为过滤；近15日涨停是加分项、不作硬性门槛）：\n\n" +
        "1. 均线多头启动：收盘 > MA5 > MA10，且 MA10 拐头向上\n" +
        $"2. 放量：当日成交量 ≥ 前5日均量 × {VolumeSurgeRatio:F1}\n" +
        "3. 突破：收盘价创近20日新高（最高价口径）\n" +
        "4. MACD动能确认：MACD柱转正且放大，或 DIF 在0轴上方金叉\n" +
        "5. 主力资金净流入：最新交易日主力净流入 > 0（缺数据则跳过此条）\n" +
        $"6. 流通市值适中：{MinCapYi:F0}亿 ~ {MaxCapYi:F0}亿（缺数据则跳过此条）\n" +
        $"7. 不追高：当日涨幅 ≤ {MaxDayGainPct:F1}%\n" +
        "8. 过滤：排除 ST/*ST、北交所\n\n" +
        "结果只显示以上条件全部满足的股票，并按\"近15日涨停次数\"从高到低排序（涨停多的排前面——有资金关注、弹性大，但不作硬性入选条件）。\n\n" +
        "说明：适合 T+1 下的短线/波段——盘后选出\"明天值得关注的启动票\"，不做分时/打板。信号失败率不低，实盘请配止损（如跌破MA5或买入价-5%）。放量倍数/涨幅上限/流通市值区间都可在上方调整。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public ShortTermTabViewModel(AnalyzerPaths paths, IBarRepository barRepository,
        INetInflowRepository netInflowRepository, IFundamentalMetricRepository fundamentalRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _netInflowRepository = netInflowRepository;
        _fundamentalRepository = fundamentalRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "短线法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "短线法", Granularity.Day);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("短线法", Results, includeScore: true, scoreHeader: "近15日涨停"));
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
            var engine = new ShortTermAnalysisEngine(_barRepository, _netInflowRepository, _fundamentalRepository);
            double ratio = VolumeSurgeRatio, maxGain = MaxDayGainPct, minCap = MinCapYi, maxCap = MaxCapYi;
            int errorCount = 0;
            var missingDataCounts = new Dictionary<string, int>();
            var passed = new List<StockScreenResult>();

            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var name = names.GetValueOrDefault(code, code);
                    var result = engine.Analyze(code, name, ratio, maxGain, minCap, maxCap);

                    if (result.Error != null) { errorCount++; continue; }
                    foreach (var c in result.Criteria.Where(c => c.DataMissing))
                        missingDataCounts[c.Name] = missingDataCounts.GetValueOrDefault(c.Name) + 1;
                    if (!result.Passed) continue;

                    passed.Add(result);
                }
            });

            foreach (var r in passed.OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(ResultRowViewModel.From(r));

            var missingSummary = missingDataCounts.Count > 0
                ? "；" + string.Join("；", missingDataCounts.Select(kv => $"「{kv.Key}」这条条件因缺数据被跳过（不计入该条件，不代表股票被跳过，其余条件正常判断）：{kv.Value} 只涉及"))
                : "";
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passed.Count} 只满足全部条件（已按近15日涨停次数从高到低排序）" +
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足/次新被跳过" : "") + missingSummary);
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
