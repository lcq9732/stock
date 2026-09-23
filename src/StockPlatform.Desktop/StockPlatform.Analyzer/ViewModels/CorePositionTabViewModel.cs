using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;   // 临近财报标色用（见 EarningsColor）
using StockPlatform.Desktop.Shared.Theme;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// 【底仓】页的一行（2026-08-20 新增）。
///
/// 跟"主动仓"那页（<see cref="WatchlistRowViewModel"/>）的列**刻意不同**：那边是止亏价、
/// ±10%纪律、持仓盈亏；这边是股息率、连续分红、累计已收股息、免税到期日。底仓的收益来源不是
/// 价差而是分红，用同一套列看它会得出错误结论——见 <see cref="TotalReturnText"/>。
/// </summary>
public class CorePositionRowViewModel : ISelectableRow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public CorePosition Entry { get; }

    private readonly JsonCorePositionStore _store;
    private readonly double? _latestClose;
    private readonly TradeCostSummary _cost;

    /// <summary>近12个月已实施的每股派息（元/股，税前）——股息率和目标股数都靠它。</summary>
    private readonly double _dpsTrailing;

    /// <summary>各笔买入至今累计收到的股息合计（元，税前）。按每笔的买入日分别判有没有权，
    /// 见 <see cref="DividendMetrics.ReceivedPerShare"/>。</summary>
    private readonly double _receivedDividend;

    private readonly int _consecutiveYears;
    private readonly string _dividendTrend;

    // ── 财报披露日（2026-09-01）──────────────────────────────────────────────────
    // 底仓是"吃分红、不设止损"的长期仓，为什么还要盯财报日？
    // 分红方案是**跟着年报一起公布**的：派息变了、连续分红断了，底仓的持有理由本身就动摇了。
    // 这跟主动仓的用法不同——主动仓怕的是财报当天的价格波动，底仓关心的是分红政策会不会变。
    // 数据由 Fetcher 的【拉取财报预约日】抓，这里只读；抓不到就显示"—"。

    /// <summary>抓来的本期预约披露情况；没有则为 null。</summary>
    public StockPlatform.Logic.Models.EarningsScheduleRow? AutoEarnings { get; }

    public string EarningsDateText => AutoEarnings?.EffectiveDate?.ToString("yyyy-MM-dd") ?? "—";

    /// <summary>排序用（没日期的排最后）。</summary>
    public DateTime EarningsSortValue => AutoEarnings?.EffectiveDate ?? DateTime.MaxValue;

    private int? DaysToEarnings => AutoEarnings?.EffectiveDate is { } d
        ? (int)(d - DateTime.Today).TotalDays : null;

    /// <summary>临近财报标个色；已经过去的压成灰（那是**上一次**财报，不是下一次）。
    /// 年报尤其要紧——分红方案跟它一起出。含义同 WatchlistTabViewModel.EarningsColor。</summary>
    public Brush EarningsColor => DaysToEarnings switch
    {
        { } d when d >= 0 && d <= MorningStockRowViewModel.EarningsWarnDays => ThemeBrushes.Firebrick,
        < 0 => ThemeBrushes.Gray,
        _ => ThemeBrushes.Foreground,
    };

    public string EarningsTooltip
    {
        get
        {
            if (AutoEarnings is not { } a || a.EffectiveDate is null)
                return "还不知道下一次财报什么时候披露。\n【拉取财报预约日】每天自动抓，但预约表分期发布——"
                     + "上一期都披露完、下一期还没发布时就是空的。";
            string changed = a.ChangeCount switch
            {
                0 => "",
                1 => $"（改过 1 次，原定 {a.AppointDate:MM-dd}）",
                _ => $"（改过 {a.ChangeCount} 次，原定 {a.AppointDate:MM-dd}）",
            };
            string head = DaysToEarnings switch
            {
                < 0 => $"⚠ 这是**上一次**财报（{a.EffectiveDate:yyyy-MM-dd} 已披露），不是下一次；"
                     + "下一期预约表还没发布",
                0 => "今天披露财报",
                { } d => $"还有 {d} 天披露财报（{a.EffectiveDate:MM-dd}）",
                _ => $"{a.EffectiveDate:yyyy-MM-dd} 披露财报",
            };
            return head + changed
                 + "\n底仓看财报主要是看**分红方案**——它跟年报一起公布。派息缩水或者连续分红中断，"
                 + "持有理由就变了，该重新算一遍股息率和仓位。";
        }
    }

    /// <summary>历年每股派息（升序，全部历史）——【分红历史】按钮画柱状图用。</summary>
    public IReadOnlyList<(int Year, double PerShare)> AnnualDividends { get; }

    /// <summary>每股股息（元/股，税前；已按"滚动12个月 vs 最近完整年度取较小值"修正过）——
    /// 【录成交】窗口拿它把"目标年化股息"换算成目标股数。</summary>
    public double DividendPerShare => _dpsTrailing;

    public CorePositionRowViewModel(
        CorePosition entry,
        JsonCorePositionStore store,
        double? latestClose,
        TradeFeeSettings fees,
        double dpsTrailing,
        IReadOnlyList<DividendRow> dividendRows,
        int consecutiveYears,
        string dividendTrend,
        IReadOnlyList<(int Year, double PerShare)> annualDividends,
        StockPlatform.Logic.Models.EarningsScheduleRow? autoEarnings = null)
    {
        AutoEarnings = autoEarnings;
        Entry = entry;
        _store = store;
        _latestClose = latestClose;
        _cost = TradeCostSummary.For(entry.Lots, fees);
        _dpsTrailing = dpsTrailing;
        _consecutiveYears = consecutiveYears;
        _dividendTrend = dividendTrend;
        AnnualDividends = annualDividends;

        double received = 0;
        foreach (var lot in entry.Lots.Where(l => l.Side == TradeSide.Buy && l.Shares > 0))
            received += DividendMetrics.ReceivedPerShare(dividendRows, lot.Date) * lot.Shares;
        _receivedDividend = received;
    }

    public string Code => Entry.Code;
    public string Name => Entry.Name;
    public string Industry => IndustryClassifier.GetIndustry(Entry.Code);

    // ── 表格里可直接编辑的列（同"主动仓"页那个"财报日✎"的做法：绑定 UpdateSourceTrigger=LostFocus，
    //    setter 里立刻落盘）。2026-08-20 按用户要求只剩"建仓理由"这一个：
    //    · 目标年化股息 → 挪进【录成交】窗口（跟录买入笔数在一起，正好是设定目标的时候）
    //    · 计划档数 → 直接删了，它除了当"2/4档"的分母之外没有任何作用 ──

    /// <summary>建仓理由 / 退出条件——底仓没有价格止损，退出条件是"分红中断"或"基本面变坏"这类
    /// 事件，必须写下来，不然几年后自己都想不起当初为什么买它。</summary>
    public string Note
    {
        get => Entry.Note;
        set
        {
            var text = value ?? "";
            if (text == Entry.Note) return;
            _store.UpdateNote(Entry.Id, text);
            Entry.Note = text;
            Raise();
        }
    }

    public string LatestCloseText => _latestClose is > 0 ? $"{_latestClose.Value:F2}" : "—";

    /// <summary>目标股数——按"目标年化股息 ÷ 每股股息"倒推，向下取整到整手。
    /// 底仓的仓位是按现金流倒推的，不走凯利（凯利要胜率/赔率，底仓不做胜负判断）。</summary>
    public int? TargetShares => Entry.TargetAnnualDividend > 0 && _dpsTrailing > 0
        ? (int)Math.Floor(Entry.TargetAnnualDividend / _dpsTrailing / 100) * 100
        : null;

    /// <summary>
    /// 建仓进度："2,000 / 3,200 股（62%）"——**已买入股数 / 计划买入股数**（2026-08-20 用户要求
    /// 的口径）。计划股数 = 目标年化股息 ÷ 每股股息，在【录成交】窗口里设目标。
    ///
    /// 这一列同时替掉了原来的"持仓"列：分子就是当前持仓股数，有没有建仓看一眼分子就知道，
    /// 不用再单开一列。没设目标年化股息时算不出分母，只显示已买股数。
    /// </summary>
    public string ProgressText
    {
        get
        {
            int held = Entry.RemainingShares;
            if (TargetShares is not > 0)
                return held > 0 ? $"{held:N0} 股（未设目标）" : "未建仓";
            double pct = (double)held / TargetShares.Value * 100;
            return $"{held:N0} / {TargetShares.Value:N0} 股（{pct:F0}%）";
        }
    }

    /// <summary>目标股数的显示文本——**只在【录成交】窗口里用**，不再是表格的一列
    /// （2026-08-20：表格里"目标"列已经把年化股息写了一遍，跟旁边"目标年化股息"列重复）。</summary>
    public string TargetSharesText => TargetShares is > 0
        ? $"{TargetShares.Value:N0} 股"
        : "未设目标";

    // ── 供表格排序用的数值形式（2026-08-19新增）──
    // 这几列显示的是拼好的字符串（"1,500 / 3,000 股（50%）"、"3 年"、"2027-03-05（还198天）"），
    // DataGrid 默认按字符串排会排错（见用户报的股息率问题）。XAML 里用 SortMemberPath 指到这里。

    /// <summary>建仓进度的数值形式：设了目标就用完成百分比，没设目标只能按已买股数排
    /// （两者量纲不同，但没设目标的行本来就显示股数，排序跟着显示走）。</summary>
    public double ProgressValue => TargetShares is > 0
        ? (double)Entry.RemainingShares / TargetShares.Value * 100
        : Entry.RemainingShares;

    /// <summary>含费均价的数值形式。</summary>
    public double? AvgCostValue => _cost.NetAvgCost;

    /// <summary>现价的数值形式。</summary>
    public double? LatestCloseValue => _latestClose;

    /// <summary>连续分红年数的数值形式。</summary>
    public int ConsecutiveYearsValue => _consecutiveYears;

    /// <summary>累计已收股息的数值形式（元）。</summary>
    public double ReceivedDividendValue => _receivedDividend;

    /// <summary>免税日的数值(日期)形式——没买过为 null，排序时排在最后。</summary>
    public DateTime? TaxFreeDateValue =>
        Entry.LastBuyDate is { } last ? DividendMetrics.TaxFreeSellDate(last) : null;

    /// <summary>含费持仓均价（每股实际成本）。</summary>
    public string AvgCostText => _cost.NetAvgCost is { } c ? $"{c:F2}" : "—";

    /// <summary>当前股息率 = 每股股息 ÷ 现价。买入时机的参考。</summary>
    public double? CurrentYield => _latestClose is > 0 && _dpsTrailing > 0 ? _dpsTrailing / _latestClose : null;
    public string CurrentYieldText => CurrentYield.HasValue ? $"{CurrentYield.Value * 100:F2}%" : "—";

    /// <summary>
    /// **成本股息率**（yield on cost）= 每股股息 ÷ 含费持仓均价。
    /// 2026-08-20 按用户要求从表格里撤掉了这一列（没录成交时整列都是"—"），但属性保留：
    /// 顶部汇总的"组合成本股息率"在用它。
    /// 买得越早越便宜，这个数就越高，而且它不随股价波动（分母是你的成本，不是现价）。
    /// 现价股息率回答"现在该不该加仓"，成本股息率回答"我这笔持仓每年实际给我多少回报"。
    /// </summary>
    public double? YieldOnCost => _cost.NetAvgCost is { } c && c > 0 && _dpsTrailing > 0 ? _dpsTrailing / c : null;
    public string YieldOnCostText => YieldOnCost.HasValue ? $"{YieldOnCost.Value * 100:F2}%" : "—";

    /// <summary>按当前持仓算的年化股息（元，税前）。同上：2026-08-20 撤掉了表格列，属性保留
    /// 给顶部汇总的"年化股息合计"用。</summary>
    public double AnnualDividend => _dpsTrailing * Entry.RemainingShares;
    public string AnnualDividendText => AnnualDividend > 0 ? $"{AnnualDividend:N0} 元" : "—";

    public string ConsecutiveYearsText => _consecutiveYears > 0 ? $"{_consecutiveYears} 年" : "无记录";
    public string DividendTrendText => _dividendTrend;

    /// <summary>连续分红中断 / 派息趋势为"中断"——底仓的**退出信号**（它没有价格止损，
    /// 退出条件就是分红中断或基本面变坏）。界面上标红。</summary>
    public bool IsDividendBroken => _consecutiveYears == 0 || _dividendTrend.StartsWith("中断");

    /// <summary>累计已收股息（元，税前）。</summary>
    public string ReceivedDividendText => _receivedDividend > 0 ? $"{_receivedDividend:N0} 元" : "—";

    /// <summary>
    /// 最早可全部免税卖出的日期 = **最晚那笔买入**再加一年零两天。
    ///
    /// 为什么看最晚那笔：分档建仓的每一档免税时钟各自独立起算，整个底仓要全部跨过一年，
    /// 取决于最后补的那一档。已经全部过了就显示"已全部免税"。
    /// </summary>
    public string TaxFreeDateText
    {
        get
        {
            if (Entry.LastBuyDate is not { } last) return "—";
            var free = DividendMetrics.TaxFreeSellDate(last);
            if (free <= DateTime.Today) return "已全部免税";
            int days = (int)(free - DateTime.Today).TotalDays;
            return $"{free:yyyy-MM-dd}（还{days}天）";
        }
    }

    /// <summary>
    /// **总回报 = 价差（含费，按现价全卖）+ 累计已收股息**。
    ///
    /// 这一列跟"主动仓"页的持仓盈亏口径不同，是有意的：除权当天股价下调，底仓的账面浮亏会
    /// 自动变深，但那笔钱已经进了口袋。只看价差的话，一只拿了三年的高股息股会被系统性低估，
    /// 看报表的人会误判底仓在亏钱。
    /// </summary>
    public double? TotalReturn
    {
        get
        {
            if (_latestClose is not > 0) return null;
            var priceDiff = _cost.TotalPnlIfLiquidated(_latestClose.Value);
            if (priceDiff == null) return null;
            return priceDiff.Value + _receivedDividend;
        }
    }

    public string TotalReturnText
    {
        get
        {
            if (TotalReturn is not { } total) return "—";
            var priceDiff = _latestClose is > 0 ? _cost.TotalPnlIfLiquidated(_latestClose.Value) : null;
            double cost = _cost.NetCost;
            string pct = cost > 0 ? $"（{total / cost * 100:+0.00;-0.00}%）" : "";
            return $"{total:N0}{pct}｜价差{priceDiff ?? 0:N0} + 股息{_receivedDividend:N0}";
        }
    }

    public bool IsTotalReturnPositive => TotalReturn is > 0;

    public bool IsSelected { get; set; }

    public void RaiseAll()
    {
        Raise(nameof(ProgressText));
        Raise(nameof(AvgCostText));
        Raise(nameof(YieldOnCostText));
        Raise(nameof(AnnualDividendText));
        Raise(nameof(ReceivedDividendText));
        Raise(nameof(TaxFreeDateText));
        Raise(nameof(TotalReturnText));
        Raise(nameof(Note));
    }
}

