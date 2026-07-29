using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using StockPlatform.Analyzer.Export;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

public class WatchlistRowViewModel : ISelectableRow, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public WatchlistEntry Entry { get; }
    private readonly JsonWatchlistStore _store;
    private double? _latestClose;

    public WatchlistRowViewModel(WatchlistEntry entry, IBarRepository barRepository, string conceptBoards, JsonWatchlistStore store)
    {
        Entry = entry;
        Board = conceptBoards;
        _store = store;
        ComputeTracking(barRepository);
    }

    public string Code => Entry.Code;
    public string Name => Entry.Name;
    /// <summary>该股所属的概念/题材板块（来自"板块热度"抓取的数据，一只票可能属于多个，用顿号连接）；
    /// 没有板块数据或不属于任何概念板块时显示"—"。由 WatchlistTabViewModel 一次性反查后传入。</summary>
    public string Board { get; }
    /// <summary>申万行业（跟其它结果表"板块"列同一个口径，来自本地静态映射，见 IndustryClassifier）。</summary>
    public string Industry => IndustryClassifier.GetIndustry(Entry.Code);
    public string Method => Entry.Method;
    public string DataDate => Entry.DataDate.ToString("yyyy-MM-dd");
    public double PriceAtPick => Entry.PriceAtPick;
    public string AddedAt => Entry.AddedAt.ToString("yyyy-MM-dd HH:mm");
    public string SatisfiedText => $"{Entry.SatisfiedCount}/{Entry.TotalCount}";

    /// <summary>"选中后表现"——用本地已有的K线历史，从选中当天(DataDate)开始查到最新一根日线，
    /// 拿最新收盘价相对PriceAtPick（选中当天收盘价）算涨跌幅。故意不持久化任何跟踪数据：本地
    /// K线库本来就有选中日之后的完整历史，每次刷新这个Tab直接现算即可，没必要额外存一份、
    /// 也没有"数据过期"的问题。start用DataDate本身（不是DataDate+1）——如果选中日之后还没有更新的
    /// 交易日数据，会查到DataDate自己那根，涨跌幅显示为0%，这正确反映"还没有新的一天可比较"。</summary>
    public string LatestCloseText { get; private set; } = "—";
    public string ChangeText { get; private set; } = "本地无该日期之后的K线数据";
    public Brush ChangeColor { get; private set; } = Brushes.Gray;

    private void ComputeTracking(IBarRepository barRepository)
    {
        var bars = barRepository.Query(Entry.Code, Granularity.Day, start: Entry.DataDate);
        if (bars.Count == 0 || Entry.PriceAtPick <= 0) return;

        var latest = bars[^1];
        _latestClose = latest.Close;
        LatestCloseText = $"{latest.Close:F2}（{latest.PeriodStart:yyyy-MM-dd}）";
        var pct = (latest.Close - Entry.PriceAtPick) / Entry.PriceAtPick * 100;
        ChangeText = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
        ChangeColor = pct >= 0 ? Brushes.Red : Brushes.Green; // 国内看盘习惯：涨红跌绿
    }

    // ── 手动持仓信息（买入日期/买入价/股数，2026-07-29新增）——单元格里直接编辑，提交时解析并
    //    立即持久化；解析不了的输入丢弃（Raise让界面回显旧值）。三个都空=观察中、没买。 ──

    public string BuyDateText
    {
        get => Entry.BuyDate?.ToString("yyyy-MM-dd") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.BuyDate = null;
            else if (DateTime.TryParse(t, out var d)) Entry.BuyDate = d.Date;
            PersistTradeInfo();
        }
    }

    public string BuyPriceText
    {
        get => Entry.BuyPrice?.ToString("F2") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.BuyPrice = null;
            else if (double.TryParse(t, out var p) && p > 0) Entry.BuyPrice = p;
            PersistTradeInfo();
        }
    }

    public string SharesText
    {
        get => Entry.Shares?.ToString() ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.Shares = null;
            else if (int.TryParse(t, out var n) && n > 0) Entry.Shares = n;
            PersistTradeInfo();
        }
    }

    public string SellDateText
    {
        get => Entry.SellDate?.ToString("yyyy-MM-dd") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.SellDate = null;
            else if (DateTime.TryParse(t, out var d)) Entry.SellDate = d.Date;
            PersistTradeInfo();
        }
    }

    public string SellPriceText
    {
        get => Entry.SellPrice?.ToString("F2") ?? "";
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0) Entry.SellPrice = null;
            else if (double.TryParse(t, out var p) && p > 0) Entry.SellPrice = p;
            PersistTradeInfo();
        }
    }

    private void PersistTradeInfo()
    {
        _store.UpdateTradeInfo(Entry.Id, Entry.BuyDate, Entry.BuyPrice, Entry.Shares, Entry.SellDate, Entry.SellPrice);
        Raise(nameof(BuyDateText));
        Raise(nameof(BuyPriceText));
        Raise(nameof(SharesText));
        Raise(nameof(SellDateText));
        Raise(nameof(SellPriceText));
        Raise(nameof(HoldingText));
        Raise(nameof(HoldingColor));
    }

    /// <summary>持仓盈亏——三种状态：没填买入价=观察中；填了买入价没填卖出价=持仓（较买入价的浮动
    /// 盈亏，有股数带金额）；买入卖出都填了=已平仓（按卖出价算最终已实现盈亏，留痕复盘）。</summary>
    public string HoldingText
    {
        get
        {
            if (Entry.BuyPrice is not (> 0)) return "观察中";

            if (Entry.SellPrice is > 0)
            {
                var spct = (Entry.SellPrice.Value - Entry.BuyPrice.Value) / Entry.BuyPrice.Value * 100;
                if (Entry.Shares is > 0)
                {
                    var spnl = (Entry.SellPrice.Value - Entry.BuyPrice.Value) * Entry.Shares.Value;
                    return $"已平仓 {(spnl >= 0 ? "+" : "")}{spnl:N0}元（{(spct >= 0 ? "+" : "")}{spct:F2}%）";
                }
                return $"已平仓 {(spct >= 0 ? "+" : "")}{spct:F2}%";
            }

            if (_latestClose is not (> 0)) return "无最新价";
            var pct = (_latestClose.Value - Entry.BuyPrice.Value) / Entry.BuyPrice.Value * 100;
            var text = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
            if (Entry.Shares is > 0)
            {
                var pnl = (_latestClose.Value - Entry.BuyPrice.Value) * Entry.Shares.Value;
                text += $"（{(pnl >= 0 ? "+" : "")}{pnl:N0}元）";
            }
            return text;
        }
    }

    public Brush HoldingColor
    {
        get
        {
            if (Entry.BuyPrice is not (> 0)) return Brushes.Gray;
            if (Entry.SellPrice is > 0)
                return Entry.SellPrice >= Entry.BuyPrice ? Brushes.Red : Brushes.Green;
            if (_latestClose is not (> 0)) return Brushes.Gray;
            return _latestClose >= Entry.BuyPrice ? Brushes.Red : Brushes.Green;
        }
    }

    /// <summary>Plain mutable property, same reasoning as ResultRowViewModel.IsSelected — only
    /// read when "移除勾选" is clicked.</summary>
    public bool IsSelected { get; set; }
}

