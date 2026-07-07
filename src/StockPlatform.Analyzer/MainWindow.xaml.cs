using System.Windows;
using System.Windows.Controls;
using StockPlatform.Analyzer.ViewModels;

namespace StockPlatform.Analyzer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Setting WindowState=Maximized here (or even in XAML) doesn't reliably stick — WPF
        // needs a completed layout pass first. Deferring to Loaded is the standard workaround.
        Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void FoundationGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!TryGetSelectedBars(sender, vm, out var row, out var bars)) return;
        new DetailWindow(row.Result, bars, vm.FoundationTab.Lookback) { Owner = this }.ShowDialog();
    }

    private void GoldenCrossGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // 金叉法的详情图（GoldenCrossChartBuilder）画的是它自己7条规则用到的指标（MA5/MA10/MACD/
        // KDJ/RSI/成交量），跟筑基法那套K线+BOLL+MACD的DetailWindow是两回事，不能共用——共用会导致
        // 图上的指标和判断依据文字对不上（例如筑基法那条参考线用收盘价，金叉法条件7用的是最高价）。
        if (!TryGetSelectedBars(sender, vm, out var row, out var bars)) return;
        new GoldenCrossDetailWindow(row.Result, bars) { Owner = this }.ShowDialog();
    }

    private bool TryGetSelectedBars(object sender, MainViewModel vm, out ResultRowViewModel row, out List<StockPlatform.Logic.Models.Bar> bars)
    {
        row = null!;
        bars = null!;
        if ((sender as DataGrid)?.SelectedItem is not ResultRowViewModel selected) return false;
        row = selected;

        if (row.Error != null)
        {
            MessageBox.Show(this, row.Error, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        bars = vm.BarRepository.Query(row.Code, row.Result.Granularity);
        if (bars.Count == 0)
        {
            MessageBox.Show(this, "没有找到该股票的K线数据。", "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }
}
