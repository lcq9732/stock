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
#if DEBUG
        // Debug 构建可以跟正在用的 Release 版同时开着（见 SingleInstanceGuard）——标题上标一下，
        // 免得两个长得一样的窗口分不清谁是谁，把调试版当成日常用的那个去点抓取。
        Title += "　【DEBUG 调试版·数据目录在 bin 下】";
#endif
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
    // 方向 ComboBox), not just the TabControl itself — the TabItem type check below is what
    // filters those out, not the sender.
    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem { Header: "自选股" })
            vm.WatchlistTab.Reload(); // picks up anything added from another tab this session
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem { Header: "主动仓" })
            vm.TradePoolTab.Reload(); // 同上——刚从"自选股"/"查询"页加进主动仓的票，切过来就能看到
        // 观察项**只在第一次切进来时自动读**（见 EnsureLoaded）：读一轮要扫每只票 7 张表 +
        // 算一次全市场均线广度，每次切页都跑会顿一下。要最新的点那页的【刷新】。
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is TabItem { Header: "观察项" })
            vm.WatchTab.EnsureLoaded();
        // 每日晨检不在切Tab时自动体检（读全库+逐只算、会顿一下）——改成纯手动，用户点该Tab里的"刷新"按钮才算，
        // 这样开程序秒开、切Tab也不卡（2026-07-31 按用户要求从"启动/切Tab自动跑"改为全手动）。
        // 同理：主动仓成员变动也不自动触发晨检重算（见 MainViewModel 里 TradePoolChanged 的接线）。
    }

    // ── 表格里点一格 = 开一个窗口（2026-09-15）──
    //
    // 原来每个列表页最右边都有一条"操作"列，里面一排按钮（分析详情▾ / 条件详情 / 交易记录 / 仓位…），
    // 而且每页塞的还不一样。现在改成**点数据本身**：列上写一句 local:CellAction.Kind（见 CellAction.cs），
    // 这里按 Kind 分发。操作列整条撤掉，只剩观察项的 📝 和因子清单的【说明】——那两样没有对应的
    // 数据列可点。
    //
    // 两个细节：
    // · 打开窗口推迟到本轮输入处理完之后（BeginInvoke）。在 Preview 阶段直接弹模态窗，会把
    //   DataGrid 自己的选中逻辑堵在后面，而板块页正是靠"选中某行"刷新右边的成分股列表。
    // · _cellActionRunning 防重入：ShowDialog 会起一个嵌套消息泵，双击时排在队列里的第二次点击
    //   会在那里面被执行，不拦就叠出第二个窗口。
    //
    // 不是可点列时行为照旧：让单元格在鼠标按下的 Tunneling 阶段就获得焦点，可编辑的文本单元格
    // （主动仓页那几列 ✎）一下点进去就能改，不用先点一次选中行。隐式 DataGridCell 样式，本窗口所有表通用。
    private bool _cellActionRunning;

    private void DataGridCell_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell) return;

        var kind = cell.Column is { } column ? CellAction.GetKind(column) : CellActionKind.None;
        if (kind != CellActionKind.None)
        {
            var row = cell.DataContext;
            // 八个筛选页的行都是 ResultRowViewModel，光看类型分不出是哪一页的条件详情，
            // 靠所在 DataGrid 的 x:Name 区分（见 OpenCriteria）。
            var gridName = FindVisualAncestor<DataGrid>(cell)?.Name ?? "";
            Dispatcher.BeginInvoke(new Action(() => RunCellAction(kind, row, gridName)),
                                   System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        if (cell is { IsFocused: false, IsEditing: false }) cell.Focus();
    }

    private void RunCellAction(CellActionKind kind, object? row, string gridName)
    {
        if (_cellActionRunning) return;
        if (DataContext is not MainViewModel vm) return;

        _cellActionRunning = true;
        try
        {
            switch (kind)
            {
                // 【代码】列：分析详情（左财务 / 右上观察项 / 右下趋势）。
                // 不拦非个股——指数/ETF 没有财务报表，但观察项和趋势照常有，左栏如实写一句没数据就行。
                case CellActionKind.Detail:
                    if (TryGetCodeName(row, out var code, out var name)) FinancialAnalysisWindow.Open(this, vm, code, name);
                    break;

                // 【名称】列：行情详情（K线）。
                case CellActionKind.Quote:
                    if (TryGetCodeName(row, out var quoteCode, out var quoteName)) OpenQuoteDetail(quoteCode, quoteName);
                    break;

                // 板块榜的【板块】列：那一行不是个股，看的是本地按成分股等权合成的板块指数
                // （code = gn_xxx / new_xxx，见 BoardIndexSynthesizer）。
                case CellActionKind.BoardQuote:
                    if (row is BoardRowViewModel board) OpenQuoteDetail(board.BoardCode, board.Name);
                    break;

                case CellActionKind.Criteria:
                    OpenCriteria(vm, row, gridName);
                    break;

                // 【买入】【卖出】列（主动仓）和【建仓进度】列（底仓）——同一个成交录入窗口，
                // 两页各写各的文件（watchlist.json / core-positions.json）。
                case CellActionKind.Lots:
                    if (row is WatchlistRowViewModel lotsRow) OpenTradeLots(vm, lotsRow);
                    else if (row is CorePositionRowViewModel coreRow) OpenCorePositionLots(vm, coreRow);
                    break;

                // 【持仓股数】列：仓位计算器，现价和当前持仓自动带进去。
                case CellActionKind.Sizing:
                    if (row is WatchlistRowViewModel sizingRow) OpenPositionSizing(vm, sizingRow);
                    break;

                // 底仓页【连续分红】列：历年每股派息柱状图——底仓没有价格止损，分红中断就是退出信号。
                case CellActionKind.Dividend:
                    if (row is CorePositionRowViewModel dividendRow) OpenDividendHistory(dividendRow);
                    break;
            }
        }
        finally
        {
            _cellActionRunning = false;
        }
    }

    /// <summary>
    /// 条件详情——"这只票当初为什么被选出来"。八个筛选页各有各的图（图上的指标跟判断依据必须对得上，
    /// 不能互相借用，见 <see cref="OpenGoldenCrossCriteria"/> 里那段教训），行类型又都是
    /// <see cref="ResultRowViewModel"/>，所以靠所在 DataGrid 的 x:Name 区分是哪一页。
    /// 自选股 / 主动仓 / 每日晨检三页看的是**加入自选那一刻的条件快照**，走另一条路。
    /// </summary>
    private void OpenCriteria(MainViewModel vm, object? row, string gridName)
    {
        switch (row)
        {
            case WatchlistRowViewModel watchRow:          // 自选股页的【方法】列 + 主动仓页的【来源方法】列
                OpenWatchlistCriteria(vm, watchRow);
                return;

            case MorningStockRowViewModel morningRow:      // 每日晨检页的【方法】列
                OpenMorningCriteria(vm, morningRow);
                return;

            case ResultRowViewModel result:
                switch (gridName)
                {
                    case "ShortTermGrid": OpenShortTermCriteria(result); break;
                    case "CorePositionGrid": OpenCorePositionScreenCriteria(result); break;
                    case "TriangleConvergenceGrid": OpenTriangleConvergenceCriteria(vm, result); break;
                    case "FoundationGrid": OpenFoundationCriteria(vm, result); break;
                    case "BottomReboundGrid": OpenBottomReboundCriteria(vm, result); break;
                    case "MidCapPullbackGrid": OpenMidCapPullbackCriteria(vm, result); break;
                    case "GoldenCrossGrid": OpenGoldenCrossCriteria(vm, result); break;
                    case "RisingLowsGrid": OpenRisingLowsCriteria(vm, result); break;
                    // 新加了筛选页却忘了在这里接一行的话会走到这——说出来，别让它静静地点不动。
                    default:
                        MessageBox.Show(this, $"这一页（{gridName}）还没有接条件详情。", "无法显示详情",
                                        MessageBoxButton.OK, MessageBoxImage.Information);
                        break;
                }
                return;
        }
    }

    /// <summary>
    /// 【每日晨检】页点【方法】那一格。晨检的行是拿主动仓记录现算出来的、自己不带条件快照，
    /// 所以回主动仓那张表里按代码找回原始记录。一只票被多个方法同时选中时（方法列显示成
    /// "金叉法、短线法"），弹个菜单让人挑看哪一个，而不是替他选第一个。
    /// </summary>
    private void OpenMorningCriteria(MainViewModel vm, MorningStockRowViewModel row)
    {
        var entries = vm.TradePoolTab.Entries.Where(r => r.Code == row.Code).ToList();
        if (entries.Count == 0)
        {
            MessageBox.Show(this, "主动仓里找不到这只票的自选记录，没有可显示的条件快照。", "无法显示详情",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (entries.Count == 1)
        {
            OpenWatchlistCriteria(vm, entries[0]);
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        foreach (var entry in entries)
        {
            var item = new MenuItem { Header = entry.Method };
            var target = entry;
            item.Click += (_, _) => OpenWatchlistCriteria(vm, target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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

    private void OpenFoundationCriteria(MainViewModel vm, ResultRowViewModel row)
    {
        if (!TryGetBars(vm, row, out var bars)) return;
        new DetailWindow(row.Result, bars, vm.FoundationTab.Lookback) { Owner = this }.ShowDialog();
    }

    private void OpenGoldenCrossCriteria(MainViewModel vm, ResultRowViewModel row)
    {
        // 金叉法的详情图（GoldenCrossChartBuilder）画的是它自己7条规则用到的指标（MA5/MA10/MACD/
        // KDJ/RSI/成交量），跟峰哥法那套K线+BOLL+MACD的DetailWindow是两回事，不能共用——共用会导致
        // 图上的指标和判断依据文字对不上（例如峰哥法那条参考线用收盘价，金叉法条件7用的是最高价）。
        if (!TryGetBars(vm, row, out var bars)) return;
        new GoldenCrossDetailWindow(row.Result, bars) { Owner = this }.ShowDialog();
    }

    private void OpenBottomReboundCriteria(MainViewModel vm, ResultRowViewModel row)
    {
        if (!TryGetBars(vm, row, out var bars)) return;
        new BottomReboundDetailWindow(row.Result, bars, vm.BottomReboundTab.DifThreshold) { Owner = this }.ShowDialog();
    }

    private void OpenMidCapPullbackCriteria(MainViewModel vm, ResultRowViewModel row)
    {

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

    // 回调法的"条件详情"故意用纯文字，不复用任何一张详情图：它的6个条件里有两条是财务
    // （净利润/经营现金流），两条是可操作性（成交额/一手金额），现有的详情图都画不出来；
    // 硬套一张图会让"图上指标"和"判断依据文字"对不上（见 OpenGoldenCrossCriteria 的教训）。
    // 想看K线走势点旁边的"行情详情"即可。
    private void OpenCorePositionScreenCriteria(ResultRowViewModel row)
    {

        if (row.Error != null)
        {
            MessageBox.Show(this, row.Error, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 用带图的专用窗口：历年派息/净利画成柱状图，条件文字在下半部分。
        // 这两项原来是结果表里的两串文字，列宽不够也读不出重点，见 CorePositionChartBuilder。
        new CorePositionCriteriaWindow(row.Result) { Owner = this }.ShowDialog();
    }

    private void OpenTriangleConvergenceCriteria(MainViewModel vm, ResultRowViewModel row)
    {
        if (!TryGetBars(vm, row, out var bars)) return;
        new TriangleConvergenceDetailWindow(row.Result, bars, vm.TriangleConvergenceTab.Lookback, vm.TriangleConvergenceTab.SwingWindow) { Owner = this }.ShowDialog();
    }

    private void OpenRisingLowsCriteria(MainViewModel vm, ResultRowViewModel row)
    {
        // 手工验证模式下图表数据也截到分析用的截止日期，否则图上画的锚点（用全量数据重新定位）
        // 会和当时的判定结果对不上
        if (!TryGetBars(vm, row, out var bars, vm.RisingLowsTab.AppliedCutoffDate)) return;
        new RisingLowsDetailWindow(row.Result, bars) { Owner = this }.ShowDialog();
    }

    // 短线法的"条件详情"——2026-08-07 规则替换后改用纯文字，不再复用金叉法的详情图。
    // 旧版9条正好是那张图画的指标子集，所以能共用；新版加了两条财务条件（净利/经营现金流）和
    // MA20位置，那张图都画不出来，硬套会让"图上指标"和"判断依据文字"对不上。想看走势点"行情详情"。
    private void OpenShortTermCriteria(ResultRowViewModel row)
    {

        if (row.Error != null)
        {
            MessageBox.Show(this, row.Error, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var r = row.Result;
        var header = $"{r.Code} {r.Name}　　数据日期 {r.DataDate:yyyy-MM-dd}　收盘 {r.LastClose:F2}\n" +
                     $"低于MA20 {r.SortScore:F2}%（列表按这个降序排）";
        var text = string.Join("\n\n", r.Criteria.Select(c =>
            $"{(c.DataMissing ? "⚠" : c.Satisfied ? "✓" : "✗")} {c.Name}\n    {c.Basis}"));
        TextDetailWindow.Show("短线法 — 条件详情", header, text, this);
    }

    private void OpenWatchlistCriteria(MainViewModel vm, WatchlistRowViewModel row)
    {
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
                    // 自选股里存的 Lookback 是加入自选那天用的回看根数；老记录（规则换之前加的）没有
                    // 这个值时按 1 处理 = 只看最新那根，跟现在的默认口径一致。
                    new DetailWindow(result, bars, entry.Lookback ?? 1) { Owner = this }.ShowDialog();
                    break;
                }
                case "金叉法":
                case "短线法": // 短线法复用金叉法的详情图，理由见 OpenShortTermCriteria
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
                case "查询":
                    // 查询Tab手工加入的自选没有分析条件快照，"条件详情"没有内容可展示——直接打开
                    // 纯行情图（跟"行情详情"同一个窗口），比弹"未知方法"警告更符合预期。
                    OpenQuoteDetail(entry.Code, entry.Name);
                    break;

                default:
                    // 因子法/底仓法，以及以后新加的方法——它们的判据不是K线指标，画不出条件图。
                    // 跟上面"查询"一个处理：打开纯行情图。弹一句"未知方法"对人没有任何用处。
                    OpenQuoteDetail(entry.Code, entry.Name);
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "无法显示详情", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// 从一行的 DataContext 里取出股票代码和名称。各列表页的行类型都不一样，但都有 Code/Name，
    /// 所以在这里统一拆一次，省得每页各写一个处理器。
    ///
    /// 一律返回 true（只要拿得到非空代码）：指数/ETF/板块的 K线是能看的，"有没有财务数据"
    /// 由【分析详情】窗口自己如实写，不在这儿拦。
    /// </summary>
    private static bool TryGetCodeName(object? row, out string code, out string name)
    {
        code = name = "";
        switch (row)
        {
            case ResultRowViewModel r: code = r.Code; name = r.Name; break;            // 八个筛选页
            case WatchlistRowViewModel w: code = w.Code; name = w.Name; break;         // 自选股 / 主动仓
            case CorePositionRowViewModel c: code = c.Code; name = c.Name; break;      // 底仓
            case QueryRowViewModel q: code = q.Code; name = q.Name; break;             // 查询（可能是指数/ETF/板块）
            case MorningStockRowViewModel m: code = m.Code; name = m.Name; break;      // 每日晨检
            case FactorPickRowViewModel f: code = f.Code; name = f.Name; break;        // 因子法名单
            case BoardMemberRowViewModel b: code = b.Code; name = b.Name; break;       // 板块热度·成分股
            case StockEventRow s: code = s.Code; name = s.Name; break;                 // 观察项
            default: return false;
        }
        return !string.IsNullOrWhiteSpace(code);
    }

    /// <summary>分裂按钮的箭头：弹出下拉菜单。
    /// 2026-09-15 起全窗口只剩【查询】页那个【加入主动仓 ▾】在用它（各列表页的分裂详情按钮
    /// 已经改成点代码/点名称）。
    /// ContextMenu 是独立的 popup，DataContext 不会自动跟着 PlacementTarget，必须手动传，
    /// 否则菜单项里取不到行。</summary>
    private void DetailDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.ContextMenu == null) return;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        b.ContextMenu.DataContext = b.DataContext;
        b.ContextMenu.IsOpen = true;
    }

    // "主动仓"Tab点【买入】或【卖出】那一格——录这只票的每一笔买入/卖出（金字塔式建仓、分批止盈）。窗口里改的是
    // 拷贝，点保存才整份写回 watchlist.json；取消什么都不动。存完刷新两个列表：持仓状态变了会影响
    // "主动仓"的排序（持仓优先）和"自选股"页的"在主动仓"标记。晨检不在这里自动重算——跟主动仓成员
    // 变动一样，要等用户主动点【刷新】（见本文件上方的接线说明）。
    private void OpenTradeLots(MainViewModel vm, WatchlistRowViewModel row)
    {

        var dialog = new TradeLotsWindow($"{row.Name}（{row.Code}）交易记录", row.Entry.Lots, vm.TradePoolTab.FeeStore) { Owner = this };
        bool saved = dialog.ShowDialog() == true;

        // 费率是在这个窗口里改的、账户级全局生效——改过就得刷新，**哪怕用户点了取消**（取消只针对
        // 本只票的成交明细），否则整表的含费盈亏和止亏价还停在老费率上。
        if (!saved && !dialog.FeesChanged) return;
        if (saved) row.ApplyLots(dialog.Result);
        vm.TradePoolTab.Reload();
        vm.WatchlistTab.Reload();
    }

    // 【底仓】页点【建仓进度】那一格——逐档录入这只票的买入/卖出。窗口和费率跟"主动仓"页完全共用
    // （TradeLotsWindow 本来就只接 IEnumerable<TradeLot>，不绑定具体哪种记录）。存完写回的是
    // core-positions.json 而不是 watchlist.json——两套持仓分开存，理由见 AnalyzerPaths.CorePositionPath。
    private void OpenCorePositionLots(MainViewModel vm, CorePositionRowViewModel row)
    {

        // 多传两个参数就会多出"目标年化股息 → 目标股数"那一行（2026-08-20 从底仓页表格挪进来的，
        // 见 TradeLotsWindow 构造函数的注释）。主动仓那边不传，那一行不出现。
        var dialog = new TradeLotsWindow(
            $"{row.Name}（{row.Code}）底仓成交记录", row.Entry.Lots, vm.CorePositionTab.FeeStore,
            targetAnnualDividend: row.Entry.TargetAnnualDividend,
            dividendPerShare: row.DividendPerShare) { Owner = this };
        bool saved = dialog.ShowDialog() == true;

        // 同"主动仓"页：费率是账户级全局设置，在这个窗口里改过就得刷新，哪怕用户点了取消。
        if (!saved && !dialog.FeesChanged) return;
        if (saved)
        {
            vm.CorePositionTab.Store.UpdateLots(row.Entry.Id, dialog.Result);
            if (dialog.TargetAnnualDividendResult is { } target)
                vm.CorePositionTab.Store.UpdateTargetDividend(row.Entry.Id, target);
        }
        vm.CorePositionTab.Reload();
    }

    // 【底仓】页点【连续分红】那一格——这只票的历年每股派息柱状图（全部历史）。持仓页没有"条件详情"
    // （它不是筛选结果），但"这家公司分红是一贯的还是最近才开始的"对底仓同样是核心判断，
    // 所以给它留了这个入口，复用筛选页那张图。
    private void OpenDividendHistory(CorePositionRowViewModel row)
    {
        new DividendHistoryWindow(row.Code, row.Name, row.AnnualDividends) { Owner = this }.ShowDialog();
    }

    // 【代码规则】——顶部数据状态那一行：跟具体页签、具体票都无关的公共查询表，所以不带任何参数。
    private void CodeRuleButton_Click(object sender, RoutedEventArgs e)
        => new CodeRuleWindow { Owner = this }.ShowDialog();

    // 【仓位计算器】——"主动仓"页顶部那个按钮：不针对具体某只票，纯算"这样一笔机会该下多少注"。
    // 针对某一只的入口是点那行的【持仓股数】格，见 OpenPositionSizing。
    private void PositionSizingButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        new PositionSizingWindow(vm.SizingStore) { Owner = this }.ShowDialog();
    }

    // 【仓位计算器】——点行上的【持仓股数】那一格：把这只票的现价（最新收盘）和当前持仓股数带进去，
    // 直接算出"该减多少股/还能加多少股"。窗口只做计算不改数据，所以关掉后不用刷新列表。
    private void OpenPositionSizing(MainViewModel vm, WatchlistRowViewModel row)
    {

        new PositionSizingWindow(vm.SizingStore, $"{row.Name}（{row.Code}）",
            row.LatestClose, row.Entry.RemainingShares,
            vm.BarRepository, row.Code,
            row.LatestCloseDate, row.NetAvgCost,
            vm.TradePoolTab.FeeStore.Current) { Owner = this }.ShowDialog();
    }

    // 因子法Tab因子清单的"说明"——弹窗显示该因子的构造/方向/作用/逐年IC（见 FactorDetailWindow）。
    private void FactorExplainButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FactorRowViewModel row) return;
        new FactorDetailWindow($"{row.Name}（{row.Category} / {row.Role}）", row.DetailText) { Owner = this }.ShowDialog();
    }

    // 晨检页"大盘总开关"里双击某个指数 → 看它自己的K线（2026-09-01 新增）。表里只给了收盘和
    // 两条均线的数字，"线下37个交易日"这种结论光看数字判断不了是横着磨还是一路阴跌，得看图。
    // 指数在库里是带前缀的8位符号，直接喂给同一个行情详情窗口即可；叠加大盘那个勾对指数会
    // 自动失效（MarketClassifier 认不出8位符号 → PickFor 返回 null），跟板块指数的行为一致。
    private void IndexLightRow_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 双击表头、滚动条、空白处同样会冒泡到 DataGrid，所以从点中的元素往上找真正的行；
        // 找不到就当没点（不能用 SelectedItem——那会拿到上一次选中的行，双击表头也弹窗）。
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement((DataGrid)sender, src) is not DataGridRow { Item: IndexLightRowViewModel row }) return;
        if (row.Code.Length > 0) OpenQuoteDetail(row.Code, row.Name);
    }

    private void OpenQuoteDetail(string code, string name)
    {
        if (DataContext is not MainViewModel vm) return;
        // QuoteDetailWindow 自己按需查日/周/月线（粒度切换按钮见该窗口），这里只提前确认日线
        // 至少有数据，避免打开一个完全空白、什么都显示不出来的窗口。
        if (vm.BarRepository.Query(code, Granularity.Day).Count == 0)
        {
            MessageBox.Show(this, "没有找到该标的的日线数据。\n（若是板块指数，请先在 Fetcher 里\"合成板块指数\"并把数据库拷贝过来）", "无法显示行情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new QuoteDetailWindow(code, name, vm.BarRepository, vm.CurrentDbPath, vm.NoteStore) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// 【观察项】页那列 📝——打开这只票的分析笔记。
    ///
    /// 这是 L2「个人观点」的录入入口（2026-09-11）。观点写在 <c>notes/{code}.md</c> 里而不是
    /// 表格里直接编辑：笔记本来就是"数据能重算、判断不能"那类东西的去处
    /// （见 AnalyzerPaths.NotesDir），而且"要写成一段话存进文件"这件事本身就是个门槛，
    /// 能拦住随手许愿——观察项要过"可判定／能改变动作／有归属层"三条准入。
    /// </summary>
    private void WatchNote_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if ((sender as FrameworkElement)?.DataContext is not StockEventRow row) return;
        if (string.IsNullOrWhiteSpace(row.Code)) return;

        new StockNoteWindow(vm.NoteStore, row.Code, row.Name) { Owner = this }.ShowDialog();
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
