using System.Windows;
using System.Windows.Controls;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

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

    // SelectionChanged bubbles up from ANY Selector inside a tab's content too (e.g. the 峰哥法
    // 粒度 ComboBox), not just the TabControl itself — the TabItem type check below is what
    // filters those out, not the sender.
    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem { Header: "自选股" })
            vm.WatchlistTab.Reload(); // picks up anything added from another tab this session
    }

    // WPF's DataGridCheckBoxColumn needs two clicks by default (the first click only focuses/
    // selects the cell; only the second actually reaches the checkbox). Focusing the cell during
    // the tunneling PreviewMouseLeftButtonDown — before the same click bubbles back up to the
    // checkbox's own click handling — makes the very first click land on the checkbox instead.
    // Wired as an implicit DataGridCell style in MainWindow.xaml, applies to every grid.
    private void DataGridCell_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGridCell { IsFocused: false, IsEditing: false } cell)
            cell.Focus();
    }

    private void FoundationCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;
        if (!TryGetBars(vm, row, out var bars)) return;
        new DetailWindow(row.Result, bars, vm.FoundationTab.Lookback) { Owner = this }.ShowDialog();
    }

    private void GoldenCrossCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // 金叉法的详情图（GoldenCrossChartBuilder）画的是它自己7条规则用到的指标（MA5/MA10/MACD/
        // KDJ/RSI/成交量），跟峰哥法那套K线+BOLL+MACD的DetailWindow是两回事，不能共用——共用会导致
        // 图上的指标和判断依据文字对不上（例如峰哥法那条参考线用收盘价，金叉法条件7用的是最高价）。
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;
        if (!TryGetBars(vm, row, out var bars)) return;
        new GoldenCrossDetailWindow(row.Result, bars) { Owner = this }.ShowDialog();
    }

    private void BottomReboundCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;
        if (!TryGetBars(vm, row, out var bars)) return;
        new BottomReboundDetailWindow(row.Result, bars, vm.BottomReboundTab.DifThreshold) { Owner = this }.ShowDialog();
    }

    private void MidCapPullbackCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;

        if (row.Error != null)
        {
            MessageBox.Show(this, row.Error, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 彬哥法要同时用到日/周/月三种粒度（TryGetBars只查一种，这里单独取三次）。
        var dayBars = vm.BarRepository.Query(row.Code, Granularity.Day);
        var weekBars = vm.BarRepository.Query(row.Code, Granularity.Week);
        var monthBars = vm.BarRepository.Query(row.Code, Granularity.Month);
        if (dayBars.Count == 0 || weekBars.Count == 0 || monthBars.Count == 0)
        {
            MessageBox.Show(this, "没有找到该股票的日/周/月线K线数据。", "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new MidCapPullbackDetailWindow(row.Result, dayBars, weekBars, monthBars) { Owner = this }.ShowDialog();
    }

    private void TriangleConvergenceCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;
        if (!TryGetBars(vm, row, out var bars)) return;
        new TriangleConvergenceDetailWindow(row.Result, bars, vm.TriangleConvergenceTab.Lookback, vm.TriangleConvergenceTab.SwingWindow) { Owner = this }.ShowDialog();
    }

    private void WatchlistCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not WatchlistRowViewModel row) return;
        var entry = row.Entry;

        // 图表用最新数据重新画（这样能看出加入自选之后走势怎么样），文字依据用加入自选那一刻的
        // 快照（Criteria）——两者故意不是同一个时间点的数据，这正是"跟踪"这个功能要看的东西。
        var result = new StockScreenResult
        {
            Code = entry.Code,
            Name = entry.Name,
            Granularity = entry.Granularity,
            Passed = true,
            Criteria = entry.Criteria.Select(c => new CriterionResult
            {
                Name = c.Name,
                Satisfied = c.Satisfied,
                Basis = c.Basis,
                DataMissing = c.DataMissing,
            }).ToList(),
        };

        try
        {
            switch (entry.Method)
            {
                case "峰哥法":
                {
                    var bars = vm.BarRepository.Query(entry.Code, entry.Granularity);
                    if (bars.Count == 0) throw new InvalidOperationException("没有找到该股票的K线数据。");
                    new DetailWindow(result, bars, entry.Lookback ?? 60) { Owner = this }.ShowDialog();
                    break;
                }
                case "金叉法":
                {
                    var bars = vm.BarRepository.Query(entry.Code, Granularity.Day);
                    if (bars.Count == 0) throw new InvalidOperationException("没有找到该股票的K线数据。");
                    new GoldenCrossDetailWindow(result, bars) { Owner = this }.ShowDialog();
                    break;
                }
                case "耀哥法":
                {
                    var bars = vm.BarRepository.Query(entry.Code, Granularity.Day);
                    if (bars.Count == 0) throw new InvalidOperationException("没有找到该股票的K线数据。");
                    new BottomReboundDetailWindow(result, bars, entry.DifThreshold ?? 0) { Owner = this }.ShowDialog();
                    break;
                }
                case "彬哥法":
                {
                    var dayBars = vm.BarRepository.Query(entry.Code, Granularity.Day);
                    var weekBars = vm.BarRepository.Query(entry.Code, Granularity.Week);
                    var monthBars = vm.BarRepository.Query(entry.Code, Granularity.Month);
                    if (dayBars.Count == 0 || weekBars.Count == 0 || monthBars.Count == 0)
                        throw new InvalidOperationException("没有找到该股票的日/周/月线K线数据。");
                    new MidCapPullbackDetailWindow(result, dayBars, weekBars, monthBars) { Owner = this }.ShowDialog();
                    break;
                }
                case "三角收敛":
                {
                    var bars = vm.BarRepository.Query(entry.Code, Granularity.Day);
                    if (bars.Count == 0) throw new InvalidOperationException("没有找到该股票的K线数据。");
                    // WatchlistEntry 只存了 Lookback，没存 SwingWindow（没有对应字段）——重新打开
                    // 时用Tab当前的SwingWindow值兜底，跟其它方法"Lookback ?? 默认值"是同一种降级方式。
                    new TriangleConvergenceDetailWindow(result, bars, entry.Lookback ?? 60, vm.TriangleConvergenceTab.SwingWindow) { Owner = this }.ShowDialog();
                    break;
                }
                default:
                    MessageBox.Show(this, $"未知方法：{entry.Method}", "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // "行情详情"——典型股票APP样式的纯行情图（见QuoteDetailWindow），跟方法/条件无关，固定看
    // 日线，四个方法的结果表共用同一个处理逻辑。
    private void QuoteDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ResultRowViewModel row)
            OpenQuoteDetail(row.Code, row.Name);
    }

    private void WatchlistQuoteDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is WatchlistRowViewModel row)
            OpenQuoteDetail(row.Code, row.Name);
    }

    private void OpenQuoteDetail(string code, string name)
    {
        if (DataContext is not MainViewModel vm) return;
        // QuoteDetailWindow 自己按需查日/周/月线（粒度切换按钮见该窗口），这里只提前确认日线
        // 至少有数据，避免打开一个完全空白、什么都显示不出来的窗口。
        if (vm.BarRepository.Query(code, Granularity.Day).Count == 0)
        {
            MessageBox.Show(this, "没有找到该股票的日线数据。", "无法显示行情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new QuoteDetailWindow(code, name, vm.BarRepository) { Owner = this }.ShowDialog();
    }

    private bool TryGetBars(MainViewModel vm, ResultRowViewModel row, out List<Bar> bars)
    {
        bars = null!;
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
