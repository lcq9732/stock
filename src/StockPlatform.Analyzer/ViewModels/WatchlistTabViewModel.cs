using System.Collections.ObjectModel;
using System.Windows.Media;
using StockPlatform.Analyzer.Export;
using StockPlatform.Analyzer.Watchlist;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

public class WatchlistRowViewModel : ISelectableRow
{
    public WatchlistEntry Entry { get; }

    public WatchlistRowViewModel(WatchlistEntry entry, IBarRepository barRepository)
    {
        Entry = entry;
        ComputeTracking(barRepository);
    }

    public string Code => Entry.Code;
    public string Name => Entry.Name;
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
        LatestCloseText = $"{latest.Close:F2}（{latest.PeriodStart:yyyy-MM-dd}）";
        var pct = (latest.Close - Entry.PriceAtPick) / Entry.PriceAtPick * 100;
        ChangeText = $"{(pct >= 0 ? "+" : "")}{pct:F2}%";
        ChangeColor = pct >= 0 ? Brushes.Red : Brushes.Green; // 国内看盘习惯：涨红跌绿
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

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ExportCommand { get; }

    public WatchlistTabViewModel(JsonWatchlistStore store, IBarRepository barRepository)
    {
        _store = store;
        _barRepository = barRepository;
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
        foreach (var e in _store.Load().OrderByDescending(e => e.AddedAt))
            Entries.Add(new WatchlistRowViewModel(e, _barRepository));
    }

    private void RemoveSelected()
    {
        var toRemove = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (toRemove.Count == 0) return;
        _store.Remove(toRemove);
        Reload();
    }
}