/// <summary>
/// 【底仓】页（2026-08-20 新增）—— 记录"用来拿分红、基本不动"的那部分持仓。
///
/// 跟【主动仓】页的分工：那边走短线纪律（±10%、每天要盯），这边是底仓（不看价格、
/// 靠时间和分红）。两套数据存在**不同的文件**里，理由见 AnalyzerPaths.CorePositionPath——
/// 最关键的一条是晨检的短线纪律绝对不能误伤底仓。
///
/// **本页不进晨检**（2026-08-20 用户确认）：晨检那套"跌破MA5就卖 / 20天时间止损 / 15%回撤止损"
/// 对底仓全部不适用。以后若要加，只能加"分红预案变化 / 连续分红中断 / 有档位刚满一年"这三类
/// 事件提醒，不能加任何价格信号。
/// </summary>
public class CorePositionTabViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>连续分红年数要往前看得够远，多取几年留余量（同底仓法筛选页）。</summary>
    private const int DividendHistoryYears = 13;

    private readonly JsonCorePositionStore _store;
    private readonly IBarRepository _barRepository;
    private readonly IDividendRepository _dividendRepository;
    private readonly TradeFeeStore _fees;

    /// <summary>全量底仓记录（表格显示的是按 <see cref="Filter"/> 过滤后的视图）。</summary>
    public ObservableCollection<CorePositionRowViewModel> Entries { get; } = new();

    /// <summary>查询框（2026-09-23新增）——代码/名称/行业/备注；【移出底仓】只作用于显示出来的行，
    /// 顶部汇总仍按全量算（那是整个底仓组合的数，筛过就不是了）。</summary>
    public RowFilter<CorePositionRowViewModel> Filter { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveCommand { get; }

    private string _summaryText = "";
    /// <summary>顶部汇总：总投入、年化股息合计、组合平均成本股息率。</summary>
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

    /// <summary>交易费率——跟"主动仓"页共用一份设置（录成交窗口要用它算含费成本）。</summary>
    public TradeFeeStore FeeStore => _fees;
    public JsonCorePositionStore Store => _store;

    public CorePositionTabViewModel(
        JsonCorePositionStore store,
        IBarRepository barRepository,
        IDividendRepository dividendRepository,
        TradeFeeStore fees)
    {
        _store = store;
        _barRepository = barRepository;
        _dividendRepository = dividendRepository;
        _fees = fees;
        Filter = new RowFilter<CorePositionRowViewModel>(Entries, r => [r.Code, r.Name, r.Industry, r.Note]);
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveCommand = new RelayCommand(_ => RemoveSelected());
        Reload();
    }

    public void Reload()
    {
        Entries.Clear();
        // 财报预约日（每只票取还没披露的最早那期）。抓取归 Fetcher 的【拉取财报预约日】，这里只读。
        var earnings = EarningsLookup.LoadUpcoming();
        // 取全部历史：【分红历史】图要画完整历史，"连续分红年数"也不该被回看窗口截断（2026-08-20）
        var since = new DateTime(1990, 1, 1);

        // 底仓realistically 就几只到十几只，逐只查分红明细完全没问题（不像全市场筛选那样需要批量）。
        foreach (var entry in _store.Load().OrderByDescending(e => e.RemainingShares > 0).ThenBy(e => e.Code))
        {
            var bars = _barRepository.Query(entry.Code, Granularity.Day);
            double? close = bars.Count > 0 ? bars[^1].Close : null;

            var rows = _dividendRepository.GetByCode(entry.Code);

            var byYear = rows
                .Where(r => string.Equals(r.Progress, "实施", StringComparison.Ordinal)
                            && r.DividendYuan > 0 && r.ExDate.HasValue && r.ExDate.Value >= since)
                .GroupBy(r => r.ExDate!.Value.Year)
                .Select(g => (Year: g.Key, PerShare: g.Sum(r => r.DividendYuan) / 10.0))
                .OrderBy(x => x.Year)
                .ToList();

            // 每股股息取"滚动12个月"和"最近一个年度"的较小值——跟底仓法筛选页同一个口径。
            // 滚动12个月窗口在分红季前后会把两个年度的分红框进来、算出近一倍的虚高股息率
            // （2026-08-20 实测华邦健康 10.11% vs 真实约5.6%），而这里算出来的成本股息率和
            // 年化股息是用户拿来做仓位决策的数，虚高一倍会直接导致仓位配错。
            double dpsTrailing = DividendMetrics.ReceivedPerShare(rows, DateTime.Today.AddYears(-1));
            var (_, dpsLatestYear) = DividendMetrics.LatestYearDividend(byYear);
            double dps = dpsLatestYear > 0 ? Math.Min(dpsTrailing, dpsLatestYear) : dpsTrailing;

            int consecutive = DividendMetrics.ConsecutiveYears(byYear, DateTime.Today.Year);
            // 列里只放结论（递增/持平/波动/中断）；逐年明细走【分红历史】的柱状图，同筛选页
            string trend = DividendMetrics.TrendShape(byYear, CorePositionAnalysisEngine.DividendLookbackYears);

            Entries.Add(new CorePositionRowViewModel(
                entry, _store, close, _fees.Current, dps, rows, consecutive, trend, byYear,
                earnings.TryGetValue(entry.Code, out var es) ? es : null));
        }

        UpdateSummary();   // 全量，不看查询框
        Filter.RaiseCounts();
    }

    private void UpdateSummary()
    {
        if (Entries.Count == 0)
        {
            SummaryText = "还没有底仓记录。到【底仓法】页筛选后勾选\"加入底仓\"，或直接在这里新增。";
            return;
        }

        double annual = Entries.Sum(e => e.AnnualDividend);
        double invested = Entries.Sum(e =>
            TradeCostSummary.For(e.Entry.Lots, _fees.Current).NetCost);
        double totalReturn = Entries.Sum(e => e.TotalReturn ?? 0);
        int held = Entries.Count(e => e.Entry.RemainingShares > 0);

        string yieldOnCost = invested > 0 ? $"，组合成本股息率 {annual / invested * 100:F2}%" : "";
        var broken = Entries.Where(e => e.IsDividendBroken && e.Entry.RemainingShares > 0).ToList();

        SummaryText =
            $"底仓 {Entries.Count} 只（已建仓 {held} 只）　总投入 {invested / 1e4:F1} 万（含费）　" +
            $"年化股息 {annual:N0} 元（税前）{yieldOnCost}　" +
            $"总回报 {totalReturn:N0} 元（价差+已收股息）" +
            (broken.Count > 0
                ? $"\n⚠ {string.Join("、", broken.Select(b => b.Name))} 的连续分红已中断——" +
                  "底仓没有价格止损，分红中断就是它的退出信号，该复核了。"
                : "");
    }

    private void RemoveSelected()
    {
        var ids = Filter.Visible.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (ids.Count == 0) return;
        _store.Remove(ids);
        Reload();
    }
}
