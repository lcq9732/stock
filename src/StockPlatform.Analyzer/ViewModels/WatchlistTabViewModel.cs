using System.Collections.ObjectModel;
using StockPlatform.Analyzer.Watchlist;

namespace StockPlatform.Analyzer.ViewModels;

public class WatchlistRowViewModel
{
    public WatchlistEntry Entry { get; }
    public WatchlistRowViewModel(WatchlistEntry entry) { Entry = entry; }

    public string Code => Entry.Code;
    public string Name => Entry.Name;
    public string Method => Entry.Method;
    public string DataDate => Entry.DataDate.ToString("yyyy-MM-dd");
    public double PriceAtPick => Entry.PriceAtPick;
    public string AddedAt => Entry.AddedAt.ToString("yyyy-MM-dd HH:mm");
    public string SatisfiedText => $"{Entry.SatisfiedCount}/{Entry.TotalCount}";

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

    public ObservableCollection<WatchlistRowViewModel> Entries { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }

    public WatchlistTabViewModel(JsonWatchlistStore store)
    {
        _store = store;
        RefreshCommand = new RelayCommand(_ => Reload());
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected());
        Reload();
    }

    /// <summary>Public so MainWindow can call it when the user switches to this tab — entries
    /// added from another tab in the same session otherwise wouldn't show up until "刷新" is
    /// clicked manually.</summary>
    public void Reload()
    {
        Entries.Clear();
        foreach (var e in _store.Load().OrderByDescending(e => e.AddedAt))
            Entries.Add(new WatchlistRowViewModel(e));
    }

    private void RemoveSelected()
    {
        var toRemove = Entries.Where(e => e.IsSelected).Select(e => e.Entry.Id).ToList();
        if (toRemove.Count == 0) return;
        _store.Remove(toRemove);
        Reload();
    }
}
