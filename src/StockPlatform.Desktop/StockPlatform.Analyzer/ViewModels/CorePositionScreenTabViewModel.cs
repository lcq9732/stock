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

/// <summary>
/// "底仓法" tab（2026-08-20 由"回调法"改造而来）—— 见 <see cref="CorePositionAnalysisEngine"/>。
/// 跟其它筛选页不同的三点：
///
/// ① **结果按股息率降序**，不是按位置/跌幅。底仓比的是现金流。
/// ② **不显示大盘MA60状态**。原回调法顶部有那一行，因为它带止损、大盘跌破MA60时表现明显变差。
///    底仓不择时也没有止损——它按股息率分档买、靠持有时间和分红赚钱，摆个大盘状态在那里只会
///    诱导择时。要看大盘状态去短线法页。
/// ③ 多一个"目标年化股息"输入——把"想每年收多少分红"换算成"这只票要投多少钱"，跟【底仓】页
///    的同名设置是同一个数。
///
/// ⚠ 本方法**没有回测支持**，条件是按"拿分红"的目的推的。理由和验证方案见引擎的类注释——
/// 界面上的条件说明里也照实写了，不能让它看起来跟短线法那种有回测表的方法一个证据等级。
/// </summary>
public class CorePositionScreenTabViewModel : INotifyPropertyChanged
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
    private readonly IDividendRepository _dividendRepository;
    private readonly JsonWatchlistStore _watchlistStore;
    private readonly JsonCorePositionStore _corePositionStore;

    /// <summary>"加入底仓"之后要通知【底仓】页重新加载——由 MainViewModel 注入（同交易池那对页）。</summary>
    public Action? CorePositionsChanged { get; set; }

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<ResultRowViewModel> Results { get; } = new();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; set => Set(ref _progressText, value); }

    /// <summary>目标年化股息（元，税前）——不参与筛选，只做"需投入多少"的换算。留空则不换算。</summary>
    private string _targetDividendText = "30000";
    public string TargetDividendText { get => _targetDividendText; set => Set(ref _targetDividendText, value); }

    public string CriteriaInfoText =>
        "底仓法 — 筛选能长期拿着吃分红的股票（固定用日线，8条硬条件全满足才入选）\n\n" +
        "════════════ ⚠ 先说证据等级：本方法没有回测支持 ════════════\n\n" +
        "本项目其它方法（短线法、阶梯低点法等）每个阈值背后都有一份回测表，底仓法**没有**。\n" +
        "原因是口径不同：那些回测的口径是「+10%止盈 / -10%止损 / 最长持有120日」，而底仓的\n" +
        "持有期是**数年**，差一个量级，那批结论对这里不成立。\n\n" +
        "下面的阈值是按\"拿分红\"这个目的推出来的，不是拟合出来的。要真正验证需要另做一轮长周期\n" +
        "回测：用后复权日线（前复权在十年尺度上会失真）算价差，加上分红表的实收股息，按持有\n" +
        "1/3/5 年分别统计。这是独立的一个活，还没做。\n\n" +
        "特别注意一条：原回调法的说明里有\"重基本面筛选（ROE/净利增长）反而降低收益\"的结论，\n" +
        "**底仓法不再沿用**——那是 +10%止盈/120日 的短线口径测出来的。持有数年的票不能是连亏\n" +
        "公司，所以\"近3年净利\"在这里从\"仅提示\"升级成了硬条件（第③条）。\n\n" +
        "另外 2026-08-20 去掉了原来的\"一手金额 ≤ 总资产15%\"那条（用户要求）：它防的是茅台\n" +
        "那种高价股，但 4.5% 股息率的池子里现价基本都在个位数到几十元、一手几百到几千块，\n" +
        "这条从来没真正生效过，留着只是白占一个条件位和一个输入框。\n\n" +
        "════════════ 8条硬条件 ════════════\n\n" +
        "【质量】\n" +
        "  ① 净利润为正（最新一期财报，归母净利润 > 0）\n" +
        "  ② 经营现金流为正（赚的是真钱，不是账面利润）\n" +
        "  ③ 近3年归母净利：累计为正 且 最新年度为正   ← 本次由\"仅提示\"升级\n\n" +
        "【可操作性】\n" +
        "  ④ 20日均成交额 ≥ 1亿元（底仓不常动，但真要减仓时得出得去）\n" +
        "\n" +
        "【现金流】\n" +
        "  ⑤ 股息率 ≥ 4.5%（近12个月已实施派息 ÷ 现价）\n" +
        "      ——要跟国债/定存拉开足够差距，拿分红才有意义。持满一年分红还免税。\n" +
        "      代价：池子明显变小、行业高度集中（银行/电力/高速/煤炭为主）。所以结果表里\n" +
        "      一定要看行业分布，别让底仓全压在一两个行业上（运行日志里给出统计）。\n" +
        "  ⑥ 经营现金流 / 净利润 ≥ 1.0（利润要变成真钱，这个分红才发得出来）\n\n" +
        "【拿得住】\n" +
        "  ⑦ 日均波幅(60日) ≤ 1.5%\n" +
        "      ——底仓**没有价格止损**，低波动不是为了控制止损，而是因为要往下分档加仓、\n" +
        "      仓位会放大，天天大幅波动的票拿不住。\n\n" +
        "【可持续】\n" +
        "  ⑧ 连续分红 ≥ 5年（按除权除息日所属年计）   ← 本次新增，最关键的一条\n" +
        "      ——底仓赌的就是\"未来还会不会继续分红\"。只看近12个月的股息率完全分不出\n" +
        "      \"连分十年的电力股\"和\"去年头一回分红、今年就停了\"的票，后者的高股息率纯属巧合。\n" +
        "      5年足以滤掉一次性大额分红，又不会把2020年后上市的优质公司全挡掉。\n" +
        "      算法上\"今年还没到分红季\"不算中断（A股多在4~7月实施上年度分红），\n" +
        "      只有去年和今年都没派息才判定为中断。\n\n" +
        "════════════ 相比原回调法删掉了什么 ════════════\n\n" +
        "· 主动仓那一路判定（低于MA20 3~25%、距一年高点回撤≥10%、波幅≤4.5%）——全部交给\n" +
        "  【短线法】。那套8条硬条件有 89个时点/28020个样本的回测（3.35%/胜率66.8%），\n" +
        "  比原回调法的主动仓判定强，去掉没有功能缺口。\n" +
        "· 两个止盈目标（+2%/+10%）和止损价——底仓不用价格纪律。它的退出条件是\n" +
        "  **分红中断 或 基本面变坏**，不是跌了多少个点。\n" +
        "· 顶部的大盘MA60状态——底仓不择时。摆在那里只会诱导择时。\n\n" +
        "════════════ 只展示、不参与筛选的列 ════════════\n\n" +
        "· 近5年平均股息率 —— 跟当期股息率并排看。当期明显高出一截（超过均值1.5倍标⚠）时，\n" +
        "  多半含一次性大额分红，或者股价刚大跌（分母变小），不是可持续的水平。\n" +
        "· 派息趋势：结果表只给结论（递增/持平/波动/中断）。近5年每股派息的**逐年柱状图**\n" +
        "  在每行的【条件详情】里。底仓最怕的不是股息率低一点，而是派息一年比一年少，\n" +
        "  这件事一行数字看不出来，柱子高低一眼就看出来。\n" +
        "· 近3年净利：结果表只给3年累计。逐年柱状图同样在【条件详情】里。\n" +
        "· 需投入 —— 目标年化股息 ÷ 股息率。这是\"全压这一只\"的口径，实际要除以计划配置的只数。\n\n" +
        "════════════ 关于红利税（财税[2015]101号 / [2012]85号）════════════\n\n" +
        "持股期限 = 买入日 → **卖出日前一日**：\n" +
        "  ≤1个月            全额计入，实际税负 20%\n" +
        "  1个月~1年（含1年） 50%计入，实际税负 10%\n" +
        "  >1年（严格超过）   暂免征收\n\n" +
        "派息时对持股1年以内的**暂不扣缴**（股息全额到账），等卖出时由中登按持股期限算出税额、\n" +
        "券商从资金账户扣收。所以底仓一直不卖、跨过1年，这笔税不仅免掉，期间根本没被扣过。\n" +
        "1年整仍属10%那档，差一天就是 10% vs 0，实操再留几天更稳。\n\n" +
        "⚠ **先进先出**：同一证券账户内先买入的视为先卖出。所以同一只票如果既有底仓又做波段，\n" +
        "卖波段那笔会按 FIFO 认定成卖掉了最早买入的底仓份额，把底仓的免税时钟往后推。\n" +
        "软件里给成交打标记**改变不了**这个认定——要隔开只能靠\"同一只票不两头做\"，\n" +
        "或者底仓和主动仓分开用两个证券账户。\n\n" +
        "扫描范围已排除：ETF/指数/板块、ST与退市股、科创板(688)、北交所(8x/4x/92x)。";

    public RelayCommand AnalyzeCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }
    public RelayCommand AddToWatchlistCommand { get; }
    public RelayCommand AddToCorePositionCommand { get; }
    public RelayCommand ExportCommand { get; }

    public CorePositionScreenTabViewModel(
        AnalyzerPaths paths,
        IBarRepository barRepository,
        IFinancialRepository financialRepository,
        IDividendRepository dividendRepository,
        JsonWatchlistStore watchlistStore,
        JsonCorePositionStore corePositionStore)
    {
        _paths = paths;
        _barRepository = barRepository;
        _financialRepository = financialRepository;
        _dividendRepository = dividendRepository;
        _watchlistStore = watchlistStore;
        _corePositionStore = corePositionStore;

        AnalyzeCommand = new RelayCommand(async _ => await RunAnalyzeAsync(), _ => !IsBusy);
        ShowCriteriaInfoCommand = new RelayCommand(_ =>
            TextDetailWindow.Show("底仓法 — 分析条件说明", "底仓法 — 入选条件与依据说明", CriteriaInfoText));
        AddToWatchlistCommand = new RelayCommand(_ =>
        {
            var added = WatchlistAdder.AddSelected(_watchlistStore, Results, "底仓法", Granularity.Day, lookback: null);
            Log(added > 0 ? $"已将 {added} 只股票加入自选" : "没有勾选股票，或勾选的都已经在自选里了");
        });
        AddToCorePositionCommand = new RelayCommand(_ => AddToCorePosition());
        ExportCommand = new RelayCommand(_ => GridExporter.ExportResults("底仓法", Results, includeTotalCount: true));
    }

    private void Log(string message) => LogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    /// <summary>把勾选的票加进【底仓】页。只建记录（含目标年化股息按只数摊分），成交明细要到那边
    /// 用【录成交】逐档录——这里不代填任何买入，筛选出来 ≠ 已经买了。</summary>
    private void AddToCorePosition()
    {
        var selected = Results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log("没有勾选股票");
            return;
        }

        // 目标年化股息按勾选只数摊分——底仓要分散（高股息池子行业集中度很高），
        // 一只票背全部目标是不现实的，摊分后每只的目标股数才是可执行的数。
        double perStockTarget = 0;
        if (double.TryParse(TargetDividendText, out var total) && total > 0)
            perStockTarget = total / selected.Count;

        var added = _corePositionStore.Add(selected.Select(r => new CorePosition
        {
            Code = r.Code,
            Name = r.Name,
            TargetAnnualDividend = perStockTarget,
            Note = $"{DateTime.Today:yyyy-MM-dd} 由底仓法选入：股息率 {r.DividendYieldText}、" +
                   $"连续分红 {r.ConsecutiveDividendYearsText}、派息{r.DividendTrend}",
        }));

        if (added > 0)
        {
            Log($"已将 {added} 只加入底仓" +
                (perStockTarget > 0 ? $"，每只目标年化股息 {perStockTarget:N0} 元（{total:N0} ÷ {selected.Count} 只）" : "") +
                "。成交明细请到【底仓】页用【录成交】逐档录入。");
            CorePositionsChanged?.Invoke();
        }
        else
        {
            Log("勾选的都已经在底仓里了");
        }
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

            // 目标年化股息可以留空——只影响"需投入"那一列，不影响筛选。
            double targetDividend = 0;
            if (!string.IsNullOrWhiteSpace(TargetDividendText) &&
                (!double.TryParse(TargetDividendText, out targetDividend) || targetDividend < 0))
            {
                Log($"目标年化股息填写有误（{TargetDividendText}），请填正整数或留空，例如 30000");
                return;
            }

            ProgressText = "正在载入财务与分红数据…";
            var financials = await Task.Run(() => _financialRepository.GetLatestSnapshotByCode());
            var dividends = await Task.Run(() =>
                _dividendRepository.GetTrailingCashDividendPerShare(DateTime.Today.AddYears(-1)));
            // 取**全部**历史分红：条件详情里的柱状图要画完整历史（2026-08-20 用户要求），
            // 顺带让"连续分红年数"不再被回看窗口截断——原来只往前取13年，实测有612只显示
            // 满13年、20只14年，明显是被窗口截断的数字而不是真实年数。
            var annualDividends = await Task.Run(() =>
                _dividendRepository.GetAnnualCashDividendPerShare(new DateTime(1990, 1, 1)));
            // 同上取全部历年年报给图用；条件③的判定在引擎内部仍只看最近3年，见 CorePositionAnalysisEngine.ProfitJudgeYears。
            var annualProfits = await Task.Run(() => _financialRepository.GetRecentAnnualNetProfitByCode(int.MaxValue));
            Log($"已载入 {financials.Count} 只股票的财务快照、{dividends.Count} 只的近一年派息、" +
                $"{annualDividends.Count} 只的历年派息、{annualProfits.Count} 只的历年年报净利");

            if (annualDividends.Count == 0)
                Log("⚠ 本地没有任何分红数据——条件⑥⑨会全部按\"缺数据\"跳过，结果没有意义。" +
                    "请先在 Fetcher 里拉取分红送配（一键拉取定期数据，或单独的\"拉取分红\"按钮）。");

            var names = SqliteStockMetaUpsert.GetAll(_paths.CurrentDb).ToDictionary(s => s.Code, s => s.Name);

            var engine = new CorePositionAnalysisEngine(
                _barRepository, financials, dividends, annualDividends, annualProfits)
            {
                TargetAnnualDividend = targetDividend,
            };

            int passedCount = 0, errorCount = 0, missingDataCount = 0;
            var rows = new List<ResultRowViewModel>();
            await Task.Run(() =>
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    var code = codes[i];
                    // ETF/板块指数/指数不参与个股筛选——它们没有财报也没有分红方案。
                    if (!names.TryGetValue(code, out var name)) continue;
                    if (code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("gn_") ||
                        code.StartsWith("new_") || code.StartsWith("dy_")) continue;
                    // 科创板(688)/北交所(8x/4x/92x)排除：跟其它方法保持同一个扫描范围，
                    // 而且这两个板块基本没有稳定高分红的标的。
                    if (code.StartsWith("688") || code.StartsWith("8") || code.StartsWith("4")
                        || code.StartsWith("92")) continue;
                    if (name.Contains("ST") || name.StartsWith("退")) continue;

                    if (i % 100 == 0) ProgressText = $"正在分析 {code} ({i + 1}/{codes.Count})";
                    var result = engine.Analyze(code);
                    result.Name = name;

                    if (result.Error != null) { errorCount++; continue; }
                    if (result.Criteria.Any(c => c.DataMissing)) missingDataCount++;
                    if (!result.Passed) continue;

                    passedCount++;
                    rows.Add(ResultRowViewModel.From(result));
                }
            });

            // 股息率降序——底仓比的是现金流，不是位置。
            foreach (var r in rows.OrderByDescending(r => r.SortScore ?? 0))
                Results.Add(r);

            Log($"分析完成，共扫描 {codes.Count} 个代码（已排除ETF/指数/ST/科创板/北交所），" +
                $"{passedCount} 只全部满足9个条件" +
                (errorCount > 0 ? $"，{errorCount} 只因上市不足一年或数据不足被跳过" : "") +
                (missingDataCount > 0 ? $"，{missingDataCount} 只缺财务/分红数据（缺的那条被跳过，不影响其余条件）" : ""));

            if (passedCount == 0)
            {
                Log("没有一只满足全部条件。股息率≥4.5% 这条本身就很严——在多数市况下入选数量是个位数到几十只，" +
                    "结果为空未必是数据问题，也可能是当前价位下确实没有。可以点 ℹ 看条件说明，" +
                    "或临时把总资产填大一点看看是不是被条件⑤（一手金额）挡住的。");
                return;
            }

            // 行业分布——4.5% 这条门槛会让结果高度集中在银行/电力/高速/煤炭，
            // 不看这个统计就容易把底仓全压在一两个行业上。这是本页最该盯的一条日志。
            var byIndustry = Results
                .GroupBy(r => string.IsNullOrEmpty(r.Board) ? "未分类" : r.Board)
                .OrderByDescending(g => g.Count())
                .ToList();
            Log("行业分布：" + string.Join(" · ", byIndustry.Select(g => $"{g.Key} {g.Count()}只")));
            var top = byIndustry[0];
            if (top.Count() > Results.Count * 0.5 && Results.Count >= 4)
                Log($"⚠ {top.Key} 一个行业就占了 {top.Count()}/{Results.Count} 只。" +
                    "高股息池子天然集中，但底仓要分散到不同行业——同一行业的分红能力往往同时变坏" +
                    "（政策、利率、煤价都是行业级的），全压一个行业等于把\"分红会持续\"这个赌注押在一件事上。");

            // 派息趋势里凡是"波动"的都值得单独看一眼——它过了连续5年这条，但派息金额不稳。
            int wobbly = Results.Count(r => r.DividendTrend.StartsWith("波动"));
            if (wobbly > 0)
                Log($"其中 {wobbly} 只派息趋势为\"波动\"（连续分红没断，但金额有过腰斩）。" +
                    "条件⑨只查\"有没有分\"，查不出\"分多少\"，这几只的股息率可持续性要自己看一眼派息明细。");

            double avgYield = Results.Average(r => r.DividendYield ?? 0);
            Log($"入选股息率区间 {Results.Min(r => r.DividendYield ?? 0) * 100:F2}% ~ " +
                $"{Results.Max(r => r.DividendYield ?? 0) * 100:F2}%，平均 {avgYield * 100:F2}%");
            if (targetDividend > 0 && avgYield > 0)
                Log($"按平均股息率 {avgYield * 100:F2}% 算，要拿到 {targetDividend:N0} 元年化股息，" +
                    $"底仓总投入约需 {targetDividend / avgYield / 1e4:F1} 万元（税前口径）。" +
                    $"分散到 5 只的话每只约 {targetDividend / avgYield / 5 / 1e4:F1} 万元。");
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
