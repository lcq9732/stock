using System.ComponentModel;
using System.Windows;
// 同样是UseWindowsForms带来的歧义（见App.xaml.cs顶部注释）——MessageBox这个类名WPF/WinForms
// 都有，显式取别名，确保下面几处MessageBox.Show()用的还是WPF那个（跟现有调用方式保持一致）。
using MessageBox = System.Windows.MessageBox;
using StockPlatform.Fetcher.ViewModels;

namespace StockPlatform.Fetcher;

public partial class MainWindow : Window
{
    // 用WinForms的NotifyIcon（WPF自己没有托盘图标控件）——故意不加 using System.Windows.Forms;，
    // 全部用全名引用，避免跟已经在用的 System.Windows.MessageBox 等同名类型产生歧义。
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    /// <summary>
    /// 让右边日志的顶边跟左边【任务表】齐平。
    ///
    /// 为什么要用代码算：左边"任务表以上"的那截高度是 页签头 + 工具条 两段拼出来的，
    /// 它们在 TabControl 内部、跟右边这一列不在同一个 Grid 里，XAML 没法直接对齐
    /// （SharedSizeGroup 只能共享同一个 Grid 作用域里的行）。所以量一次实际位置最直接。
    ///
    /// 顺带解决的问题：这样右上角空出一块，计划的运行状态就搬到那儿了——
    /// 原来它挤在工具条末尾，长句子被切得只剩半句（2026-09-01 用户反馈）。
    /// </summary>
    private void SyncLogTop()
    {
        if (PlanTableBox == null || RootGrid == null || StatusPane == null) return;
        if (!PlanTableBox.IsVisible) return;        // 切到【手动】页时任务表不可见，保持上一次的高度
        try
        {
            double y = PlanTableBox.TranslatePoint(new System.Windows.Point(0, 0), RootGrid).Y;
            if (y <= 0 || y >= RootGrid.ActualHeight) return;

            // ⚠ 设的是 Min/MaxHeight，不是行高（2026-09-05 改）。原来这里写
            //    LogTopRow.Height = y，等于把状态区**钉死**在这个高度上：并发之后
            //    「正在执行」是几行取决于同时跑着几项，多出来的行被这条固定高度直接切掉——
            //    用户截图里只跑着一项，那一行就已经被切得只剩上半截。现在 y 只当下限，
            //    内容更多时这一行（Height=Auto）自己长高，日志相应短一点。
            if (Math.Abs(StatusPane.MinHeight - y) > 0.5) StatusPane.MinHeight = y;
            // 上限兜底：同时跑起十几项时不能把日志挤没，超过这个高度才让状态区自己滚。
            double max = Math.Max(y, RootGrid.ActualHeight * 0.4);
            if (Math.Abs(StatusPane.MaxHeight - max) > 0.5) StatusPane.MaxHeight = max;
        }
        catch { /* 布局还没算完，下一轮 LayoutUpdated 会再来 */ }
    }

