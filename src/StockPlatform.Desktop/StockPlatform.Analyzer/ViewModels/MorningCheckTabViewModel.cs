using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// "每日晨检"里一行大盘指数的红绿灯状态。所有字段构造时算好，界面只读展示。
/// </summary>
public class IndexLightRowViewModel
{
    public string Name { get; }
    public string CloseText { get; } = "—";
    public string Ma20Text { get; } = "—";
    public string Ma60Text { get; } = "—";
    public string StateText { get; } = "数据不足";
    public Brush StateColor { get; } = Brushes.Gray;

    /// <summary>收盘是否站上MA60；数据不足60根时为 null（总开关判定时按"线下"保守处理）。</summary>
    public bool? AboveMa60 { get; }

    public IndexLightRowViewModel(string name, List<Bar> bars)
    {
        Name = name;
        if (bars.Count == 0) return;

        var last = bars[^1];
        CloseText = $"{last.Close:F0}（{last.PeriodStart:MM-dd}）";
        if (bars.Count < 60) return;

        double ma20 = bars.Skip(bars.Count - 20).Average(b => b.Close);
        double ma60 = bars.Skip(bars.Count - 60).Average(b => b.Close);
        Ma20Text = $"{ma20:F0} {(last.Close > ma20 ? "线上" : "线下")}";
        Ma60Text = $"{ma60:F0}";
        AboveMa60 = last.Close > ma60;

        // 从最后一根往前找"收盘 vs 自身MA60"状态翻转的那天，得到当前趋势段的起点/长度。
        var closes = bars.Select(b => b.Close).ToArray();
        int segStart = bars.Count - 1;
        for (int i = bars.Count - 1; i >= 60; i--)
        {
            double sum = 0;
            for (int j = i - 59; j <= i; j++) sum += closes[j];
            bool above = closes[i] > sum / 60;
            if (above != AboveMa60) break;
            segStart = i;
        }
        int days = bars.Count - segStart;
        StateText = AboveMa60 == true
            ? $"线上✅（自{bars[segStart].PeriodStart:MM-dd}起，{days}个交易日）"
            : $"线下❌（自{bars[segStart].PeriodStart:MM-dd}起，{days}个交易日）";
        StateColor = AboveMa60 == true ? Brushes.Firebrick : Brushes.Green; // 涨红跌绿的看盘习惯
    }
}

/// <summary>
/// "每日晨检"里一行自选股的体检结果——按已回测验证的纪律逐条检查：
/// ① 距基准日后最高收盘回撤≥15% → 止损纪律；② 较基准价涨幅≥50% → 减仓1/3止盈纪律；
/// ③ 跌破自身MA60 → 趋势转弱不加仓；④ 最新股东户数环比暴增(>+20%) → 筹码分散警示。
/// 多条同时命中时全部列出，颜色取最severe的一条。
///
/// "持仓 vs 观察"（2026-07-29新增）：自选股Tab里手动填了买入价的算真实持仓——基准=买入价/买入日，
/// 止损/止盈纪律生效，有股数还给出盈亏金额；没填的只是观察中——基准=自选价/自选日，止损/止盈
/// 不触发（没持有就没什么可减仓的），只做趋势/筹码提示。
/// </summary>
public class MorningStockRowViewModel
{
    public string Code { get; }
    public string Name { get; }
    /// <summary>方法列显示文本——同一只票被多个方法选中时是合并后的"金叉法、短线法"。</summary>
    public string Method { get; }
    /// <summary>拆开的来源方法清单（去重前每条自选记录的方法）——给方法列表头的过滤器做包含匹配用，
    /// 不能拿 <see cref="Method"/> 的合并字符串做相等比较（那样"金叉法、短线法"选"金叉法"会漏掉）。</summary>
    public IReadOnlyList<string> Methods { get; }
    public string StatusText { get; }
    public Brush StatusColor { get; }
    public string DataDate { get; }
    public string LatestCloseText { get; private set; } = "—";
    public string Ma60Text { get; private set; } = "—";
    public string SincePickText { get; private set; } = "—";
    public Brush SincePickColor { get; private set; } = Brushes.Gray;
    /// <summary>"较基准涨跌"的数值形式（持仓=较买入价，观察=较自选日收盘）——给汇总里"该方法整体
    /// 平均涨跌/胜率"用，方法过滤后这几个数字就是各选股方法的横向对比口径。</summary>
    public double? SinceBasisPct { get; private set; }
    public string DrawdownText { get; private set; } = "—";
    public string PnlText { get; private set; } = "—";
    public Brush PnlColor { get; private set; } = Brushes.Gray;
    /// <summary>纪律参考价——持仓：止损线(买入后最高收盘×0.85)和止盈线(买入价×1.5)；观察：参考
    /// 买点(线下=站上MA60的价位、线上=回踩MA20位置)。机械推导自回测过的纪律，不是预测。</summary>
    public string AdviceText { get; private set; } = "—";
    public string HolderChangeText { get; private set; } = "—";
    public string ActionText { get; private set; } = "数据不足";
    public Brush ActionColor { get; private set; } = Brushes.Gray;

