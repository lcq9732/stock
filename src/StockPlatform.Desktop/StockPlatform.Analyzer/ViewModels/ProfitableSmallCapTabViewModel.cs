using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using StockPlatform.Analyzer.Export;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>"陈峰法"页（2026-11-16）——判据见 <see cref="ProfitableSmallCapAnalysisEngine"/>，
/// 卖出纪律在每日晨检（<see cref="VolumeExitDiscipline"/>）。结果按 250 日位置升序，前 30 只标"建仓名单"
/// （回测就是这么取的）。</summary>
public class ProfitableSmallCapTabViewModel : INotifyPropertyChanged
{
    public const string MethodName = "陈峰法";
    /// <summary>回测持仓只数——排在前面这么多只标"建仓名单"。</summary>
    public const int TopN = 30;

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
    private readonly IFinancialRepository _financialRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    /// <summary>最低股价（元），界面可调，默认 10。跑分析时取一次快照。</summary>
    private double _minPrice = ProfitableSmallCapAnalysisEngine.DefaultMinPrice;
    public double MinPrice
    {
        get => _minPrice;
        set => Set(ref _minPrice, value);
    }

    public string CriteriaInfoText =>
        "陈峰法 — 盈利小盘 + 一年低位 + 缩量分批买，放量减仓（固定用日线，8条必须全部满足）\n\n" +
        "来历：库里十大流通股东\"陈峰\"的持仓风格——流通5~50亿小盘、盈利、在一年低位附近；\n" +
        "盘中缩量时分批低吸，分时放量就减仓。\n\n" +
        "════ 入选条件 ════\n" +
        "1. 非北交所、简称不含ST\n" +
        "2. 最新流通市值 5~50 亿元\n" +
        "3. 上市满 1 年（用名册里的上市日期；个别没有的退回第一根日K）\n" +
        "4. 近四季（TTM）归母净利润 > 0\n" +
        "5. 近四季（TTM）经营现金流 > 0\n" +
        $"6. 股价 ≥ {MinPrice:0.##} 元（页面上可改，默认10元）\n" +
        "7. 近一年处于低位：收盘价在近250日[最低收盘, 最高收盘]区间的下30%以内\n" +
        "8. 近5个交易日（含当天）至少有1天缩量：当天成交额 < 前20日平均成交额×0.6\n" +
        "   备注列显示缩量天数和日期；\"今天缩量\"表示今天就在缩量。\n\n" +
        "结果按250日位置从低到高排，前30只标\"建仓名单\"（回测就是这样取的）。\n\n" +
        "════ 卖出纪律（在【每日晨检】里，对来源是陈峰法的持仓生效）════\n" +
        "· 放量收涨（成交额 > 前20日均×2 且收涨）：第一次减半，减过之后再放量就清仓\n" +
        "· 较买入均价 +25% 减半；+50% 或 250日位置 > 80% 清仓\n" +
        "· 较上次买入价跌15%、买入不足3笔：可加仓\n" +
        "· 近四季净利转亏或经营现金流转负：清仓\n" +
        "· 不用短线的 MA5 卖点 / 20天时间止损 / 15%回撤止损（那是短线口径，跟这套长持打法冲突）\n\n" +
        "════ 回测（2016-01~2026-11，含退市股，双边成本0.3%，取30只）════\n" +
        "· 只按条件选股、不设卖出纪律：年化 +2.7%，最大回撤 −51%——挑低位本身不赚钱\n" +
        "· 加价格版加减仓 + 放量先减半再清：年化 +9.3%，最大回撤 −31%\n" +
        "· 对照：同池盈利股等权 +6.4% / −53%；中证500 −0.7%\n" +
        "· 卖出纪律主要在熊市/震荡年护盘（2018、2022、2026）；牛市主升浪里会卖早（2019年 +56% → +15%）\n" +
        "· 放量收涨后20日，同池平均跑输1.6%（两段都成立）；缩量后20日只多0.12%——缩量是\"不追高\"，不是买入信号\n" +
        "· 回测的股价门槛是15元，它在回测里是负贡献（同池等权 +6.4% → +3.5%）；现在默认10元，回测数字没按10元重跑\n" +
        "· 缩量/放量口径按成交额；回测用的是换手率，数字不完全一一对应\n" +
        "· 回测没有剔除历史ST，只能靠盈利条件挡";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public ProfitableSmallCapTabViewModel(AnalyzerPaths paths, IBarRepository barRepository,
        IFundamentalMetricRepository fundamentalRepository, IFinancialRepository financialRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _fundamentalRepository = fundamentalRepository;
        _financialRepository = financialRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            TextDetailWindow.Show("陈峰法 — 分析条件说明", "陈峰法 — 入选条件、卖出纪律与回测", CriteriaInfoText));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, MethodName, Granularity.Day, lookback: null);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults(MethodName, Results));
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

            double minPrice = MinPrice;   // 取一次快照：扫描跑在后台线程，中途改输入框不该改判据
            if (minPrice < 0) { Log("最低股价不能为负数"); return; }
            var passed = new List<StockScreenResult>();
            int errorCount = 0, shrinkMissing = 0, noListDate = 0;
            await Task.Run(() =>
            {
                var names = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).ToDictionary(s => s.Code, s => s.Name);
                var listDates = SqliteStockMetaUpsert.GetListDates(_paths.CurrentDb);
                var engine = new ProfitableSmallCapAnalysisEngine(_barRepository, _fundamentalRepository, _financialRepository, listDates)
                {
                    MinPrice = minPrice,
                };
                // 只扫个股：Bar 表里还有 ETF/指数/板块（带前缀），名册 GetAll 只给个股
                var stockCodes = codes.Where(names.ContainsKey).ToList();
                for (int i = 0; i < stockCodes.Count; i++)
                {
                    var code = stockCodes[i];
                    if (i % 50 == 0) ProgressText = $"正在分析 {code} ({i + 1}/{stockCodes.Count})";
                    var r = engine.Analyze(code, names[code]);
                    if (r.Error != null) { errorCount++; continue; }
                    if (!r.Passed) continue;
                    if (r.Criteria.Any(c => c.DataMissing)) shrinkMissing++;
                    if (r.Criteria.Any(c => c.Name.StartsWith("3 ") && c.Basis.Contains("名册里没有上市日"))) noListDate++;
                    passed.Add(r);
                }
            });

            int rank = 0;
            foreach (var r in passed.OrderBy(r => r.SortScore ?? double.MaxValue))
            {
                rank++;
                r.Category = rank <= TopN ? "建仓名单" : "";
                Results.Add(ResultRowViewModel.From(r));
            }
            Log($"分析完成，{passed.Count} 只满足全部条件（前 {Math.Min(TopN, passed.Count)} 只标\"建仓名单\"）" +
                (shrinkMissing > 0 ? $"；其中 {shrinkMissing} 只近5日成交额缺失，缩量这条被跳过" : "") +
                (noListDate > 0 ? $"；{noListDate} 只名册里没有上市日，用第一根日K代替" : "") +
                (errorCount > 0 ? $"；{errorCount} 只没有日K被跳过" : ""));
            Log("提醒：这一页只给建仓名单，收益靠的是卖出纪律——买入后加进【主动仓】并填买入记录，每日晨检会按陈峰法纪律提示减仓/清仓。");
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
