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

/// <summary>"短线法" tab —— 2026-08-07 规则整体替换，见 <see cref="ShortTermAnalysisEngine"/>。
/// 从"放量突破追涨"改成"回调埋伏"，参数不再暴露给界面调整：这套9条的每个阈值都是回测选出来的，
/// 随手改一个（比如把量比放宽到1.5）就会落到被证伪的那一侧，所以固定住、只在ⓘ里说明依据。
/// 顶部显示大盘MA60状态——两种环境都为正，但下行时明显更好（4.28% vs 1.83%），是出手前的背景。</summary>
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
    private readonly IFinancialRepository _financialRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    private string _marketStateText = "（点\"开始分析\"后显示）";
    public string MarketStateText { get => _marketStateText; private set => Set(ref _marketStateText, value); }

    public string CriteriaInfoText =>
        "短线法 — 回调埋伏型（2026-08-07 规则整体替换，固定用日线，8条必须全部满足）\n\n" +
        "【旧版已废弃】原来的\"均线多头启动+放量+突破20日新高+主力净流入+市值区间+涨幅≤7%\"是\n" +
        "追涨型。新版方向相反：找\"跌到位、动能刚要转、但市场还没放量追进去\"的票。\n" +
        "换掉的依据就是下面的回测——旧版核心的\"放量突破\"恰恰是被证伪的一侧：\n" +
        "入场当日放量的平均收益 0.14%，缩量的 1.01%，差七倍。\n\n" +
        "════ 质量底线（2条）════\n" +
        "1. 净利润为正（最新一期财报，归母净利润>0）\n" +
        "2. 经营现金流为正（赚的是真钱）\n" +
        "   —— 只保留底线，不做质量优选：ROE/净利增长这类重基本面筛选在回测里是负贡献\n\n" +
        "════ 位置（2条）════\n" +
        "3. 低于 MA20 在 3%~25% 之间（回测验证过的档位）\n" +
        "4. 距一年内最高价已回撤 ≥ 10%\n\n" +
        "════ 动能（4条）════\n" +
        "5. MACD柱连续 ≥2天 收窄，且仍在0轴下方（正在向0轴靠）\n" +
        "6. 入场时机 —— ★这一条按大盘状态分两个分支★\n" +
        "     · 上证 < MA60（熊市）：MA5 拐头向上 **且 收盘仍低于 MA5**\n" +
        "         回测 4.62% / 胜率73.2% / 样本1338（不加价格位置只有 3.29%/66.6%）\n" +
        "         理由：熊市反弹又快又短，等价格站上MA5，第一段已经被吃掉了\n" +
        "     · 上证 > MA60（牛市）：MA5 拐头向上（不限价格位置）\n" +
        "         回测 3.29% / 胜率66.6% / 样本1629\n" +
        "7. KDJ 处于金叉状态（K>D）\n" +
        "     2026-08-18 按要求放宽：原来还要求\"不是当日刚金叉\"，现在当日刚叉也算通过。\n" +
        "     2026-08-19 起结果表多了一列【KDJ状态】，把子状态和它的历史期望直接标在行上——\n" +
        "     放宽后“严格组”里会同时出现刚叉和金叉延续，两者质量差一截，不标出来看不见：\n" +
        "       未金叉(K≤D)      0.36% / 胜率51.8%  (16001样本)  ← 不通过\n" +
        "       今日刚金叉        0.63% / 胜率53.0%  (19221样本)  ← ⚠标红，四档里最差\n" +
        "       延续·K在40~60    2.53% / 胜率62.6%  (48120样本)  ← 标绿，最稳\n" +
        "       延续·K<40(超卖)  1.25% / 胜率56.1%  (93724样本)\n" +
        "       延续·K>60        4.59% / 胜率72.9%  ( 5046样本)  ← 样本太少，不当依据\n" +
        "     口径：2016年至今全样本、其余8条都满足、+10%止盈/-10%止损/最长120交易日。\n" +
        "8. 当日未放量：量比 ≤ 1.2（当日量 ÷ 前5日均量）\n\n" +
        "════ 仅提示、不拦截 ════\n" +
        "· 日均波幅(60日)：原为第9条硬条件，2026-08-07 按要求降级。\n" +
        "  它是回测里唯一站得住的护栏——分档 0~1.5%档0.94%、1.5~2.5%档1.67%、2.5~3.5%档1.67%、\n" +
        "  3.5~4.5%档2.10%，唯独 >4.5% 那档垮成 0.03%/胜率50.2%（等于随机）；\n" +
        "  且给它放宽止损只会更差（+15/-15→0.25%，+20/-20→-0.36%）。\n" +
        "  ⚠ 现在超过4.5%的票也会入选，条件详情里会标出来，请自行减半仓位或跳过。\n\n" +
        "排序：按\"低于MA20的幅度\"降序，跌得越深排越前。\n" +
        "扫描范围排除：ETF/指数/板块、ST与退市股、科创板(688)、北交所(8x/4x)。\n\n" +
        "──────── 回测依据 ────────\n" +
        "2022-06~2026-02，89个时点、28020个入场样本，财报按法定披露截止日滞后避免未来函数；\n" +
        "口径 = +10%止盈 / -10%止损 / 最长持有120交易日。逐层叠加：\n\n" +
        "  基准（位置+波幅+流动性）        平均 0.59%   胜率 52.9%   样本28020\n" +
        "  + MACD柱连续2天收窄且为负       平均 0.96%   胜率 54.8%   样本 9322\n" +
        "  + 当日未放量（量比<1.2）        平均 1.05%   胜率 55.2%   样本 8345\n" +
        "  + MA5拐头向上                 平均 2.77%   胜率 63.9%   样本 2915\n" +
        "  + KDJ金叉状态且非当日刚叉        平均 3.35%   胜率 66.8%   样本 2424\n" +
        "    ↑ 这是【放宽前】的口径。第7条现已放宽成\"只要金叉\"，实际表现会低于这一行。\n\n" +
        "大盘>MA60 时 1.83%/59.1%，大盘<MA60 时 4.28%/71.6% —— 两种环境都为正。\n\n" +
        "──────── 两个反直觉的点 ────────\n\n" +
        "① 指标交叉那一天是最差的买点，不是最好的：\n" +
        "   KDJ「当日刚金叉」在下面那份28020样本的回测里是 -1.23% / 胜率43.9%，延续状态 3.35% / 66.8%。\n" +
        "   ⚠ 2026-08-19 用2016年至今的全样本重跑：负期望复现不出来，刚叉是 +0.63% / 胜率53.0%\n" +
        "     （19221样本）——它仍是最差的一档，但不是亏钱的一档。放宽第7条没有放进负期望的信号。\n" +
        "   MACD「只收窄一天」 -0.46% / 胜率47.6%（全表最差），所以要求连续≥2天\n" +
        "   交叉瞬间噪音最大，要等它站稳一两天。\n" +
        "   现行口径：MACD 那半条仍在执行；KDJ 那半条按要求已取消（当日刚叉也放行，只标注）。\n\n" +
        "② K值不是越低越好：\n" +
        "   K在40~60  平均5.37% 胜率76.9%      K在0~40  平均1.93% 胜率59.8%   ←旧窄样本\n" +
        "   全样本重跑同向、但没这么夸张：K40~60 2.53%/62.6%，K<40 1.25%/56.1%。\n" +
        "   太低说明还在超卖磨底，中位说明动能起来了但没超买。所以只要求K>D，不额外要求低位。\n\n" +
        "──────── 量比分档 ────────\n" +
        "  缩量<0.8      平均4.63%  胜率73.2%   ← 最优\n" +
        "  平量0.8~1.2   平均1.64%  胜率58.3%\n" +
        "  放量>1.2      平均-0.07% 胜率49.4%   ← 已被市场发现，太晚了\n\n" +
        "──────── 为什么牛市那侧没有做得更严 ────────\n" +
        "回测显示牛市里改用「确认型」（MACD已转正 + 收盘站上MA5）能到 5.17%/胜率75.9%，\n" +
        "但那只有 116 个样本，且在该环境下命中率仅 0.41%——等于牛市里工具几乎不出信号。\n" +
        "2026-08-07 与用户确认：只改熊市分支，牛市保持原样。用户的判断是\n" +
        "「牛市可能就不用这套方法，毕竟遍地好股」——这和数据也吻合：牛市里中跌档(-0.01%)、\n" +
        "深跌档(0.09%)收益基本为零，只有超跌档(1.81%)还有效，本来就没多少可选的。\n\n" +
        "参数不开放调整：每个阈值都是回测选出来的，随手放宽一个就会落到被证伪的一侧。\n" +
        "⚠ 所有阈值在同一批历史数据上选出，有过拟合成分，需前向跟踪验证。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand ExportCommand { get; }

    public ShortTermTabViewModel(AnalyzerPaths paths, IBarRepository barRepository,
        IFinancialRepository financialRepository, JsonWatchlistStore watchlistStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _financialRepository = financialRepository;
        _watchlistStore = watchlistStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            MessageBox.Show(CriteriaInfoText, "短线法 — 分析条件说明", MessageBoxButton.OK, MessageBoxImage.Information));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "短线法", Granularity.Day);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        ExportCommand = new RelayCommand(_ =>
            GridExporter.ExportResults("短线法", Results, includeScore: true, scoreHeader: "低于MA20%"));
    }

    private void Log(string message) => LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>上证收盘 vs MA60——**决定用哪个分支的入场时机条件**，不只是提示。
    /// 返回 (是否在MA60上方, 显示文字)；本地缺指数日线时按"上方"处理（较保守的那个分支）。</summary>
    private (bool AboveMa60, string Text) ComputeMarketState()
    {
        var bars = _barRepository.Query("sh000001", Granularity.Day);
        if (bars.Count < 60) return (true, "⚠ 无法计算大盘状态（本地缺上证指数日线），按大盘>MA60分支处理");
        int i = bars.Count - 1;
        double ma60 = 0;
        for (int t = i - 59; t <= i; t++) ma60 += bars[t].Close;
        ma60 /= 60;
        double close = bars[i].Close;
        bool above = close > ma60;
        var text = $"大盘（{bars[i].PeriodStart:yyyy-MM-dd}）：上证 {close:F2}，MA60 {ma60:F2}，" +
                   $"位于MA60{(above ? "上方" : "下方")} {Math.Abs(close / ma60 - 1) * 100:F2}% → " +
                   (above
                       ? "启用【大盘>MA60分支】：第7条只要求 MA5 拐头向上，不限价格位置（回测 3.29%/胜率66.6%）"
                       : "启用【大盘<MA60分支·提前埋伏】：第7条额外要求 收盘仍低于MA5（回测 4.62%/胜率73.2%）");
        return (above, text);
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

            var (aboveMa60, stateText) = ComputeMarketState();
            MarketStateText = stateText;
            Log(stateText);

            ProgressText = "正在载入财务数据…";
            var financials = await Task.Run(() => _financialRepository.GetLatestSnapshotByCode());
            var annualProfits = await Task.Run(() => _financialRepository.GetRecentAnnualNetProfitByCode(3));
            Log($"已载入 {financials.Count} 只股票的财务快照、{annualProfits.Count} 只的近3年年报净利");

            var names = SqliteStockMetaUpsert.GetAll(_paths.TotalDb).ToDictionary(s => s.Code, s => s.Name);
            var engine = new ShortTermAnalysisEngine(_barRepository, financials, annualProfits, aboveMa60);
            int errorCount = 0, noFinancialCount = 0;
            var passed = new List<StockScreenResult>();

            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    // ETF/指数/板块没有财报；科创板与北交所不在回测样本内，结论不能外推过去。
                    if (!names.TryGetValue(code, out var name)) continue;
                    if (code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("gn_") ||
                        code.StartsWith("new_") || code.StartsWith("dy_")) continue;
                    // 北交所有两套代码段：老的 8x/4x，以及后来启用的 920xxx——只排前者会漏掉
                    // 库里336个92开头的票（2026-08-13 修：新赣江920367就是这么混进结果的）。
                    if (code.StartsWith("688") || code.StartsWith("8") || code.StartsWith("4")
                        || code.StartsWith("92")) continue;
                    if (name.Contains("ST") || name.StartsWith("退")) continue;

                    if (i % 100 == 0) ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code, name);

                    if (result.Error != null) { errorCount++; continue; }
                    if (result.Criteria.Any(c => c.DataMissing)) noFinancialCount++;
                    if (!result.Passed) continue;
                    passed.Add(result);
                }
            });

            // 严格组在前、次优组在后，各自按跌幅降序（两组的历史表现不同，不能混排）
            foreach (var r in passed.Where(r => r.Category == ShortTermAnalysisEngine.CategoryStrict)
                                    .OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(ResultRowViewModel.From(r));
            foreach (var r in passed.Where(r => r.Category == ShortTermAnalysisEngine.CategoryNearMiss)
                                    .OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(ResultRowViewModel.From(r));

            int strictCount = passed.Count(r => r.Category == ShortTermAnalysisEngine.CategoryStrict);
            int nearCount = passed.Count - strictCount;
            Log($"分析完成，共扫描 {codes.Count} 个代码（已排除ETF/指数/ST/科创板/北交所），" +
                $"严格组 {strictCount} 只（8条全过，回测4.62%/胜率73.2%）、" +
                $"次优组 {nearCount} 只（只差入场时机，价格已站上MA5，回测3.29%/胜率66.6%）" +
                (errorCount > 0 ? $"，{errorCount} 只因历史数据不足被跳过" : "") +
                (noFinancialCount > 0 ? $"，{noFinancialCount} 只缺财务数据（该条被跳过，其余正常判断）" : ""));
            if (strictCount == 0 && nearCount > 0)
                Log("今日没有严格组信号——\"收盘仍低于MA5\"是个窄窗口（要求今天比5天前高、但仍低于近5日均价），" +
                    "普涨日常常一只都没有。次优组仍是正期望，可以看，但要知道埋伏窗口已经过了。");

            if (passed.Count > 0)
            {
                int mid = Results.Count(r => r.SortScore is >= 3 and < 8);
                int deep = Results.Count(r => r.SortScore is >= 8 and < 15);
                int veryDeep = Results.Count(r => r.SortScore >= 15);
                Log($"按跌幅分档：中跌(3~8%) {mid} 只 · 深跌(8~15%) {deep} 只 · 超跌(15~25%) {veryDeep} 只");
                int highVol = Results.Count(r => r.Result.Criteria
                    .Any(c => c.Name.Contains("日均波幅") && c.Basis.Contains("⚠")));
                if (highVol > 0)
                    Log($"⚠ 其中 {highVol} 只日均波幅超过4.5%（该档回测0.03%/胜率50.2%，等于随机）——" +
                        "波幅这条现在只提示不拦截，点\"条件详情\"能看到具体数值，建议减半仓位或跳过。");
                Log("这8条同时满足是很苛刻的组合（带波幅条件时回测命中率约8.6%），" +
                    "所以正常市场下每天只有个位数结果，为0也是正常的——宁可没信号，不要放宽条件。");
            }
            else
            {
                Log("今日无符合条件的股票。这套条件命中率很低，没信号是常态，不要因此放宽阈值。");
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