    /// <summary>是否真实持仓中（填过买入价且还没填卖出价）——止损/止盈纪律只对它生效。</summary>
    public bool IsHolding { get; }

    /// <summary>是否已平仓（买入价、卖出价都填了）——交易已结束，回到观察语义，但状态列单独标出，
    /// 持仓盈亏列显示最终已实现结果，作为交易留痕。</summary>
    public bool IsClosed { get; }

    /// <summary>已平仓交易的已实现收益率文本（如"+0.75%"），供汇总里的交易留痕行使用；非已平仓为空。</summary>
    public string RealizedText { get; private set; } = "";

    /// <summary>排序权重：0=止损 1=止盈 2=筹码警示 3=趋势弱 4=正常，问题最严重的排最前。</summary>
    public int Severity { get; private set; } = 4;

    /// <param name="methods">该股的全部来源方法（同一只票可能被多个方法各加过一条自选记录）——显示时
    /// 合并成"耀哥法、阶梯低点法"，同时原样留一份给方法过滤器做包含匹配。体检基准（自选日期/价格）
    /// 用最早那条记录（entry）：最早的峰值最高，止损纪律触发得最保守。</param>
    public MorningStockRowViewModel(WatchlistEntry entry, IReadOnlyList<string> methods, IBarRepository barRepository, IShareholderRepository shareholderRepository)
    {
        Code = entry.Code;
        Name = entry.Name;
        Methods = methods;
        Method = string.Join("、", methods);
        IsClosed = entry.BuyPrice is > 0 && entry.SellPrice is > 0;
        IsHolding = entry.BuyPrice is > 0 && !IsClosed;
        (StatusText, StatusColor) = IsHolding ? ("持仓", (Brush)Brushes.Firebrick)
                                  : IsClosed ? ("已平仓", Brushes.SteelBlue)
                                  : ("观察", Brushes.Gray);
        var basisDate = IsHolding ? (entry.BuyDate ?? entry.DataDate) : entry.DataDate;
        DataDate = basisDate.ToString("yyyy-MM-dd") + (IsHolding ? "买" : "");
        Compute(entry, basisDate, barRepository, shareholderRepository);
    }

