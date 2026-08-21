using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using StockPlatform.Analyzer.Watchlist;

namespace StockPlatform.Analyzer;

/// <summary>
/// 【交易记录】窗口里的一行——一笔买入或卖出。字段都用字符串保存：表格里是自由输入，边打字边解析
/// 会把"12."这种半截输入吞掉；统一在点【保存】时一次性校验、解析（<see cref="TradeLotsWindow.Save_Click"/>）。
/// </summary>
public class TradeLotEditRow : INotifyPropertyChanged
{
    /// <summary>方向下拉的两个选项——DataGridComboBoxColumn 用 x:Static 直接绑这个。</summary>
    public static string[] SideOptions { get; } = { "买入", "卖出" };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        // 金额/费用是算出来的，任何一格改了都要跟着刷新。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AmountText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FeeText)));
    }

    /// <summary>原来那笔的 Id——改价格/股数时保留下来，不用每次编辑都换一个新 Id。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>当前费率（本窗口顶部设置的那一份，全程序共用）——用来算本笔的佣金/过户费/印花税。</summary>
    public TradeFeeSettings Fees { get; set; } = new();

    /// <summary>费率改了以后重算本行的费用显示（<see cref="Fees"/> 是同一个对象、原地改的，
    /// 所以只要通知界面重读就行）。</summary>
    public void RefreshFees() => Raise(nameof(FeeText));

    private string _sideText = "买入";
    public string SideText { get => _sideText; set { _sideText = value; Raise(); } }

    private string _dateText = "";
    public string DateText { get => _dateText; set { _dateText = value; Raise(); } }

    private string _priceText = "";
    public string PriceText { get => _priceText; set { _priceText = value; Raise(); } }

    private string _sharesText = "";
    public string SharesText { get => _sharesText; set { _sharesText = value; Raise(); } }

    /// <summary>这笔的成交金额（价格×股数）——录入时顺手核对一下有没有把股数打错一个零。</summary>
    public string AmountText => Amount is { } a ? $"{a:N0}元" : "—";

    /// <summary>这笔的费用明细（佣金/过户费/卖出印花税），按当前费率算。</summary>
    public string FeeText
    {
        get
        {
            if (Amount is not { } amount) return "—";
            var side = IsBuy ? TradeSide.Buy : TradeSide.Sell;
            var commission = Fees.Commission(amount);
            var transfer = Fees.TransferFee(amount);
            var stamp = Fees.StampDuty(amount, side);
            var detail = $"佣金{commission:0.##}+过户{transfer:0.##}";
            if (side == TradeSide.Sell) detail += $"+印花{stamp:0.##}";
            return $"{commission + transfer + stamp:0.##}元（{detail}）";
        }
    }

    private double? Amount =>
        double.TryParse(PriceText, out var p) && p > 0 && int.TryParse(SharesText, out var s) && s > 0
            ? p * s
            : null;

    public bool IsBuy => SideText == SideOptions[0];

    public static TradeLotEditRow From(TradeLot lot, TradeFeeSettings fees) => new()
    {
        Id = lot.Id,
        Fees = fees,
        SideText = lot.Side == TradeSide.Buy ? SideOptions[0] : SideOptions[1],
        DateText = lot.Date.ToString("yyyy-MM-dd"),
        PriceText = lot.Price > 0 ? lot.Price.ToString("F2") : "",
        SharesText = lot.Shares > 0 ? lot.Shares.ToString() : "",
    };
}

/// <summary>
/// 一只票的成交明细录入窗口（2026-08-11新增）——买入、卖出都可以有多笔，用来支持金字塔式建仓和
/// 分批止盈。改完点【保存】整份覆盖回 <see cref="WatchlistEntry.Lots"/>（见 JsonWatchlistStore.UpdateLots），
/// 取消则什么都不动：窗口里编辑的是拷贝，不是原对象。
/// </summary>
public partial class TradeLotsWindow : Window
{
    public ObservableCollection<TradeLotEditRow> Rows { get; } = new();

    /// <summary>点【保存】后解析好的成交明细——调用方在 DialogResult==true 时取。</summary>
    public IReadOnlyList<TradeLot> Result { get; private set; } = Array.Empty<TradeLot>();

    private readonly TradeFeeStore _feeStore;
    private TradeFeeSettings _fees;

