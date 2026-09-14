using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;

namespace StockPlatform.Data.Remote.Cdp;

/// <summary>
/// 起一个**真 Chrome/Edge** 并连上它的调试端口（2026-09-14）。
///
/// ⚠ 这段逻辑跟 <c>ChromeCdpBoardPageScraper</c> 里的启动段是等价的两份——**故意的**。
/// 用户 2026-09-14 定的：资金流通道和板块通道各起各的浏览器（各自的端口、各自的 profile），
/// 不去动板块那份已经在生产上跑着的代码。抽公共基类要改板块的启动路径，而那条路径
/// 2026-09-06 才靠一堆实测参数调通（非 headless、不带 --enable-automation……），
/// 为省几十行去动它不划算。新代码放这儿，将来谁愿意迁板块过来，接口是现成的。
///
/// 两条通道共用同一个出口 IP，所以**配额是共享的**——调度侧别让它俩同时跑。
/// </summary>
public sealed class ChromeCdpLauncher(string userDataDir, int port, string? browserPath = null)
    : IAsyncDisposable
{
    private Process? _browser;

    /// <summary>连上之后的 CDP 连接；没起来就是 null。</summary>
    public CdpConnection? Cdp { get; private set; }

    public event Action<string>? OnStatus;

    /// <summary>起浏览器、连调试端口。成功返回 true；失败只报一句，不抛。</summary>
    /// <param name="firstUrl">启动时先落在哪个地址（给页面一个正常的 origin 和 Referer）。</param>
    public async Task<bool> StartAsync(string firstUrl, CancellationToken ct = default)
    {
        try
        {
            var exe = browserPath ?? FindBrowser();
            if (exe == null)
            {
                OnStatus?.Invoke("⚠ 没找到 Chrome 或 Edge，浏览器通道用不了。");
                return false;
            }

            Directory.CreateDirectory(userDataDir);

            // 参数跟板块通道一字不差（2026-09-06 实测出来的）：
            //   · 不加 --headless：无头渲染管线不一样，而且更容易被认出来；
            //   · 不加 --enable-automation：那会把 navigator.webdriver 置为 true，是反爬最好认的标记；
            //   · --no-first-run / --no-default-browser-check：新 profile 别弹欢迎页。
            var args = string.Join(' ',
                $"--remote-debugging-port={port}",
                $"--user-data-dir=\"{userDataDir}\"",
                "--no-first-run",
                "--no-default-browser-check",
                "about:blank");

            _browser = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            if (_browser == null) { OnStatus?.Invoke("⚠ 浏览器启动失败。"); return false; }

            var wsUrl = await WaitForPageTargetAsync(ct);
            if (wsUrl == null)
            {
                OnStatus?.Invoke($"⚠ 浏览器起来了但调试端口 {port} 连不上，浏览器通道用不了。");
                return false;
            }

            Cdp = new CdpConnection();
            await Cdp.ConnectAsync(wsUrl, ct);
            await Cdp.SendAsync("Page.enable", ct: ct);
            await NavigateAsync(firstUrl, ct);

            OnStatus?.Invoke($"浏览器通道已就绪：{Path.GetFileName(exe)}"
                           + $"（调试端口 {port}，profile 在 {userDataDir}）。");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"⚠ 浏览器通道启动失败（{ex.GetType().Name}：{ex.Message}）。");
            return false;
        }
    }

    /// <summary>导航并等 load 事件（最多 30 秒，等不到就算了——我们只要一个能跑 JS 的页面）。</summary>
    public async Task NavigateAsync(string url, CancellationToken ct = default)
    {
        if (Cdp == null) return;
        var loaded = new TaskCompletionSource();
        void OnLoad(System.Text.Json.JsonElement _) => loaded.TrySetResult();
        Cdp.On("Page.loadEventFired", OnLoad);
        await Cdp.SendAsync("Page.navigate", new { url }, ct);
        try { await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
        catch (TimeoutException) { /* 页面慢就慢，JS 照样能跑 */ }
    }

    /// <summary>常见安装位置里找 Chrome，找不到再找 Edge。</summary>
    public static string? FindBrowser()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<string?> WaitForPageTargetAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var targets = await http.GetFromJsonAsync<List<CdpTargetInfo>>(
                    $"http://127.0.0.1:{port}/json/list", ct);
                var page = targets?.FirstOrDefault(
                    t => t.type == "page" && !string.IsNullOrEmpty(t.webSocketDebuggerUrl));
                if (page != null) return page.webSocketDebuggerUrl;
            }
            catch { /* 还没起来，接着等 */ }
            await Task.Delay(500, ct);
        }
        return null;
    }

    private sealed class CdpTargetInfo
    {
        public string? type { get; set; }
        public string? url { get; set; }
        public string? webSocketDebuggerUrl { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Cdp != null) await Cdp.DisposeAsync();
        try
        {
            if (_browser is { HasExited: false })
            {
                _browser.Kill(entireProcessTree: true);
                _browser.WaitForExit(3000);
            }
        }
        catch { }
        _browser?.Dispose();
    }
}
