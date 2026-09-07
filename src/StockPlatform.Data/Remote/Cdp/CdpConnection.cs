using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StockPlatform.Data.Remote.Cdp;

/// <summary>
/// Chrome DevTools Protocol 的一个**最小客户端**（2026-09-06）——够我们驱动一个真浏览器就行。
///
/// ════ 为什么自己写而不是上 Playwright ════
/// 我们只需要四件事：导航、执行 JS、监听请求、取响应体。CDP 就是一条 WebSocket 上收发 JSON，
/// .NET 自带 <see cref="ClientWebSocket"/> 和 <c>System.Text.Json</c>，**一个 NuGet 都不用加**。
/// 换 Playwright 要给程序背上几十 MB 的 Node 驱动，而且它默认带 <c>--enable-automation</c>，
/// 会让页面里的 <c>navigator.webdriver</c> 变成 true——这正是反爬最容易认出来的标记。
/// 纯 CDP 连一个正常启动的浏览器不设这个标记。
///
/// ════ 协议就这么点东西 ════
/// · 发：<c>{"id":1,"method":"Page.navigate","params":{...}}</c>，按 id 收回执；
/// · 收：没有 id 的就是**事件**（<c>{"method":"Network.responseReceived","params":{...}}</c>）。
/// 所以下面只有两张表：等回执的 <see cref="_pending"/>，和订阅事件的 <see cref="_handlers"/>。
///
/// ⚠ 线程模型：只有一个后台读循环在收消息，回执用 TaskCompletionSource 交回调用方，
/// 事件回调**在读循环线程上同步执行**——所以事件处理器里别做慢活，更别在里面等另一个 CDP 命令
/// （那会把读循环堵死、自己等自己）。取响应体那种需要发命令的，扔到 Task.Run 里去做。
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _readCts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<Action<JsonElement>>> _handlers = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _readLoop;
    private int _nextId;

    /// <summary>单条命令等回执的上限。CDP 正常都是毫秒级，等这么久还没回多半是页面卡死了。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public async Task ConnectAsync(string webSocketUrl, CancellationToken ct = default)
    {
        await _ws.ConnectAsync(new Uri(webSocketUrl), ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    /// <summary>订阅一个 CDP 事件（如 <c>Network.responseReceived</c>）。</summary>
    public void On(string method, Action<JsonElement> handler)
    {
        var list = _handlers.GetOrAdd(method, _ => []);
        lock (list) list.Add(handler);
    }

    /// <summary>发一条命令、等它的回执。<paramref name="parameters"/> 传 null 表示没有参数。</summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new { },
        });

        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
        }
        finally { _sendLock.Release(); }

        try { return await tcs.Task.WaitAsync(CommandTimeout, ct); }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>
    /// 在页面里跑一段 JS，把它的返回值当字符串拿回来。
    ///
    /// <c>returnByValue</c> 必须为 true：不然 CDP 只回一个远程对象句柄，还得再发一条命令去取值。
    /// 我们那几段脚本本来就都返回字符串（'yes'/'ok'/'not-found'/一段描述），正好。
    /// </summary>
    public async Task<string> EvaluateAsync(string expression, CancellationToken ct = default)
    {
        var res = await SendAsync("Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
        }, ct);

        if (res.TryGetProperty("exceptionDetails", out _)) return "";
        if (!res.TryGetProperty("result", out var inner)) return "";
        if (!inner.TryGetProperty("value", out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);        // 大响应体会分片，拼完再解析

                Dispatch(sb.ToString());
            }
        }
        catch (OperationCanceledException) { /* 正常收工 */ }
        catch (Exception ex)
        {
            // 连接断了：把所有等回执的都叫醒，否则它们会一直等到 CommandTimeout
            foreach (var kv in _pending) kv.Value.TrySetException(ex);
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 有 id ＝ 命令的回执
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                if (!_pending.TryRemove(id, out var tcs)) return;
                if (root.TryGetProperty("error", out var err))
                    tcs.TrySetException(new InvalidOperationException($"CDP 报错：{err}"));
                else
                    tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                return;
            }

            // 没有 id ＝ 事件
            if (!root.TryGetProperty("method", out var m)) return;
            var method = m.GetString();
            if (method == null || !_handlers.TryGetValue(method, out var list)) return;

            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            Action<JsonElement>[] snapshot;
            lock (list) snapshot = [.. list];
            foreach (var h in snapshot)
            {
                try { h(p); } catch { /* 一个处理器出错不能把读循环带停 */ }
            }
        }
        catch (JsonException) { /* 不认识的消息忽略 */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _readCts.CancelAsync(); } catch { }
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        try { if (_readLoop != null) await _readLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _ws.Dispose();
        _readCts.Dispose();
        _sendLock.Dispose();
    }
}