    /// <summary>【底仓】页传进来的每股股息（元/股，税前）——把"目标年化股息"换算成目标股数用。
    /// 主动仓不传（null），那一行 UI 整个折叠。</summary>
    private readonly double? _dividendPerShare;

    /// <summary>点【保存】后的目标年化股息——调用方在 DialogResult==true 时取，写回
    /// core-positions.json。主动仓打开时始终是 null。</summary>
    public double? TargetAnnualDividendResult { get; private set; }

    /// <summary>
    /// <paramref name="targetAnnualDividend"/>/<paramref name="dividendPerShare"/> 只有【底仓】页会传：
    /// 传了就在费率下面多显示一行"目标年化股息 → 目标股数"（2026-08-20 从底仓页表格挪进来的，
    /// 见 XAML 里 CorePositionPlanPanel 的注释）。主动仓两个都传 null，那一行不出现。
    /// </summary>
    public TradeLotsWindow(string title, IEnumerable<TradeLot> lots, TradeFeeStore feeStore,
        double? targetAnnualDividend = null, double? dividendPerShare = null)
    {
        InitializeComponent();
        _feeStore = feeStore;
        _fees = feeStore.Current;
        TitleText.Text = title;
        ShowFeeInputs();

        _dividendPerShare = dividendPerShare;
        if (targetAnnualDividend.HasValue)
        {
            CorePositionPlanPanel.Visibility = Visibility.Visible;
            TargetDividendBox.Text = targetAnnualDividend.Value > 0
                ? targetAnnualDividend.Value.ToString("F0")
                : "";
            TargetAnnualDividendResult = targetAnnualDividend;
            // 不在这里调 ShowTargetShares()——此刻 Rows 还没填，算不出"还差多少股"。
            // 交给构造末尾的 UpdateSummary() 一起刷，之后每次增删改行也会跟着实时更新。
        }
        foreach (var lot in lots.OrderBy(l => l.Date))
            Rows.Add(TradeLotEditRow.From(lot, _fees));
        LotsGrid.ItemsSource = Rows;

        // 汇总要跟着编辑实时变——行内容变了、增删行了都重算一次。
        Rows.CollectionChanged += OnRowsChanged;
        foreach (var r in Rows) r.PropertyChanged += OnRowPropertyChanged;
        UpdateSummary();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var r in e.OldItems?.OfType<TradeLotEditRow>() ?? Enumerable.Empty<TradeLotEditRow>())
            r.PropertyChanged -= OnRowPropertyChanged;
        foreach (var r in e.NewItems?.OfType<TradeLotEditRow>() ?? Enumerable.Empty<TradeLotEditRow>())
            r.PropertyChanged += OnRowPropertyChanged;
        UpdateSummary();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    // ── 费率设置（账户级、全程序共用一份）——在这里改，改完立刻落盘并按新费率重算本窗口的费用/汇总。
    //    填得不对（非数字/负数）就丢弃、回显原值。 ──

    private void ShowFeeInputs()
    {
        CommissionBox.Text = _fees.CommissionRateBp.ToString("0.###");
        MinCommissionBox.Text = _fees.MinCommission.ToString("0.##");
        TransferBox.Text = _fees.TransferRateBp.ToString("0.###");
        StampDutyBox.Text = _fees.StampDutyRateBp.ToString("0.###");
    }

    private void FeeInput_LostFocus(object sender, RoutedEventArgs e) => ApplyFeeInputs();

    // ── 底仓专用：目标年化股息 → 目标股数 ──

    private void TargetDividend_LostFocus(object sender, RoutedEventArgs e) => ApplyTargetDividend();

    /// <summary>解析目标年化股息。填得不对（非数字/负数）就丢弃、回显原值，跟费率框一个做法。</summary>
    private void ApplyTargetDividend()
    {
        var text = (TargetDividendBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            TargetAnnualDividendResult = 0;
        }
        else if (double.TryParse(text, out var parsed) && parsed >= 0)
        {
            TargetAnnualDividendResult = parsed;
        }
        // 解析不了就保持原值、回显
        TargetDividendBox.Text = TargetAnnualDividendResult is > 0
            ? TargetAnnualDividendResult.Value.ToString("F0")
            : "";
        ShowTargetShares();
    }

