using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
// 两个项目都开了 UseWindowsForms=true（托盘图标要用 NotifyIcon），于是 System.Windows.Forms.Application
// 也在作用域里、跟这里要用的 WPF Application 同名——取别名消歧义（Fetcher 的 App.xaml.cs 同样处理）。
using Application = System.Windows.Application;

namespace StockPlatform.Desktop.Shared.Theme;

/// <summary>界面配色：浅色 / 深色 / 跟随 Windows 系统设置。</summary>
public enum ThemeMode
{
    Light,
    Dark,
    System,
}

/// <summary>
/// 界面主题的唯一入口（2026-08-29 新增）——两个程序（Fetcher / Analyzer）用同一份实现，靠
/// csproj 的 &lt;Compile Include&gt; 链接进去，跟 <see cref="SingleInstanceGuard"/> 同样的做法。
///
/// 怎么工作的：<see cref="Apply"/> 往 Application.Resources.MergedDictionaries 里换一组画刷字典
/// （Theme/Colors.Light.xaml 或 Colors.Dark.xaml），窗口 XAML 里所有颜色都写成
/// <c>{DynamicResource Theme.*}</c>，字典一换 WPF 自动重刷所有引用处——不用重启、不用重建窗口。
/// **必须是 DynamicResource**：StaticResource 在加载时就把值定死了，换字典不会跟着变。
///
/// 深色额外再合并 Theme/Controls.Dark.xaml（控件模板重写，见那个文件的头注释）；浅色不合并它，
/// 所以浅色下控件基本还是 WPF 原生外观。两个主题都会合并 Theme/Controls.Common.xaml——里面是
/// 表格列头/单元格那几个必须被列级 Style 用 BasedOn 引用的样式，浅色下它们也换成了纯色版
/// （原生是渐变），是这次唯一在浅色下也变了样子的地方，原因见那个文件的头注释。
///
/// 设置存在 exe 旁边的 <c>data/ui-theme.json</c>——两个 exe 装在同一个目录、data 目录也是同一个
/// （见 AnalyzerPaths 类注释），所以在任一个程序里改主题，另一个下次启动就跟着变。
/// </summary>
public static class ThemeManager
{
    private static readonly List<ResourceDictionary> Injected = new();
    private static bool _systemHookInstalled;

    /// <summary>用户选的模式（可能是"跟随系统"）。默认深色，见 <see cref="Load"/>。</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>当前实际是不是深色（Mode=System 时取决于 Windows 的设置）。</summary>
    public static bool IsDark => Mode == ThemeMode.Dark || (Mode == ThemeMode.System && IsSystemDark());

