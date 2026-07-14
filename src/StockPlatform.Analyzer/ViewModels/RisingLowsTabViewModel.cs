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

/// <summary>"阶梯低点法" tab — 见 doc/analysis-app-design.md。固定用日线，9条规则内部窗口/阈值都是
/// 固定值（形态条按 000977/000066 两只样本校准、C8/C9 及市场环境按 2026-07-13 回测校准，见
/// RisingLowsAnalysisEngine），必须9条全满足才入选。</summary>
public class RisingLowsTabViewModel : INotifyPropertyChanged
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

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    private string _marketEnvText = "";
    /// <summary>最近一次分析算出的市场宽度/热度摘要（出手条件），显示在结果表上方。</summary>
    public string MarketEnvText { get => _marketEnvText; set => Set(ref _marketEnvText, value); }

    private string _cutoffDateText = DateTime.Today.ToString("yyyy-MM-dd");
    /// <summary>手工验证用的截止日期（默认今天；留空也=用最新数据）：填历史日期时整个分析按
    /// "那天收盘往前"计算，等价于回测的截断口径；今天或未来的日期等同最新数据、不进验证模式。</summary>
    public string CutoffDateText { get => _cutoffDateText; set => Set(ref _cutoffDateText, value); }

    /// <summary>最近一次分析实际用的截止日期（null=最新数据）——"条件详情"按钮据此把图表数据
    /// 截到同一天，保证图和判定一致（见 MainWindow.RisingLowsCriteriaButton_Click）。</summary>
    public DateTime? AppliedCutoffDate { get; private set; }

    public string CriteriaInfoText =>
        "阶梯低点法 — 入选条件（固定用日线；9条必须全部满足）：\n\n" +
        "整体形态：起点低 → 拉升到阶段顶 → 回落在更高的低点金叉 → 反弹到P → 回踩V(又一个更高的低点) → 今日重新向上。\n" +
        "核心：三个依次抬高的低点（起点低 < 金叉低 < 回踩V低）。天数均为交易日、可±5天。\n\n" +
        "C1 金叉前约27交易日出现阶段顶（金叉前60交易日内最高点）\n" +
        "C2 约14交易日前出现MACD金叉，且金叉发生在零轴下方\n" +
        "C3 金叉附近最低点 略高于 其前约50交易日最低点（高出0%~10%）\n" +
        "C4 金叉后反弹到一个高点P\n" +
        "C5 回踩V：自P回落≤17%，且V低点高于金叉低点\n" +
        "C6 MACD：金叉后DIF不下穿DEA、回踩后DIF回升、今日DIF/DEA双双在零轴上方\n" +
        "C7 今日向上确认：今收 > 昨收，且今收高于回踩V底\n" +
        "C8 量能：回踩V日缩量（<前5日均量）——2026-07-13起不再要求今日放量，回测显示放量确认反而降低胜率\n" +
        "C9 今日收盘距阶段顶最高价至少3%（未破顶/未贴顶）——2026-07-13加入：破顶=追高（胜率54%）、贴顶0~3%最差（50%），顶下3%~15%才是最优区间（胜率65%+）\n\n" +
        "市场环境出手条件（2026-07-13 按86个历史时间点回测加入，两条都满足才出手，否则信号仅观察）：\n" +
        "· 市场宽度 >50%：全市场股票中\"收盘价>自身20日均线(MA20，含当日)\"的占比\n" +
        "· 市场热度 >1.0：当日全市场Σ(成交量×收盘价) ÷ 之前20个交易日该值的平均（不含当日）\n\n" +
        "买入纪律（回测口径）：信号为盘后产生 → 次日开盘买；次日开盘相对信号日收盘高开超过3%则放弃；持有满10个交易日。\n" +
        "依据：等回踩再买不提升胜率且会错过最强的两成信号；持有5日比10日胜率低6~14个百分点。\n" +
        "历史表现(2024-02~2026-06，加C9后166笔)：胜率65.1%、平均+3.93%、中位+2.67%。阈值在同一批历史数据上选出，有过拟合风险，建议持续跟踪验证。\n\n" +
        "形态阈值按 000977 浪潮、000066 长城两只样本校准；历史数据不足的股票会被跳过，数量在分析完成后的日志里汇总。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public RisingLowsTabViewModel(AnalyzerPaths paths, IBarRepository barRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "阶梯低点法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "阶梯低点法", Granularity.Day, lookback: null);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("阶梯低点法", Results, includeTotalCount: false));
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

            // 手工验证模式：输入了截止日期就把所有数据截到那天收盘（引擎+市场环境+详情图同一口径）
            DateTime? cutoff = null;
            if (!string.IsNullOrWhiteSpace(CutoffDateText))
            {
                // 接受不补零的写法：2026-7-1 / 2026-07-1 / 2026-7-01 / 2026-07-08，分隔符 - 或 /
                // （yyyy-M-d 里 M/d 同时接受一位和两位数）
                if (!DateTime.TryParseExact(CutoffDateText.Trim().Replace('/', '-'), "yyyy-M-d",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    Log($"截止日期格式不对：\"{CutoffDateText}\"，请用 年-月-日（如 2026-07-08、2026-7-1 都行），留空表示用最新数据");
                    return;
                }
                // 今天(默认值)或未来的日期 = 用最新数据，不必截断、也不算验证模式
                if (parsed.Date < DateTime.Today)
                {
                    cutoff = parsed;
                    Log($"手工验证模式：按 {cutoff:yyyy-MM-dd} 收盘往前计算（结果和条件详情图都截止到这一天）");
                }
            }
            AppliedCutoffDate = cutoff;
            IBarRepository repo = cutoff.HasValue ? new CutoffBarRepository(_barRepository, cutoff.Value) : _barRepository;

            var names = SqliteStockMetaUpsert.GetAll(_paths.TotalDb).ToDictionary(s => s.Code, s => s.Name);
            var engine = new RisingLowsAnalysisEngine(repo);
            int passedCount = 0, errorCount = 0;
            await Task.Run(() =>
            {
                // 先算市场环境（宽度/热度出手条件）——市场级判断，跟单只股票的 C1~C8 无关，
                // 不满足时不拦截结果，只在界面/日志里明确提示"今日信号仅观察"
                var env = MarketEnvironmentCalculator.Compute(repo, p => ProgressText = p);
                App.Current.Dispatcher.Invoke(() =>
                {
                    MarketEnvText = env?.Summary ?? "市场环境：本地无数据，无法计算";
                    Log(MarketEnvText);
                });

                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, names.GetValueOrDefault(code, code));
                    if (result.Error != null) { errorCount++; continue; }
                    if (!result.Passed) continue;

                    passedCount++;
                    App.Current.Dispatcher.Invoke(() => Results.Add(ResultRowViewModel.From(result)));
                }
            });
            Log($"分析完成，共扫描 {codes.Count} 只股票，{passedCount} 只满足全部9条条件" +
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足被跳过" : ""));
            if (passedCount > 0)
                Log("买入纪律（回测口径）：次日开盘买，高开超3%放弃，持有满10个交易日");
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
