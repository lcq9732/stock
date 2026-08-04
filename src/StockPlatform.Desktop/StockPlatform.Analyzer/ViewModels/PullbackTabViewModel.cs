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

/// <summary>"回调法" tab —— 见 <see cref="PullbackAnalysisEngine"/> 的回测说明。跟其它方法不同的
/// 两点：① 结果按"低于MA20的幅度"降序（跌得深的排前面，但深跌只是排序依据不是入选门槛，理由见
/// 引擎注释）；② 顶部显示大盘MA60状态——回测里带止损的策略在大盘跌破MA60时明显变差，所以这是
/// 出手前该看一眼的背景，同样只提示不过滤。</summary>
public class PullbackTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>单票仓位上限占总资产的比例——一手金额上限 = 总资产 × 这个值。</summary>
    private const double MaxPositionPct = 0.15;

    private readonly AnalyzerPaths _paths;
    private readonly IBarRepository _barRepository;
    private readonly IFinancialRepository _financialRepository;
    private readonly IDividendRepository _dividendRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    /// <summary>总资产（元）——只用来推导"单票上限10%"进而推导一手金额上限，不参与其它计算。</summary>
    private string _capitalText = "1000000";
    public string CapitalText { get => _capitalText; set => Set(ref _capitalText, value); }

    private string _marketStateText = "（点\"开始分析\"后显示）";
    /// <summary>大盘 MA60 状态提示，见类注释。</summary>
    public string MarketStateText { get => _marketStateText; private set => Set(ref _marketStateText, value); }

    public string CriteriaInfoText =>
        "回调法 — 入选条件（固定用日线；6条必须全部满足）：\n\n" +
        "1. 净利润为正（最新一期财报，归母净利润>0）\n" +
        "2. 经营现金流为正（赚的是真钱，不是账面利润）\n" +
        "3. 20日均成交额 ≥ 1亿元（进得去出得来）\n" +
        "4. 收盘价低于 MA20（不追高，买回调）\n" +
        "5. 距一年内最高价已回撤 ≥ 10%\n" +
        $"6. 一手（100股）金额 ≤ 总资产的{MaxPositionPct * 100:F0}%\n" +
        "    ——高价股买一手就重仓，到需要减仓时只剩\"全清\"一个选项，仓位余地要在建仓时留出来\n\n" +
        "排序：按\"低于MA20的幅度\"降序，跌得越深排越前。\n" +
        "扫描范围已排除：ETF/指数/板块、ST与退市股、科创板(688)、北交所(8x/4x)。\n" +
        "    ——排除后两个板块是因为下面那份回测的样本本来就没包含它们，结论不能外推过去。\n\n" +
        "── 回测依据（2022-06~2026-02，44个时点，186226个样本，+10%止盈/-10%止损/最长持有120日）──\n\n" +
        "基准（成交额≥1亿）              平均 0.59%，胜率 52.9%\n" +
        "仅\"收盘<MA20\"                  平均 0.93%，胜率 54.6%   ← 单条最有效\n" +
        "加\"净利>0 + 现金流>0\"           平均 0.96%，胜率 54.7%   ← 本方法采用\n" +
        "再加 ROE≥10% / 净利同比增长      平均 0.28%，胜率 51.4%   ← 反而更差，故未采用\n\n" +
        "按\"低于MA20的幅度\"分档（样本数很重要，越少越不可信）：\n" +
        "  0~-3%     平均0.29%  胜率51.3%   样本 9718  ← 接近随机\n" +
        "  -3~-8%    平均0.73%  胜率53.6%   样本10603\n" +
        "  -8~-15%   平均2.98%  胜率65.0%   样本 3650  ← 效果好且样本足\n" +
        "  -15~-20%  平均5.61%  胜率78.1%   样本  319  ← 效果最好\n" +
        "  -20~-25%  平均4.67%  胜率73.3%   样本   45   参考价值有限\n" +
        "  深于-25%                         样本  <10  ⚠ 已超出验证范围，属于外推\n\n" +
        "⚠ 分档虽然单调，但分年看并不稳定：深跌档2025年是+7.57%，2022年是-1.54%（反向），\n" +
        "且深跌样本有近三成来自2025-01-13和2025-04-16两个V型底。本质是\"买跌有效当且仅当后面\n" +
        "有反弹\"，所以深跌只作排序依据和提示，不设为入选门槛。\n\n" +
        "⚠ 大盘状态同理只作提示：回测中带止损的策略在上证跌破MA60时明显变差，但不强行过滤结果。\n\n" +
        "⚠ 入选数量本身就是温度计：正常市场深跌档只有零星几只，若几百只同时跌破MA20两位数，\n" +
        "说明是系统性下跌而非个股回调，此时\"后面有反弹\"这个前提最不可靠（日志里会给出分档统计）。\n\n" +
        "────────────  关于结果表里的两个止盈目标  ────────────\n\n" +
        "上面这套条件是按 +10% 止盈 校准的。但按 2026-08-03 核对的券商真实流水，\n" +
        "实际打法的止盈目标是 +2%（7笔已清仓里6笔涨幅 -1.37%~+2.24%、平均+1.34%，\n" +
        "只有宁德时代那笔 6天 +12.84% 是例外）。两个口径的回测结果差别很大：\n\n" +
        "  +10% 止盈 / -10% 止损 / 最长120日   平均 +0.96%   胜率 54.7%\n" +
        "  +2%  止盈 / -5%  止损 / 最长60日    平均 -0.39%   胜率 65.8%   平均持有3.2天\n\n" +
        "⚠ +2% 口径胜率虽高但**期望为负**：0.66×2% − 0.34×5% = −0.38%。而且在这个口径下，\n" +
        "不追高、基本面、深跌、大盘状态所有条件组合全部为负——也就是说本方法的条件取舍\n" +
        "只在 +10% 口径下成立。\n\n" +
        "7笔全胜是因为不止损、一直拿到回本（7笔里3笔曾深度浮亏）。这不是消除亏损，而是把\n" +
        "亏损推迟并转成尾部风险——茅台那笔价差亏3700，是分红把它救回来的。按平均仓位11.8万算，\n" +
        "一次 -10% 就是 -11,800，等于抹掉4.5笔正常交易的利润。\n\n" +
        "所以结果表里两个目标价并列给出，本方法不替你做选择。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public PullbackTabViewModel(
        AnalyzerPaths paths,
        IBarRepository barRepository,
        IFinancialRepository financialRepository,
        IDividendRepository dividendRepository,
        JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _financialRepository = financialRepository;
        _dividendRepository = dividendRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "回调法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "回调法", Granularity.Day, lookback: null);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("回调法", Results, includeTotalCount: true));
    }

    private void Log(string message) => LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>上证收盘 vs MA60——引擎不用它过滤，只在界面上提示，见类注释。</summary>
    private string ComputeMarketState()
    {
        var bars = _barRepository.Query("sh000001", Granularity.Day);
        if (bars.Count < 60) return "无法计算大盘状态（本地缺上证指数日线）";
        int i = bars.Count - 1;
        double ma60 = 0;
        for (int t = i - 59; t <= i; t++) ma60 += bars[t].Close;
        ma60 /= 60;
        double close = bars[i].Close;
        bool above = close > ma60;
        return $"大盘（{bars[i].PeriodStart:yyyy-MM-dd}）：上证 {close:F2}，MA60 {ma60:F2}，" +
               $"位于MA60{(above ? "上方" : "下方")} {Math.Abs(close / ma60 - 1) * 100:F2}% —— " +
               (above ? "回测中此状态下带止损的打法表现较好" : "⚠ 回测中此状态下带止损的打法明显变差，建议降低仓位或只观察");
    }

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

            if (!double.TryParse(CapitalText, out var capital) || capital <= 0)
            {
                Log($"总资产填写有误（{CapitalText}），请填正整数，例如 550000");
                return;
            }
            double maxLot = capital * MaxPositionPct;

            MarketStateText = ComputeMarketState();
            Log(MarketStateText);

            ProgressText = "正在载入财务与分红数据…";
            var financials = await Task.Run(() => _financialRepository.GetLatestSnapshotByCode());
            var dividends = await Task.Run(() =>
                _dividendRepository.GetTrailingCashDividendPerShare(DateTime.Today.AddYears(-1)));
            Log($"已载入 {financials.Count} 只股票的财务快照、{dividends.Count} 只的近一年派息");

            var names = SqliteStockMetaUpsert.GetAll(_paths.TotalDb).ToDictionary(s => s.Code, s => s.Name);

            var engine = new PullbackAnalysisEngine(_barRepository, financials, dividends) { MaxLotAmount = maxLot };
            int passedCount = 0, errorCount = 0, noFinancialCount = 0;
            var rows = new List<ResultRowViewModel>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    // ETF/板块指数/指数不参与个股筛选——它们没有财报，条件1/2会一律 DataMissing。
                    if (!names.TryGetValue(code, out var name)) continue;
                    if (code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("gn_") ||
                        code.StartsWith("new_") || code.StartsWith("dy_")) continue;
                    // 科创板(688)/北交所(8x、4x)排除：引擎注释里那份回测的样本就没包含它们，
                    // 把结论套到没测过的板块上不成立（何况这两个板块还有单独的开户门槛）。
                    if (code.StartsWith("688") || code.StartsWith("8") || code.StartsWith("4")) continue;
                    if (name.Contains("ST") || name.StartsWith("退")) continue;

                    if (i % 100 == 0) ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code);
                    result.Name = name;

                    if (result.Error != null) { errorCount++; continue; }
                    if (result.Criteria.Any(c => c.DataMissing)) noFinancialCount++;
                    if (!result.Passed) continue;

                    passedCount++;
                    rows.Add(ResultRowViewModel.From(result));
                }
            });

            // 跌得越深排越前（SortScore = 低于MA20的百分比）
            foreach (var r in rows.OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(r);

            Log($"分析完成，共扫描 {codes.Count} 个代码（已排除ETF/指数/ST/科创板/北交所），" +
                $"{passedCount} 只全部满足6个条件" +
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足被跳过" : "") +
                (noFinancialCount > 0 ? $"，{noFinancialCount} 只缺财务数据（该条件被跳过，不影响其余条件判断）" : ""));

            if (passedCount > 0)
            {
                // 按回测档位报一下分布——入选数量本身就是市场状态的温度计：正常市场深跌档只有零星
                // 几只，一旦几百只同时跌破MA20两位数，说明是系统性下跌而不是个股回调，这时"买回调"
                // 的前提（后面有反弹）最不可靠。
                int shallow = Results.Count(r => r.SortScore < 3);
                int mid = Results.Count(r => r.SortScore >= 3 && r.SortScore < 8);
                int deep = Results.Count(r => r.SortScore >= 8 && r.SortScore < 15);
                int veryDeep = Results.Count(r => r.SortScore >= 15 && r.SortScore < 25);
                int extreme = Results.Count(r => r.SortScore >= 25);
                Log($"按跌幅分档：浅跌(0~3%) {shallow} 只 · 中跌(3~8%) {mid} 只 · 深跌(8~15%) {deep} 只 · " +
                    $"超跌(15~25%) {veryDeep} 只 · 极端(>25%) {extreme} 只");
                Log("回测里效果最好且样本充足的是深跌与超跌两档（8%~20%）；" +
                    "跌超25%的历史样本不足10个，属于外推，通常伴随个股利空，点\"条件详情\"会标出来。");
                if (deep + veryDeep + extreme > 100)
                    Log($"⚠ 有 {deep + veryDeep + extreme} 只同时跌破MA20超过8%，这是系统性下跌而非个股回调，" +
                        "\"买回调\"赖以成立的前提（后面有反弹）此时最不可靠，建议只观察或大幅降低仓位。");
            }
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
