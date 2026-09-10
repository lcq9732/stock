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
/// FoundationAnalysisEngine 的类注释——纯贯穿一条每日会命中 681 只且没有超额，粘合+低位才有。
/// 2026-09-10 加"方向"下拉框（不限阴阳 / 一阳破三线 / 一阳破三线且收盘站上，默认最后一档）：
/// 第一版不限方向，实跑名单在下跌日 76 只里 67 只是一阴破线，用户要的是一阳破三线。</summary>
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

    /// <summary>方向下拉框的选项文案——顺序必须跟 <see cref="FoundationDirection"/> 的取值
    /// (0/1/2) 一一对应，<see cref="DirectionIndex"/> 直接把下标当枚举值用。</summary>
    public IReadOnlyList<string> DirectionOptions { get; } = new[]
    {
        "不限阴阳",
        "一阳破三线",
        "一阳破三线且收盘站上",
    };

    /// <summary>选中的方向（下拉框 SelectedIndex）。默认 2 = 一阳破三线且收盘站上——最初做的是
    /// "不限阴阳"，实跑名单在下跌日 76 只里 67 只是一阴破线，用户看到后改口要阳线（2026-09-10）。
    /// 三档的实测对比见 FoundationAnalysisEngine.DefaultDirection。</summary>
    private int _directionIndex = (int)FoundationAnalysisEngine.DefaultDirection;
    public int DirectionIndex
    {
        get => _directionIndex;
        // 条件说明文字里带着当前方向档，所以换档要顺手通知它重算（其它三个参数同理由自身
        // 的 Set 通知不了 CriteriaInfoText——那三个只是数字，说明文字里直接插值就够了）。
        set { Set(ref _directionIndex, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CriteriaInfoText))); }
    }

    /// <summary>下拉框选中项对应的枚举值，传给引擎。</summary>
    public FoundationDirection Direction => (FoundationDirection)_directionIndex;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    public string CriteriaInfoText =>
        $"峰哥法 — 入选条件（固定日线，3条全部满足）：\n\n" +
        $"1. {FoundationAnalysisEngine.DirectionText(Direction)}从最低到最高贯穿三根均线\n" +
        $"    某根K线的 最低价 < MA5/MA10/MA20 三者的最低，且 最高价 > 三者的最高——整根K线（含上下影线）把三条均线全包住。\n" +
        $"    用最高/最低价而不是实体，因为口径就是\"从最低到最高\"。\n" +
        $"    方向三档可切（当前=\"{DirectionOptions[DirectionIndex]}\"）：不限阴阳 / 一阳破三线 / 一阳破三线且收盘站上三线。\n" +
        $"    实测（间距≤1.5% 低位≤20% 内，2025-09~2026-09 共246个交易日、前复权口径）：\n" +
        $"        不限阴阳     每日 84 只，10日 +1.28% 胜56.5%（最早那版，名单里一半是一阴破线）\n" +
        $"        一阳破三线   每日 34 只，10日 +1.46% 胜57.7%\n" +
        $"        阳线且站上   每日 19 只，10日 +1.59% 胜58.6%  ← 默认这档\n" +
        $"        （被排掉的阴线那半确实最差：每日 50 只，10日 +1.16% 胜55.7%；下跌半年里差距更大——\n" +
        $"          只要阳线 20日 -0.10%，阳线且站上 +0.65%，收盘站没站上决定这根阳线是不是假突破）\n" +
        $"    回看 N={Lookback} 根（可调，默认1 = 只看最新交易日那根；调大是为了回看最近几天核对形态。\n" +
        $"    窗口里方向不合的那根会跳过继续往前找，不会因为它挡在前面就整只落选）。\n\n" +
        $"2. 三根均线粘合：命中日 (最高均线 − 最低均线) ÷ 收盘 ≤ {MaxSpreadPct:F1}%（可调，默认1.5%）\n\n" +
        $"3. 处于低位：命中日收盘价位于近{FoundationAnalysisEngine.PositionWindow}日 [最低,最高] 区间的下 {MaxLowPositionPct:F0}%（可调，默认20%）\n\n" +
        "为什么后两条是必需的（同一批实测）：\n" +
        "    · 只用第1条且不限方向：每日命中 681 只（全市场12%），5/10/20日前瞻收益 +0.02%/+0.11%/+0.00%，胜率47%/47%/45%，\n" +
        "      跟同期全市场基准(+0.16%/+0.26%/+0.46%)没区别，下跌半年里还明显跑输。三线平时只差1.5%，而日振幅中位4.3%，\n" +
        "      随便一根K线就\"包住\"了，所以单这一条几乎没有筛选力。\n" +
        "    · 加上第2、3条：把样本按时间切两半，前后半段都跑赢各自的同期基准，方向一致，不是阈值凑出来的。\n" +
        "    · 实测反而更差、故意没有加的条件：放量≥5日均量1.5倍(5日 -0.16%)、振幅≥6%(5日 -0.60%)、只挑\"穿得最透\"的\n" +
        "      前20只(5日 -0.29% 胜43%，全表最差)——穿得越猛越差。\n\n" +
        "结果按\"低位分\"（100 − 60日位置%）从高到低排序，越靠近60日底部的排前面；这只是优先级参考，组内区分度不强。\n" +
        "点\"条件详情\"可在K线上看到命中的那根K线和当天的三线位置。历史数据不足无法计算的会被跳过，数量在分析完成后的日志里汇总。";

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
            var direction = Direction;   // 取一次快照：扫描跑在后台线程，中途换下拉框不该改判据
            int errorCount = 0;
            var passed = new List<StockScreenResult>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, names.GetValueOrDefault(code, code), n, spread, lowPos, direction);
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
                $"{FoundationAnalysisEngine.DirectionText(direction)}贯穿三线 + 三线间距≤{spread:F1}% + 低位≤{lowPos:F0}%（已按低位分从高到低排序）" +
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