    private void Compute(WatchlistEntry entry, DateTime basisDate, IBarRepository barRepository, IShareholderRepository shareholderRepository)
    {
        var bars = barRepository.Query(entry.Code, Granularity.Day);
        if (bars.Count == 0) return;

        var last = bars[^1];
        LatestCloseText = $"{last.Close:F2}（{last.PeriodStart:MM-dd}）";

        bool? above60 = null;
        double ma60 = 0, ma20 = 0;
        if (bars.Count >= 60)
        {
            ma60 = bars.Skip(bars.Count - 60).Average(b => b.Close);
            ma20 = bars.Skip(bars.Count - 20).Average(b => b.Close);
            above60 = last.Close > ma60;
            Ma60Text = above60 == true ? "线上✅" : "线下❌";
        }

        // 涨跌基准：持仓=买入价，观察/已平仓=自选那天的收盘价。
        double basisPrice = IsHolding ? entry.BuyPrice!.Value : entry.PriceAtPick;
        double? sinceBasisPct = null;
        if (basisPrice > 0)
        {
            sinceBasisPct = (last.Close - basisPrice) / basisPrice * 100;
            SinceBasisPct = sinceBasisPct;
            SincePickText = $"{(sinceBasisPct >= 0 ? "+" : "")}{sinceBasisPct:F1}%";
            SincePickColor = sinceBasisPct >= 0 ? Brushes.Red : Brushes.Green;
        }

        if (IsHolding)
        {
            PnlText = SincePickText;
            if (entry.Shares is > 0 && basisPrice > 0)
            {
                var pnl = (last.Close - basisPrice) * entry.Shares.Value;
                PnlText = $"{(pnl >= 0 ? "+" : "")}{pnl:N0}元（{SincePickText}）";
            }
            PnlColor = SincePickColor;
        }
        else if (IsClosed)
        {
            // 已平仓：显示按卖出价锁定的最终结果，作为交易留痕。
            var spct = (entry.SellPrice!.Value - entry.BuyPrice!.Value) / entry.BuyPrice.Value * 100;
            RealizedText = $"{(spct >= 0 ? "+" : "")}{spct:F2}%";
            PnlText = entry.Shares is > 0
                ? $"已平仓 {(entry.SellPrice.Value - entry.BuyPrice.Value) * entry.Shares.Value:+#,0;-#,0}元（{RealizedText}）"
                : $"已平仓 {RealizedText}";
            PnlColor = spct >= 0 ? Brushes.Red : Brushes.Green;
        }

        // 距基准日后最高收盘的回撤——15%止损纪律看的是这个（回测里对集中持仓有效的口径）。
        double? drawdownPct = null;
        double peakSinceBasis = 0;
        var sinceBasis = bars.Where(b => b.PeriodStart >= basisDate).ToList();
        if (sinceBasis.Count > 0)
        {
            peakSinceBasis = sinceBasis.Max(b => b.Close);
            if (peakSinceBasis > 0)
            {
                drawdownPct = (last.Close - peakSinceBasis) / peakSinceBasis * 100;
                DrawdownText = $"{drawdownPct:F1}%";
            }
        }

        // 纪律参考价：持仓给"建议卖出价"（止损线/止盈线），观察给"建议买入价"（右侧确认位）。
        // 全部是纪律的机械换算，跟"今日动作"同一套规则，只是把触发条件翻译成价格。
        if (IsHolding && basisPrice > 0 && peakSinceBasis > 0)
        {
            var stopLine = peakSinceBasis * 0.85;
            var trimLine = basisPrice * 1.5;
            AdviceText = $"止损≤{stopLine:F2}{(last.Close <= stopLine ? "(已触发)" : "")}，止盈≥{trimLine:F2}";
        }
        else if (!IsHolding && bars.Count >= 60)
        {
            AdviceText = above60 == false
                ? $"站上60日线({ma60:F2})再考虑买入"
                : $"回踩MA20({ma20:F2})附近为参考买点";
        }

        // 最新一期股东户数环比（两期间隔不足55天视为同一期的补充披露，继续往前找）。
        double? holderChgPct = null;
        var counts = shareholderRepository.GetCountSeries(entry.Code);
        if (counts.Count >= 2)
        {
            var cur = counts[^1];
            for (int i = counts.Count - 2; i >= 0; i--)
            {
                if ((cur.ReportDate - counts[i].ReportDate).TotalDays < 55 || counts[i].HolderNum <= 0) continue;
                holderChgPct = (double)(cur.HolderNum - counts[i].HolderNum) / counts[i].HolderNum * 100;
                HolderChangeText = $"{(holderChgPct >= 0 ? "+" : "")}{holderChgPct:F1}%（{cur.ReportDate:MM-dd}期）";
                break;
            }
        }

        var actions = new List<string>();
        // 止损/止盈只对真实持仓生效——观察仓没有可卖的仓位，跌破也只是"这次选中失效"，不发操作指令。
        if (IsHolding && drawdownPct <= -15) { actions.Add($"止损纪律：距买入后高点回撤{-drawdownPct:F0}%，减仓/清仓并复核逻辑"); Severity = Math.Min(Severity, 0); }
        if (IsHolding && sinceBasisPct >= 50) { actions.Add($"止盈纪律：较买入价+{sinceBasisPct:F0}%，减仓1/3锁定利润"); Severity = Math.Min(Severity, 1); }
        if (holderChgPct > 20) { actions.Add($"筹码警示：户数环比+{holderChgPct:F0}%，散户涌入"); Severity = Math.Min(Severity, 2); }
        if (above60 == false) { actions.Add(IsHolding ? "趋势弱：在60日线下，不加仓" : "趋势弱：在60日线下，暂不买入"); Severity = Math.Min(Severity, 3); }

        if (actions.Count == 0)
        {
            ActionText = IsHolding ? "✓ 正常，继续持有" : "✓ 正常，继续观察";
            ActionColor = Brushes.SeaGreen;
        }
        else
        {
            ActionText = string.Join("；", actions);
            ActionColor = Severity switch
            {
                0 => Brushes.Firebrick,
                1 => Brushes.DarkOrange,
                2 => Brushes.DarkOrange,
                _ => Brushes.Gray,
            };
        }
    }
}

