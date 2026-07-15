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

    public MainWindow()
    {
        InitializeComponent();
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

        var result = MessageBox.Show(
            "确定要关闭程序吗？",
            "确认关闭", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
}
