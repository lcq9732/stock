namespace StockPlatform.Analyzer.ViewModels;

/// <summary>Rows that carry a "选" checkbox in a DataGrid (ResultRowViewModel / WatchlistRowViewModel).
/// Lets the shared 表头全选/全不选 handler (MainWindow.SelectAllHeader_Click) flip every row without
/// caring which grid/row type it is. IsSelected stays a plain mutable property (no
/// INotifyPropertyChanged) — the handler calls DataGrid.Items.Refresh() to repaint after a bulk
/// change, since nothing else needs to react live to a single check/uncheck.</summary>
public interface ISelectableRow
{
    bool IsSelected { get; set; }
}
