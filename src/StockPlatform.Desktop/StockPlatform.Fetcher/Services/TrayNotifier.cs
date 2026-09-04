using System.Windows.Threading;

namespace StockPlatform.Fetcher.Services;

/// <summary>
/// 往系统托盘弹一条气泡通知（2026-09-04）。
///
/// 加它的唯一原因：东财的拼图滑块验证只能人来过，而抓取常常是夜里或你在忙别的时候跑的——
/// 程序挂起等着你，你却不知道。日志里写得再清楚，没人看也是白写。
///
/// 用完就走：临时建一个 NotifyIcon、弹完气泡、半分钟后自己销毁，
/// 不跟主窗口那个常驻托盘图标抢位置（那个只在最小化时才出现，不能指望它在）。
/// </summary>
public static class TrayNotifier
{
    /// <summary>
    /// 弹一条气泡。<paramref name="title"/> 是标题，<paramref name="text"/> 是正文。
    /// 任何异常都吞掉——通知失败绝不该影响抓取本身。
    /// </summary>
    public static void Notify(string title, string text)
    {
        try
        {
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui == null) return;
            ui.InvokeAsync(() => Show(title, text));
        }
        catch { /* 通知而已，失败就算了 */ }
    }

    private static void Show(string title, string text)
    {
        try
        {
            // 单文件发布里 Assembly.Location 永远是空串，图标只能从 exe 路径取
            var exePath = Environment.ProcessPath
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "StockPlatform.Fetcher.exe");

            var icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath),
                Visible = true,
                Text = "A股历史数据获取程序",
                BalloonTipTitle = title,
                BalloonTipText = text,
                BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning,
            };
            icon.ShowBalloonTip(30_000);

            // 气泡是系统托管的，图标撤太早气泡也会跟着没。给足 40 秒再收。
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { icon.Visible = false; icon.Dispose(); } catch { }
            };
            timer.Start();
        }
        catch { /* 同上 */ }
    }
}