/// <summary>"自选股" tab — cross-method view of everything the user checked and added from the
/// other four tabs (see WatchlistAdder), kept so picks can be reviewed/tracked later to see how
/// they actually performed and help tune the 4 methods (see doc/analysis-app-design.md section
/// 3.5). Reads/writes JsonWatchlistStore, not total.sqlite — this is the Analyzer's own state,
/// not Fetcher's shared read-only data.</summary>
public class WatchlistTabViewModel
{
    private readonly JsonWatchlistStore _store;
    private readonly IBarRepository _barRepository;
    private readonly IBoardRepository _boardRepository;

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ExportCommand { get; }

    public WatchlistTabViewModel(JsonWatchlistStore store, IBarRepository barRepository, IBoardRepository boardRepository)
    {
        _store = store;
        _barRepository = barRepository;
        _boardRepository = boardRepository;
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected());
        ExportCommand = new RelayCommand(_ => GridExporter.ExportWatchlist(Entries));
        Reload();
    }

    /// <summary>Public so MainWindow can call it when the user switches to this tab — entries
    /// added from another tab in the same session otherwise wouldn't show up until "刷新" is
    /// clicked manually. Also re-triggers each row's "选中后表现"计算 with whatever is currently
    /// the latest local K线 data.</summary>
    public void Reload()
    {
        Entries.Clear();
        // 一次性反查"股票→所属概念板块"，每行直接取（没有板块数据时 map 为空，各行显示"—"）。
        var conceptMap = _boardRepository.GetConceptBoardsByStock();
        foreach (var e in _store.Load().OrderByDescending(e => e.AddedAt))
        {
            var boards = conceptMap.TryGetValue(e.Code, out var list) && list.Count > 0
                ? string.Join("、", list)
                : "—";
            Entries.Add(new WatchlistRowViewModel(e, _barRepository, boards, _store));
        }
    }

    private void RemoveSelected()
    {
        var toRemove = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (toRemove.Count == 0) return;
        _store.Remove(toRemove);
        Reload();
    }
}
