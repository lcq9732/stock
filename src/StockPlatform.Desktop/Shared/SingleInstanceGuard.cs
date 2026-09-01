using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockPlatform.Desktop.Shared;

/// <summary>
/// 单实例守卫（2026-08-04新增）——同一个程序只允许开一个，第二次双击时把已经开着的那个窗口激活到
/// 前台，而不是再起一个进程。
///
/// 为什么需要：这两个程序启动要读 7GB+ 的本地库，历史上窗口最长要等 39 秒才出现（见 App.OnStartup
/// 里把状态统计改成后台的注释），用户以为没启动就再双击，结果开出好几个实例。多实例不只是碍眼——
/// 两个 Fetcher 同时抓取会往同一个 SQLite 写、互相锁表；两个 Analyzer 同时编辑 watchlist.json 会
/// 后写覆盖先写（那个文件是整体读-改-写的，见 JsonWatchlistStore）。所以这是数据安全问题，不是体验
/// 小事。
///
/// 这个文件被 Fetcher 和 Analyzer 两个 csproj 用 &lt;Compile Include&gt; 链接进去共用（不是各自复制
/// 一份）——它是 WPF/Win32 相关的界面层逻辑，放进 StockPlatform.Data 那种数据层项目不合适。
/// </summary>
public static class SingleInstanceGuard
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    /// <summary>持有到进程退出为止——一定要保存在静态字段里，否则 Mutex 被 GC 回收就等于释放了锁，
    /// 后面再启动的实例会以为自己是第一个。</summary>
    private static Mutex? _mutex;

    /// <summary>
    /// 尝试成为唯一实例。返回 true=可以继续启动；false=已经有一个在跑（此时已尽力把那个窗口激活到
    /// 前台，或者在它最小化到托盘时提示用户去托盘找），调用方应该立刻 Shutdown()。
    /// </summary>
    /// <param name="appId">互斥体名字，两个程序各用自己的（不能共用，否则开了Fetcher就开不了Analyzer）。</param>
    /// <param name="friendlyName">提示文字里显示的程序名。</param>
    public static bool TryAcquire(string appId, string friendlyName)
    {
#if DEBUG
        // ── Debug 构建不设守卫（2026-08-31 按用户要求）────────────────────────────
        // 开发时经常要**一边开着日常在用的 Release 版、一边起一个 Debug 版看改动效果**，
        // 守卫会把后者直接挡掉（弹"程序已在运行"然后退出），改动就没法当场验证。
        // 正式用的永远是 Release 版，那边守卫照旧。
        //
        // ⚠ 为什么这样是安全的：Debug 版的数据目录是它自己 bin 目录下的 data\
        //   （FetchPaths 默认取 AppContext.BaseDirectory\data），跟 publish\data 天然隔开，
        //   不会两个进程写同一个 current.sqlite。真要把 Debug 版指到正式数据上时，
        //   自己注意别同时抓取——类注释里说的锁表和覆盖 watchlist.json 那些风险依然成立。
        _ = appId;
        _ = friendlyName;
        return true;
#else
        // 用 Local\ 而不是 Global\：按登录会话隔离就够了，Global 需要更高权限、在某些环境会直接抛异常。
        _mutex = new Mutex(initiallyOwned: true, name: $"Local\\StockPlatform.{appId}.SingleInstance", out bool isFirst);
        if (isFirst) return true;

        // 已经有一个在跑——尽量把它切到前台，让用户看到"它其实已经开着了"。
        ActivateExistingWindow(friendlyName);
        _mutex.Dispose();   // 不是持有者，别留着
        _mutex = null;
        return false;
#endif
    }

    /// <summary>进程退出时释放（App.OnExit 调用）。没显式释放虽然进程结束也会自动放开，但显式做更清晰、
    /// 也避免调试器里进程挂着不退时锁一直被占。</summary>
    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* 不是持有者，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static void ActivateExistingWindow(string friendlyName)
    {
        var me = Process.GetCurrentProcess();
        var other = Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id);

        // MainWindowHandle 为 0 = 那个实例把窗口最小化到系统托盘了（Hide() + ShowInTaskbar=false，
        // 见 MainWindow.OnStateChanged），这时没有窗口句柄可激活，只能告诉用户去托盘点它。
        if (other == null || other.MainWindowHandle == IntPtr.Zero)
        {
            System.Windows.MessageBox.Show(
                $"{friendlyName} 已经在运行了。\n\n如果没看到窗口，它可能被最小化到了系统托盘——点右下角托盘里的图标即可恢复。",
                "程序已在运行", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        ShowWindow(other.MainWindowHandle, SW_RESTORE);
        SetForegroundWindow(other.MainWindowHandle);
    }
}
