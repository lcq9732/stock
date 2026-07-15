using System.Windows;
using System.Windows.Controls;
using StockPlatform.Analyzer.ViewModels;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer;

public partial class MainWindow : Window
{
    // 用WinForms的NotifyIcon（WPF自己没有托盘图标控件）——故意不加 using System.Windows.Forms;，
    // 全部用全名引用，跟Fetcher那边同样的做法一致，避免跟已有的 System.Windows.MessageBox 等
    // 同名类型产生歧义。
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        // Setting WindowState=Maximized here (or even in XAML) doesn't reliably stick — WPF
        // needs a completed layout pass first. Deferring to Loaded is the standard workaround.
        Loaded += (_, _) => WindowState = WindowState.Maximized;
        Closed += OnClosed;
        StateChanged += OnStateChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 真正关闭时才释放托盘图标——留着不释放会在系统托盘留下一个点不动的幽灵图标，直到
        // 资源管理器重启才会消失。
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    /// <summary>
    /// 最小化到系统托盘（跟Fetcher那边同样的做法，见 StockPlatform.Fetcher/MainWindow.xaml.cs）——
    /// 最小化后窗口从任务栏消失、缩到托盘里；关闭程序走正常的 Close()，不受这个影响。
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;

        Hide();
        ShowInTaskbar = false;

        if (_trayIcon == null)
        {
            // Assembly.Location 在单文件发布里永远是空字符串，不能当兜底——这个项目就是单文件
            // 发布（见 csproj 的 PublishSingleFile），用 AppContext.BaseDirectory 拼出.exe路径。
            var exePath = Environment.ProcessPath
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "StockPlatform.Analyzer.exe");
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
            menu.Items.Add("退出程序", null, (_, _) => Close());

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "A股批量分析程序",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _trayIcon.MouseClick += (_, args) =>
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Left) RestoreFromTray();
            };
        }
        else
        {
            _trayIcon.Visible = true;
        }
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Maximized;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
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

    // 点"选"列表头的复选框 = 对整列全选/全不选。IsSelected 是普通可变属性、没有变更通知（见
    // ISelectableRow / ResultRowViewModel 的注释），批量改完必须 Items.Refresh() 让每行的勾选框
    // 重画。六个表（五个方法结果表 + 自选股表）共用这一个处理器，靠往上找到所在的 DataGrid 来
    // 区分是哪一个。注意：这个表头复选框只是"一键全选/全不选"的开关，不会随用户手动逐行勾选而
    // 自动反映"是否已全选"（IsSelected 没有通知，做联动得不偿失，这里刻意从简）。
    private void SelectAllHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var grid = FindVisualAncestor<DataGrid>(cb);
        if (grid == null) return;
        bool check = cb.IsChecked == true;
        foreach (var item in grid.Items)
            if (item is ISelectableRow row) row.IsSelected = check;
        grid.Items.Refresh();
    }

    private static T? FindVisualAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (DependencyObject? d = start; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is T match) return match;
        return null;
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

    private void RisingLowsCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;
        // 手工验证模式下图表数据也截到分析用的截止日期，否则图上画的锚点（用全量数据重新定位）
        // 会和当时的判定结果对不上
        if (!TryGetBars(vm, row, out var bars, vm.RisingLowsTab.AppliedCutoffDate)) return;
        new RisingLowsDetailWindow(row.Result, bars) { Owner = this }.ShowDialog();
    }

    // 短线法的"条件详情"复用金叉法的详情窗口（GoldenCrossDetailWindow）——短线法用到的指标
    // （MA5/MA10、前20日最高价突破线、成交量对比5日均量、MACD）正好是那张图已经画的一个子集，
    // 且突破线用的是同一套"前20日最高价"口径，图和条件文字对得上（见 ShortTermAnalysisEngine 注释）。
    private void ShortTermCriteriaButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (((FrameworkElement)sender).DataContext is not ResultRowViewModel row) return;
        if (!TryGetBars(vm, row, out var bars)) return;
        new GoldenCrossDetailWindow(row.Result, bars) { Owner = this }.ShowDialog();
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
                case "短线法": // 短线法复用金叉法的详情图，理由见 ShortTermCriteriaButton_Click
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
                case "阶梯低点法":
                {
                    var bars = vm.BarRepository.Query(entry.Code, Granularity.Day);
                    if (bars.Count == 0) throw new InvalidOperationException("没有找到该股票的K线数据。");
                    new RisingLowsDetailWindow(result, bars) { Owner = this }.ShowDialog();
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

    // 查询Tab的"K线详情"——跟"行情详情"是同一个纯行情窗口（QuoteDetailWindow），只是入口在查询结果里。
    private void QueryQuoteDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QueryRowViewModel row)
            OpenQuoteDetail(row.Code, row.Name);
    }

    // 板块热度Tab里成分股的"K线详情"——同一个纯行情窗口。
    private void BoardMemberQuoteDetailButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is BoardMemberRowViewModel row)
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

    /// <param name="cutoffDate">非空时把K线截到这一天(含)——阶梯低点法的"按历史截止日期验证"
    /// 模式用，保证详情图和当时的判定用同一批数据。</param>
    private bool TryGetBars(MainViewModel vm, ResultRowViewModel row, out List<Bar> bars, DateTime? cutoffDate = null)
    {
        bars = null!;
        if (row.Error != null)
        {
            MessageBox.Show(this, row.Error, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        bars = vm.BarRepository.Query(row.Code, row.Result.Granularity,
            end: cutoffDate?.Date.AddDays(1).AddTicks(-1));
        if (bars.Count == 0)
        {
            MessageBox.Show(this, "没有找到该股票的K线数据。", "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }
}