    public MainWindow()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => SyncLogTop();   // 见 SyncLogTop：右边日志顶边对齐左边任务表
#if DEBUG
        // Debug 构建可以跟正在用的 Release 版同时开着（见 SingleInstanceGuard）——标题上标一下，
        // 免得两个长得一样的窗口分不清谁是谁，把调试版当成日常用的那个去点抓取。
        Title += "　【DEBUG 调试版·数据目录在 bin 下】";
#endif
        // 默认最大化（2026-08-31 按用户要求）。加了【计划】页之后窗口里要放的东西多了，
        // 1240×780 的默认尺寸得横着拖一下才看得全。
        // ⚠ 跟 Analyzer 一样**延到 Loaded 里设**：在构造函数或 XAML 里直接写 WindowState=Maximized
        //    不一定生效，WPF 要先完成一次布局才认。Width/Height 留着当"还原"后的尺寸。
        Loaded += (_, _) => WindowState = WindowState.Maximized;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;
    }

    /// <summary>
    /// 关闭前的两道保护（2026-07-08）：正在抓取时直接拦下并提示先点"停止"（抓取线程还在跑，
    /// 直接关窗口没法安全终止网络请求/数据库写入）；不在抓取时也要求用户再确认一次，防止误触
    /// 关闭按钮/Alt+F4丢失当前的运行日志或状态。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        if (vm?.IsBusy == true)
        {
            MessageBox.Show(
                "正在拉取数据，请先点击\"停止\"，再关闭程序。",
                "无法关闭", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }

        // 计划在跑（哪怕此刻只是在等下一项到点）也要说清楚：关掉程序计划就不再继续了。
        // 无人值守靠的是进程活着——用户多半是想最小化到托盘而不是真的退出。
        var question = vm?.IsPlanRunning == true
            ? "计划正在执行中，关闭程序后就不会再自动继续了"
              + "（想让它继续跑，请改成最小化到系统托盘）。\n\n确定要关闭吗？"
            : "确定要关闭程序吗？";
        var result = MessageBox.Show(question, "确认关闭", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 真正关闭时才释放托盘图标（不是取消关闭的时候）——留着不释放会在系统托盘留下一个
        // 点不动的幽灵图标，直到资源管理器重启才会消失。
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    /// <summary>
    /// 最小化到系统托盘（2026-07-09新增）——用户反馈希望最小化后窗口从任务栏消失、缩到托盘里，
    /// 这样任务栏上就没有一个容易被误点关闭的窗口了；真正退出仍然要走 OnClosing 那两道确认（点
    /// 托盘图标右键菜单的"退出程序"会调用 Close()，一样会触发确认，不会绕过去）。
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
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "StockPlatform.Fetcher.exe");
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
            menu.Items.Add("退出程序", null, (_, _) => Close());

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "A股历史数据获取程序",
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
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    // ══ 计划表的拖放排序（2026-08-31 按用户要求，取代原来的上移/下移按钮）══════════
    //
    // 顺序就是执行顺序，所以"调顺序"是这张表上最常做的编辑之一，用拖的比选中再点两下自然。
    // 只做三件事：按下时记住起点、拖出阈值后启动拖放、放下时算出目标行号交给 ViewModel。
    // 真正的移动和存盘在 MainViewModel.MovePlanItem 里。

    /// <summary>按下时的位置和那一行——还不能立刻开拖，否则想点勾选框都会被当成拖动。</summary>
    private System.Windows.Point _dragStart;
    private object? _dragItem;

    private void PlanGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = RowItemAt(e.OriginalSource as DependencyObject);
    }

    private void PlanGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragItem == null) return;

        // 超过系统的最小拖动距离才算拖——不然单击单元格里的勾选框/下拉框会被误判成拖动
        var now = e.GetPosition(null);
        if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;                  // 一次拖动只触发一次
        // 拖动源是**这一组的那张表**（2026-09-02 分组之后每组一张），不再是全局那一个 PlanGrid
        if (sender is System.Windows.DependencyObject src)
            System.Windows.DragDrop.DoDragDrop(src, item, System.Windows.DragDropEffects.Move);
    }

    /// <summary>
    /// 组内换位（2026-09-02 分组之后）。**只在同一组内挪**：拖到别的组上不动作——
    /// 跨组挪意味着换重复规则和触发时刻，不该由一次拖动悄悄完成。
    /// </summary>
    private void PlanGrid_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Data.GetData(typeof(PlanItemViewModel)) is not PlanItemViewModel dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not PlanGroupViewModel group) return;

        int from = group.Items.IndexOf(dragged);
        if (from < 0) return;   // 从别的组拖过来的，忽略

        // 落点在哪一行上就插到哪一行的位置；落在空白处（表格下方）就放到最后
        var target = RowItemAt(e.OriginalSource as DependencyObject) as PlanItemViewModel;
        int to = target != null ? group.Items.IndexOf(target) : group.Items.Count - 1;
        vm.MovePlanItem(group, from, to);
    }

    /// <summary>
    /// 滚轮落在任务表上要能滚动整页（2026-09-03 用户反馈："鼠标中键在 Grid 上是 Scroll 不了，
    /// 需要放到 Scroll Bar 才可以"）。
    ///
    /// 为什么会这样：DataGrid 自带一个 ScrollViewer，滚轮事件一进它就被吃掉了；而这些表
    /// 摆在外层 ScrollViewer 里、拿到的是无限高度，自己**根本不需要滚**——于是事件既没人用，
    /// 也不会往上冒泡，鼠标只要停在表格上滚轮就没反应。分组之前整页只有一张表、它自己就是
    /// 滚动的那个，所以没露出来。
    ///
    /// 这里把滚轮直接转交给外面那个 ScrollViewer。一格 <c>e.Delta</c>（120px，约四行）
    /// 跟浏览器手感一致，比 WPF 默认的三行要跟手。
    /// </summary>
    private void PlanGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var outer = OuterScrollViewer(sender as DependencyObject);
        if (outer == null) return;
        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>从某个元素往上找第一个 ScrollViewer——DataGrid **内部**那个是它的子级，不会被找到。</summary>
    private static System.Windows.Controls.ScrollViewer? OuterScrollViewer(DependencyObject? from)
    {
        int guard = 0;
        while (from != null && guard++ < 200)
        {
            from = System.Windows.Media.VisualTreeHelper.GetParent(from);
            if (from is System.Windows.Controls.ScrollViewer sv) return sv;
        }
        return null;
    }

    /// <summary>
    /// 从鼠标落到的那个元素往上找它属于哪一行，返回那一行绑定的对象。
    ///
    /// ⚠ 不能一路只用 VisualTreeHelper.GetParent：它**只接受 Visual/Visual3D**。
    /// 点在 &lt;Run&gt; 这类 Inline 上时（ⓘ 弹出的说明里就有几个 Run），路由事件送过来的
    /// OriginalSource 是 TextElement，不是 Visual，直接调用会抛异常——而这是个事件处理器，
    /// 异常没人接，整个程序就退出了（2026-09-01 用户反馈："点 ⓘ 显示出来后再点那内容，程序自己退出"）。
    ///
    /// 所以遇到非 Visual 就先走**逻辑树**往上跳一层（Run → TextBlock），回到 Visual 之后再继续。
    /// </summary>
    private static object? RowItemAt(DependencyObject? source)
    {
        int guard = 0;                     // 万一逻辑树/视觉树接不上，别转成死循环
        while (source != null && source is not System.Windows.Controls.DataGridRow && guard++ < 200)
        {
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return (source as System.Windows.Controls.DataGridRow)?.Item;
    }
}