    /// <summary>主题变化后触发——图表这类不是靠 DynamicResource 上色的东西要在这里重画。</summary>
    public static event EventHandler? ThemeChanged;

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "data", "ui-theme.json");

    /// <summary>启动时调一次（App.OnStartup 里，创建窗口之前）——读上次选的主题并应用。</summary>
    public static void Initialize()
    {
        // 标题栏是**非客户区**、由 Windows 自己画，资源字典管不到，得单独用 DWM 属性通知系统
        // （见 ApplyTitleBar）。这里挂一个类处理器，之后每开一个窗口都会自动跟上。
        //
        // 用 SizeChanged 而不是 Loaded：Loaded/Unloaded 是 BroadcastEventHelper 广播的，
        // **类处理器对它无效**（做图表主题时踩过，见 ChartTheme 的类注释）；SizeChanged 是正常的
        // 路由事件，窗口首次布局时必定触发，那时 HWND 也已经建好了。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((sender, _) => { if (sender is Window w) ApplyTitleBar(w); }));

        Apply(Load(), persist: false);
    }

    /// <summary>切换主题并（默认）记住选择。</summary>
    public static void Apply(ThemeMode mode, bool persist = true)
    {
        Mode = mode;

        var app = Application.Current;
        if (app == null) return;

        var next = new List<ResourceDictionary>
        {
            LoadDictionary(IsDark ? "Theme/Colors.Dark.xaml" : "Theme/Colors.Light.xaml"),
            // 两个主题都要：里面是被列级 Style BasedOn 引用的那几个表格样式，见该文件头注释
            LoadDictionary("Theme/Controls.Common.xaml"),
        };
        if (IsDark)
        {
            next.Add(LoadDictionary("Theme/Controls.Dark.xaml"));
        }

        // 先加后删：中间不留"两份字典都不在"的空档，否则 DynamicResource 会短暂解析不到、
        // 控件闪一下默认色。
        foreach (var dictionary in next)
        {
            app.Resources.MergedDictionaries.Add(dictionary);
        }
        foreach (var old in Injected)
        {
            app.Resources.MergedDictionaries.Remove(old);
        }
        Injected.Clear();
        Injected.AddRange(next);

        // 跟随系统时才需要挂 Windows 的主题变更通知；挂上就不摘（用户可能来回切模式），
        // 回调里再判断当前是不是 System。
        if (mode == ThemeMode.System && !_systemHookInstalled)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _systemHookInstalled = true;
        }

        // 已经开着的窗口，标题栏也要跟着换（新开的窗口由上面那个 SizeChanged 类处理器负责）
        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
        }

        if (persist) Save(mode);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    // ===== 标题栏（非客户区） =====

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>标题栏当前是不是已经按深色画了——避免每次 SizeChanged（拖动窗口边缘时很密集）
    /// 都去重设一遍、白白触发一次非客户区重绘。</summary>
    private static readonly ConditionalWeakTable<Window, TitleBarState> TitleBars = new();

    private sealed class TitleBarState { public bool? Dark; }

    /// <summary>
    /// 让 Windows 把这个窗口的标题栏画成深色/浅色。
    ///
    /// 标题栏、窗口边框、右上角那三个按钮都是系统画的非客户区，WPF 的样式/画刷完全够不着；
    /// 唯一的正规入口是 DWM 的 <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> 属性。属性号在不同 Windows
    /// 版本上不一样：Win10 20H1(19041) 及以后 = 20，Win10 1809~1903 = 19，所以先试 20、失败再试 19。
    /// 更老的系统两个都会失败——那就保持系统默认的浅色标题栏，不影响其它部分。
    /// </summary>
    private static void ApplyTitleBar(Window window)
    {
        var state = TitleBars.GetOrCreateValue(window);
        bool dark = IsDark;
        if (state.Dark == dark) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;   // 还没 SourceInitialized，下次 SizeChanged 再来

        int value = dark ? 1 : 0;
        bool ok = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) == 0
                  || DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int)) == 0;
        if (!ok) return;

        state.Dark = dark;

        // 已经画出来的标题栏不会自己刷新，得让系统重画一次非客户区
        // （SWP_NOMOVE|SWP_NOSIZE|SWP_NOZORDER|SWP_FRAMECHANGED）
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0004 | 0x0020);
    }

    private static void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (Mode != ThemeMode.System) return;
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;

        // 这个回调来自系统线程，改资源字典必须回到 UI 线程
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(ThemeMode.System, persist: false)));
    }

    private static ResourceDictionary LoadDictionary(string relativePath)
        // 相对 Uri 会解析到当前程序集自己的资源（Fetcher/Analyzer 各有一份链接进去的同名 xaml）
        => new() { Source = new Uri(relativePath, UriKind.Relative) };

    /// <summary>读 Windows 的"默认应用模式"（设置 → 个性化 → 颜色）。读不到就当浅色。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ThemeMode Load()
    {
        try
        {
            // 没有设置文件 = 第一次跑：默认深色（2026-08-29 用户要求）
            if (!File.Exists(SettingsPath)) return ThemeMode.Dark;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("mode", out var element)
                && Enum.TryParse<ThemeMode>(element.GetString(), ignoreCase: true, out var mode))
            {
                return mode;
            }
        }
        catch
        {
            // 设置文件坏了不该拦住程序启动——退回默认
        }
        return ThemeMode.Dark;
    }

    private static void Save(ThemeMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { mode = mode.ToString() }));
        }
        catch
        {
            // 存不下就算了，主题本身已经切好了
        }
    }
}