/// <summary>方法列表头下拉过滤器的一个选项（2026-07-29新增）——<see cref="Method"/> 为 null 表示"全部方法"。
/// <see cref="Display"/> 带上该方法的股票只数（如"金叉法 (8)"），选之前就能看出各方法各选了多少只，
/// 便于横向对比各方法的表现。</summary>
public class MethodFilterOption
{
    public string? Method { get; init; }
    public string Display { get; init; } = "";
}

/// <summary>
/// "每日晨检" tab —— 把 2026-07 用本地数据回测验证过的几条量化纪律做成每天早上看一眼的
/// 仪表盘（回测结论见 doc/人口变局报告验证与回测分析.pdf 及项目记忆）：
/// ① 大盘总开关：沪深300与创业板指收盘 vs 各自MA60（回测：创业板指3年 +82% vs 买入持有 +50%，
///    回撤 -20% vs -32%），两者都线下=不建新仓；
/// ② 自选股逐只体检：15%回撤止损、+50%减仓1/3、跌破MA60不加仓、股东户数暴增警示；
/// ③ 汇总成"今日行动建议"，早上执行一次，按结果规划当天动作。
/// 只读本地数据不联网；打开程序/切到本Tab/点刷新时重算。
/// 方法列表头带下拉过滤器（2026-07-29新增）：选某个方法后表格与"今日行动建议"都只算该方法选出的股票，
/// 用来横向对比各选股方法的实际表现；体检结果本身在 <see cref="Reload"/> 里一次算好，切换过滤只是筛选，
/// 不重算、不重读数据库。
/// </summary>
public class MorningCheckTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly IBarRepository _barRepository;
    private readonly IShareholderRepository _shareholderRepository;
    private readonly JsonWatchlistStore _watchlistStore;

    public ObservableCollection<IndexLightRowViewModel> IndexRows { get; } = new();
    /// <summary>表格实际显示的行——= <see cref="_allRows"/> 按当前方法过滤后的结果。</summary>
    public ObservableCollection<MorningStockRowViewModel> StockRows { get; } = new();

    /// <summary>本轮体检的全部结果（未过滤），按"持仓优先→严重度→代码"排好序。过滤只从这里筛，
    /// 不重算体检，所以切换方法过滤是瞬时的。</summary>
    private readonly List<MorningStockRowViewModel> _allRows = new();
    /// <summary>本轮自选记录条数（去重前）——汇总里"N 条自选记录按股票去重"那句要用。</summary>
    private int _entryCount;
    /// <summary>本轮大盘总开关的"沪深300/创业板指 有几个在MA60上"——切换过滤要重建汇总，得留着。</summary>
    private int _gateAboveCount;

    /// <summary>方法列表头的过滤选项：第一项固定是"全部方法"，其后是本轮出现过的各方法（带只数）。</summary>
    public ObservableCollection<MethodFilterOption> MethodFilters { get; } = new();

    private MethodFilterOption? _selectedMethodFilter;
    /// <summary>当前选中的方法过滤项。切换时只做筛选+重建汇总，不重读数据库。</summary>
    public MethodFilterOption? SelectedMethodFilter
    {
        get => _selectedMethodFilter;
        set
        {
            if (ReferenceEquals(_selectedMethodFilter, value)) return;
            _selectedMethodFilter = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedMethodFilter)));
            ApplyMethodFilter();
        }
    }

    private string _dataDateText = "尚未体检——点右上角【刷新】按钮开始（不在启动时自动跑，避免拖慢开程序）";
    public string DataDateText { get => _dataDateText; set => Set(ref _dataDateText, value); }

    private string _gateText = "";
    public string GateText { get => _gateText; set => Set(ref _gateText, value); }

    private Brush _gateColor = Brushes.Gray;
    public Brush GateColor { get => _gateColor; set => Set(ref _gateColor, value); }

    private string _summaryText = "";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ShowCriteriaInfoCommand { get; }

    public MorningCheckTabViewModel(IBarRepository barRepository, IShareholderRepository shareholderRepository, JsonWatchlistStore watchlistStore)
    {
        _barRepository = barRepository;
        _shareholderRepository = shareholderRepository;
        _watchlistStore = watchlistStore;

        RefreshCommand = new RelayCommand(_ => Reload());
        ShowCriteriaInfoCommand = new RelayCommand(_ => System.Windows.MessageBox.Show(
            "每日晨检——把回测验证过的量化纪律做成早上看一眼的仪表盘\n" +
            "（依据：2026-07 用本地2023-2026全市场数据的因子检验与《人口变局》报告组合回测）\n\n" +
            "一、大盘总开关（最有效的一条规则）\n" +
            "   沪深300 与 创业板指 的收盘价是否站上各自60日均线：\n" +
            "   · 都线上 → 开启：可正常建仓\n" +
            "   · 只有一个线上 → 半开：谨慎、轻仓\n" +
            "   · 都线下 → 关闭：不建新仓、逐步降仓，等重新站上再回来\n" +
            "   回测：创业板指3年 +82% vs 买入持有 +50%，最大回撤 -20% vs -32%\n\n" +
            "二、自选股逐只体检（按严重度排序；持仓排在观察前面）\n" +
            "   持仓 / 观察 / 已平仓：在自选股Tab给某只票填了\"买入价\"就算真实持仓（基准=买入价/买入日，\n" +
            "   有股数还显示盈亏金额）；没填=观察仓（基准=自选价/自选日，止损止盈不触发）；\n" +
            "   买入价+卖出价都填了=已平仓（显示最终已实现盈亏，作为交易留痕沉淀，供复盘纪律执行情况）。\n" +
            "   参考价列：持仓给建议卖出价=止损线(买入后最高收盘×0.85)和止盈线(买入价×1.5)；\n" +
            "   观察给建议买入价=线下为\"站上60日线的价位\"、线上为\"回踩MA20位置\"。这些是纪律的机械\n" +
            "   换算（把触发条件翻译成价格），不是对股价的预测。\n" +
            "   1. 止损纪律[仅持仓]：距买入后最高收盘回撤≥15% → 减仓/清仓并复核逻辑\n" +
            "      （只适用于个股/集中持仓；对指数、分散组合用MA60退出，别用固定止损）\n" +
            "   2. 止盈纪律[仅持仓]：较买入价涨幅≥50% → 减仓1/3锁定利润\n" +
            "   3. 筹码警示：最新股东户数环比>+20% → 散户涌入，几乎每期回测垫底组\n" +
            "      （户数只用于排雷，不用于选股——检验显示\"户数下降\"没有选股超额）\n" +
            "   4. 趋势弱：收盘在自身60日线下 → 持仓不加仓 / 观察暂不买入（月均落后线上组0.6pp）\n\n" +
            "三、使用节奏\n" +
            "   每天早上开盘前：先\"从服务端更新数据\"，再看本Tab → 按\"今日行动建议\"执行。\n" +
            "   规则参数（MA60/15%/50%/20%）为回测原值，请勿为了历史好看微调——那是过拟合。\n" +
            "   规则保质期约6个月，到期应重新回测复核。",
            "每日晨检——规则说明"));

        // 首次体检不在构造时同步跑（会阻塞窗口显示，双击后半天不出界面）——改由 MainWindow.Loaded
        // 后用后台优先级触发（见 MainWindow 构造函数），先把窗口显示出来、再填充体检结果。
    }

    /// <summary>Public so MainWindow can call it when the user switches to this tab（同自选股Tab的
    /// 做法）——本Session里新加的自选、或刚"从服务端更新数据"后，切过来直接是最新结果。</summary>
    public void Reload()
    {
        IndexRows.Clear();
        StockRows.Clear();

        var latest = _barRepository.GetOverallLatestPeriodStart(Granularity.Day);
        DataDateText = latest == null
            ? "本地还没有K线数据——先点上方\"从服务端更新数据\""
            : $"数据日期：{latest:yyyy-MM-dd}";

        // ── 大盘红绿灯（目录里的6个指数都展示；总开关只看沪深300+创业板指） ──
        bool? csi300Above = null, chinextAbove = null;
        foreach (var (symbol, name) in MarketIndexCatalog.All)
        {
            var row = new IndexLightRowViewModel(name, _barRepository.Query(symbol, Granularity.Day));
            IndexRows.Add(row);
            if (symbol == "sh000300") csi300Above = row.AboveMa60;
            if (symbol == "sz399006") chinextAbove = row.AboveMa60;
        }

        _gateAboveCount = (csi300Above == true ? 1 : 0) + (chinextAbove == true ? 1 : 0);
        (GateText, GateColor) = _gateAboveCount switch
        {
            2 => ("🟢 总开关：开启——沪深300、创业板指均在60日线上，可正常建仓", Brushes.SeaGreen),
            1 => ("🟡 总开关：半开——沪深300/创业板指只有一个在60日线上，谨慎、轻仓", Brushes.DarkOrange),
            _ => ("🔴 总开关：关闭——沪深300、创业板指均跌破60日线，不建新仓、逐步降仓", Brushes.Firebrick),
        };

        // ── 自选股体检（问题最严重的排最前） ──
        // 同一只票可能被多个方法各加过一条自选记录——按股票去重，一只票只体检/列出一次：
        // 填过买入价（真实持仓）的记录优先作基准（止损/止盈要按真实成本算），否则用最早那条
        // 记录（最早的峰值最高，止损触发最保守）；方法列合并展示所有来源。
        var entries = _watchlistStore.Load();
        _entryCount = entries.Count;
        _allRows.Clear();
        _allRows.AddRange(entries
            .GroupBy(e => e.Code)
            .Select(g =>
            {
                var basis = g.OrderByDescending(e => e.BuyPrice is > 0 && e.SellPrice is not (> 0)) // 持仓中优先
                             .ThenByDescending(e => e.BuyPrice is > 0)                              // 其次已平仓（留痕）
                             .ThenBy(e => e.BuyDate ?? DateTime.MaxValue)
                             .ThenBy(e => e.DataDate).ThenBy(e => e.AddedAt).First();
                var methods = g.Select(e => e.Method).Distinct().ToList();
                return new MorningStockRowViewModel(basis, methods, _barRepository, _shareholderRepository);
            })
            .OrderByDescending(r => r.IsHolding)   // 持仓排在观察前面——真金白银的先看
            .ThenBy(r => r.Severity).ThenBy(r => r.Code));

        RebuildMethodFilters();
        ApplyMethodFilter();
    }

    /// <summary>重建方法列表头的过滤下拉项（"全部方法" + 本轮出现过的各方法，都带只数）——刷新后
    /// 尽量保留用户当前选中的方法（选项对象会重建，靠方法名重新对上），那个方法本轮没有了就退回"全部"。</summary>
    private void RebuildMethodFilters()
    {
        var keep = _selectedMethodFilter?.Method;
        var counts = _allRows
            .SelectMany(r => r.Methods)
            .GroupBy(m => m)
            .ToDictionary(g => g.Key, g => g.Count());

        MethodFilters.Clear();
        MethodFilters.Add(new MethodFilterOption { Method = null, Display = $"全部方法 ({_allRows.Count})" });
        foreach (var kv in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            MethodFilters.Add(new MethodFilterOption { Method = kv.Key, Display = $"{kv.Key} ({kv.Value})" });

        // 直接改字段、不走属性 setter——这里不该触发 ApplyMethodFilter（调用方紧接着就会调一次）。
        _selectedMethodFilter = MethodFilters.FirstOrDefault(o => o.Method == keep) ?? MethodFilters[0];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedMethodFilter)));
    }

    /// <summary>按当前选中的方法筛选表格并重建汇总——体检结果已在 <see cref="Reload"/> 里算好，这里
    /// 只做筛选，所以切换过滤是瞬时的、不重读数据库。</summary>
    private void ApplyMethodFilter()
    {
        var method = _selectedMethodFilter?.Method;
        var rows = method == null
            ? _allRows
            : _allRows.Where(r => r.Methods.Contains(method)).ToList();

        StockRows.Clear();
        foreach (var r in rows) StockRows.Add(r);
        BuildSummary(_gateAboveCount, rows, _entryCount, method);
    }

    /// <param name="methodFilter">当前生效的方法过滤（null=全部）——过滤生效时汇总只统计该方法选出的
    /// 股票，并在标题上标出来，这样"按方法对比表现"看到的数字和表格是一致的。</param>
    private void BuildSummary(int gateAboveCount, IReadOnlyList<MorningStockRowViewModel> rows, int entryCount, string? methodFilter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(gateAboveCount switch
        {
            2 => "1) 大盘总开关开启 → 今天可以正常建仓；从自选里优先看\"✓正常\"且站上60日线的标的。",
            1 => "1) 大盘总开关半开 → 只轻仓试错，不重仓；等两个指数都站上60日线再正常建仓。",
            _ => "1) 大盘总开关关闭 → 今天不建新仓；已有仓位逢反弹降低，等指数重新站上60日线。",
        });

        if (rows.Count == 0)
        {
            sb.AppendLine(methodFilter == null
                ? "2) 自选股为空——先在各选股Tab里勾选\"加入自选\"，晨检会每天逐只体检。"
                : $"2) 【仅方法：{methodFilter}】该方法名下暂无自选股——把方法列表头的下拉切回\"全部方法\"看全部。");
        }
        else
        {
            var stop = rows.Where(r => r.Severity == 0).Select(r => r.Name).ToList();
            var trim = rows.Where(r => r.Severity == 1).Select(r => r.Name).ToList();
            var chip = rows.Where(r => r.Severity == 2).Select(r => r.Name).ToList();
            var weakHold = rows.Where(r => r.Severity == 3 && r.IsHolding).Select(r => r.Name).ToList();
            var weakWatch = rows.Where(r => r.Severity == 3 && !r.IsHolding && !r.IsClosed).Select(r => r.Name).ToList();
            var okHold = rows.Where(r => r.Severity == 4 && r.IsHolding).Select(r => r.Name).ToList();
            var okWatch = rows.Where(r => r.Severity == 4 && !r.IsHolding && !r.IsClosed).Select(r => r.Name).ToList();
            var closed = rows.Where(r => r.IsClosed).Select(r => $"{r.Name}({r.RealizedText})").ToList();
            int holding = rows.Count(r => r.IsHolding);
            // 去重说明只在"全部方法 + 真有重复记录"时展示：过滤后拿总记录数跟子集比是没有意义的。
            var dedupNote = methodFilter == null && entryCount != rows.Count ? $"，{entryCount} 条自选记录按股票去重" : "";
            var filterNote = methodFilter == null ? "" : $"【仅方法：{methodFilter}】";
            sb.AppendLine($"2) {filterNote}自选股 {rows.Count} 只体检结果（持仓 {holding} 只 / 观察 {rows.Count - holding - closed.Count} 只 / 已平仓 {closed.Count} 只{dedupNote}）：");

            // 方法横向对比用的整体口径：平均"较基准涨跌"+上涨占比（持仓算较买入价、观察算较自选日收盘）。
            var pcts = rows.Where(r => r.SinceBasisPct.HasValue).Select(r => r.SinceBasisPct!.Value).ToList();
            if (pcts.Count > 0)
            {
                var avg = pcts.Average();
                int up = pcts.Count(p => p >= 0);
                sb.AppendLine($"   · {(methodFilter == null ? "全部" : methodFilter)}整体：平均较基准 {(avg >= 0 ? "+" : "")}{avg:F1}%，" +
                              $"上涨 {up} 只 / 下跌 {pcts.Count - up} 只（占比 {(double)up / pcts.Count * 100:F0}%）");
            }
            if (stop.Count > 0) sb.AppendLine($"   · [持仓]触发15%回撤止损（最优先处理）：{string.Join("、", stop)}");
            if (trim.Count > 0) sb.AppendLine($"   · [持仓]触发+50%止盈减仓：{string.Join("、", trim)}");
            if (chip.Count > 0) sb.AppendLine($"   · 股东户数暴增警示：{string.Join("、", chip)}");
            if (weakHold.Count > 0) sb.AppendLine($"   · [持仓]60日线下不加仓：{string.Join("、", weakHold)}");
            if (weakWatch.Count > 0) sb.AppendLine($"   · [观察]60日线下暂不买入：{string.Join("、", weakWatch)}");
            if (okHold.Count > 0) sb.AppendLine($"   · [持仓]正常继续持有：{string.Join("、", okHold)}");
            if (okWatch.Count > 0) sb.AppendLine($"   · [观察]正常（开关重开后优先买入候选）：{string.Join("、", okWatch)}");
            if (closed.Count > 0) sb.AppendLine($"   · 已平仓交易留痕：{string.Join("、", closed)}");
            if (holding == 0) sb.AppendLine("   （提示：买入后到自选股Tab把买入日期/买入价/股数填上，止损止盈就按你的真实成本盯）");
        }

        sb.Append("3) 纪律提醒：止损/止盈今天就执行，不等\"再看一天\"；参数不微调；每季度重新回测一次规则。");
        SummaryText = sb.ToString();
    }
}