    /// <summary>目标股数 = 目标年化股息 ÷ 每股股息，向下取整到整手。同时提示还差多少股——
    /// 底仓是分档建仓的，"还差多少"比"目标多少"更能指导下一笔买多少。</summary>
    private void ShowTargetShares()
    {
        double target = TargetAnnualDividendResult ?? 0;
        if (target <= 0 || _dividendPerShare is not > 0)
        {
            TargetSharesText.Text = _dividendPerShare is > 0
                ? "（填了目标才算得出目标股数）"
                : "（本地没有该股分红数据，算不出目标股数）";
            return;
        }

        int targetShares = (int)Math.Floor(target / _dividendPerShare.Value / 100) * 100;
        // 已买股数走 Parse + TradeCostSummary，跟下面的汇总同一个口径（半截输入自动忽略）
        int held = TradeCostSummary.For(Parse(out _), _fees).RemainingShares;
        int gap = targetShares - held;
        TargetSharesText.Text =
            $"→ 目标 {targetShares:N0} 股（每股股息 {_dividendPerShare.Value:F3} 元）" +
            (gap > 0 ? $"，还差 {gap:N0} 股" : gap < 0 ? $"，已超出 {-gap:N0} 股" : "，已建满");
    }

    /// <summary>把四个输入框的值收进设置里并保存。返回是否真的改动了（调用方据此决定要不要刷新列表）。</summary>
    private bool ApplyFeeInputs()
    {
        double before = _fees.CommissionRateBp + _fees.MinCommission + _fees.TransferRateBp + _fees.StampDutyRateBp;

        if (TryRate(CommissionBox.Text, out var commission)) _fees.CommissionRateBp = commission;
        if (TryRate(MinCommissionBox.Text, out var minCommission)) _fees.MinCommission = minCommission;
        if (TryRate(TransferBox.Text, out var transfer)) _fees.TransferRateBp = transfer;
        if (TryRate(StampDutyBox.Text, out var stamp)) _fees.StampDutyRateBp = stamp;

        ShowFeeInputs();   // 解析不了的输入回显原值
        bool changed = Math.Abs(before - (_fees.CommissionRateBp + _fees.MinCommission + _fees.TransferRateBp + _fees.StampDutyRateBp)) > 1e-9;
        if (!changed) return false;

        FeesChanged = true;
        _feeStore.Save();
        foreach (var r in Rows) r.RefreshFees();   // 每笔的费用列要按新费率重算
        UpdateSummary();
        return true;
    }

    private static bool TryRate(string? text, out double value)
        => double.TryParse((text ?? "").Trim(), out value) && value >= 0;

    /// <summary>本窗口里改过费率没有——改过的话调用方要刷新列表（含费盈亏/止亏价全变了），
    /// **哪怕用户点的是"取消"**：费率是账户级设置，跟这只票的成交明细改不改是两回事。</summary>
    public bool FeesChanged { get; private set; }

    private void AddBuy_Click(object sender, RoutedEventArgs e) => AddRow(buy: true);

    private void AddSell_Click(object sender, RoutedEventArgs e) => AddRow(buy: false);

    /// <summary>新起一行。日期默认今天；买入价默认带上一笔的价格（多半是在同一波里加仓，改个数字就行），
    /// 卖出股数默认当前剩余持仓（一次清仓最常见，分批卖就改小）。</summary>
    private void AddRow(bool buy)
    {
        var parsed = Parse(out _);
        var lastPrice = Rows.Select(r => double.TryParse(r.PriceText, out var p) ? p : 0).LastOrDefault(p => p > 0);
        int remaining = parsed.Where(l => l.Side == TradeSide.Buy).Sum(l => l.Shares)
                      - parsed.Where(l => l.Side == TradeSide.Sell).Sum(l => l.Shares);

        var row = new TradeLotEditRow
        {
            Fees = _fees,
            SideText = buy ? TradeLotEditRow.SideOptions[0] : TradeLotEditRow.SideOptions[1],
            DateText = DateTime.Today.ToString("yyyy-MM-dd"),
            PriceText = lastPrice > 0 ? lastPrice.ToString("F2") : "",
            SharesText = !buy && remaining > 0 ? remaining.ToString() : "",
        };
        Rows.Add(row);
        LotsGrid.SelectedItem = row;
        LotsGrid.ScrollIntoView(row);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = LotsGrid.SelectedItems.OfType<TradeLotEditRow>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("先选中要删除的行（点行首选中，按住 Ctrl 可多选）。", "删除",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var r in selected) Rows.Remove(r);
    }

    /// <summary>把表格里能解析的行解析成 TradeLot。<paramref name="badRow"/> 返回第一行解析不了的行号
    /// （1开始，全都没问题就是 0）——【保存】时用它精确指出是哪一行填错了。</summary>
    private List<TradeLot> Parse(out int badRow)
    {
        badRow = 0;
        var result = new List<TradeLot>();
        for (int i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            if (!DateTime.TryParse(r.DateText?.Trim(), out var date)
                || !double.TryParse(r.PriceText?.Trim(), out var price) || price <= 0
                || !int.TryParse(r.SharesText?.Trim(), out var shares) || shares <= 0)
            {
                if (badRow == 0) badRow = i + 1;
                continue;
            }
            result.Add(new TradeLot
            {
                Id = r.Id,
                Side = r.IsBuy ? TradeSide.Buy : TradeSide.Sell,
                Date = date.Date,
                Price = price,
                Shares = shares,
            });
        }
        return result;
    }

    /// <summary>底部实时汇总——保存后列表里会看到的那几个数（总股数 / 加权均价 / 含费成本 / 已实现），
    /// 录入时先对一眼。跟列表用的是同一个计算器（TradeCostSummary），不会出现两处口径不一致。</summary>
    private void UpdateSummary()
    {
        // 底仓那一行的"还差多少股"跟着成交明细实时变（主动仓时这行是折叠的，方法内部会直接返回）
        if (CorePositionPlanPanel.Visibility == Visibility.Visible) ShowTargetShares();

        var cost = TradeCostSummary.For(Parse(out _), _fees);
        if (cost.BuyShares == 0)
        {
            SummaryText.Text = "还没有有效的买入记录";
            return;
        }

        var parts = new List<string>
        {
            $"买入 {cost.BuyLotCount}笔 {cost.BuyShares:N0}股 均价 {cost.AvgBuyPrice:F2}，费用 {cost.BuyFee:N2}元，实际支出 {cost.NetCost:N0}元",
            $"含费成本均价 {cost.NetAvgCost:F3}（保本价）",
        };

        if (cost.SellShares > 0)
            parts.Add($"卖出 {cost.SellLotCount}笔 {cost.SellShares:N0}股 均价 {cost.AvgSellPrice:F2}，费用 {cost.SellFee:N2}元，" +
                      $"净到手 {cost.NetProceeds:N0}元，已实现 {cost.RealizedNet:+#,0;-#,0}元（{cost.RealizedNetPct:+0.00;-0.00}%）");

        int remaining = cost.BuyShares - cost.SellShares;
        if (remaining > 0)
        {
            // 止亏价：剩下的卖到这个价，整笔（含已落袋的和两头的费用）刚好不赚不亏。
            var breakEven = cost.BreakEvenPrice();
            parts.Add($"剩余持仓 {remaining:N0}股，止亏价 " +
                      (breakEven is null ? "—" : breakEven <= 0 ? "已保本（前面卖的已经把成本赚回来了）" : $"{breakEven:F3}"));
        }
        else parts.Add(remaining == 0 ? "已清仓" : $"卖出比买入多 {-remaining:N0}股⚠");
        SummaryText.Text = string.Join("\n", parts);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 正在编辑的单元格先提交，否则最后改的那格会丢（点按钮不算失焦提交）。费率框同理。
        LotsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ApplyFeeInputs();
        if (CorePositionPlanPanel.Visibility == Visibility.Visible) ApplyTargetDividend();

        var lots = Parse(out int badRow);
        if (badRow > 0)
        {
            MessageBox.Show($"第 {badRow} 行填得不完整：日期要能识别（如 2026-08-11），价格和股数都要大于0。\n" +
                            "不用的行请用【删除选中】删掉。", "保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int buyShares = lots.Where(l => l.Side == TradeSide.Buy).Sum(l => l.Shares);
        int sellShares = lots.Where(l => l.Side == TradeSide.Sell).Sum(l => l.Shares);
        if (sellShares > buyShares)
        {
            // 不直接拦——可能是加仓那笔忘了录。但得让人明确确认，免得默默存成一个对不上的仓位。
            var ok = MessageBox.Show(
                $"卖出 {sellShares:N0}股 比买入 {buyShares:N0}股 还多，是不是漏录了买入？\n\n仍然按现在填的保存吗？",
                "股数对不上", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;
        }

        Result = lots;
        DialogResult = true;
    }
}
