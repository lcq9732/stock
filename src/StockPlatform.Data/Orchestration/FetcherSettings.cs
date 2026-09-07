using System.Text.Json;
using System.Text.Json.Nodes;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// <c>data/fetcher-settings.json</c> 的读写（2026-09-05 统一到这儿）。路径见
/// <see cref="FetchPaths.SettingsPath"/>。
///
/// ════ 为什么是 JSONC（带注释的 JSON）════
/// 这个文件是**给人手改的**——界面上没有这些开关，改配置就是改它。可原来它是一份光秃秃的
/// JSON，人打开只看得见自己填过的那几个键，根本不知道还能配什么。所以现在：
///   · 读取一律允许 <c>//</c> 注释和末尾逗号（<see cref="Options"/>）；
///   · 文件不存在/空/坏掉时，<see cref="EnsureTemplate"/> 写一份把所有可配项连同说明都列出来的
///     模板，而且**每个可选值都写成一行完整的、去掉 <c>//</c> 就能用的配置**——
///     照着复制就完成配置，不用去猜键名怎么拼、值该填什么。
///
/// ════ ⚠ 程序基本不写这个文件，只有一个例外 ════
/// 唯一会改写它的是 <see cref="RemoveKey"/>（老版本设置迁移完删掉老键，一次性的）。
/// 那一下会**丢掉注释**——JSON 序列化器不保留注释，没有便宜的办法绕开。
/// 好在它只在文件里真的还留着那个老键时才触发，正常情况下永远不跑。
///
/// 在此之前那段代码是 <c>File.WriteAllText(path, "{}")</c>——**把整个文件清空**，
/// 用户配的 BarSource / Push2NetworkInterface 全没了，而且不报错。
/// 那多半就是这个文件后来变成 0 字节的原因。
/// </summary>
public static class FetcherSettings
{
    /// <summary>人手改的配置就该容得下注释和末尾多余的逗号。</summary>
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>读一个字符串设置。读不到、文件坏了、值不是字符串，一律返回 null。</summary>
    public static string? ReadString(string settingsPath, string key)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            var text = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text, Options);
            return doc.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch
        {
            // 配置读不了就用默认，不该拦住程序启动
            return null;
        }
    }

    /// <summary>
    /// 板块成分股走哪条通道，规范化成 <c>"page"</c>／<c>"browser"</c>／<c>"http"</c>／
    /// <c>"terminal"</c>，没配就是 <c>"page"</c>（2026-09-05 起的默认）。
    ///
    /// 单独开一个方法是因为这个值有**两个**读它的地方：造 fetcher 的那段（App.CreateBoardFetcher）
    /// 和【重新读取配置】在日志里报告"现在生效的是哪条"。两边各写一遍 <c>?? "http"</c> 加
    /// trim/lower 的话，早晚会出现"日志说 http、实际造的是 browser"这种最难查的不一致。
    /// </summary>
    public static string ReadBoardChannel(string settingsPath) =>
        (ReadString(settingsPath, "BoardMemberChannel") ?? "page").Trim().ToLowerInvariant();

    /// <summary>
    /// 成分股接口走哪个域名；没配就返回 null，由
    /// <c>EastMoneyBoardFetcherBase.DefaultMemberHost</c>（pushguest）兜底。
    ///
    /// 留这个开关是为了出事能一行配置退回 <c>push2.eastmoney.com</c>——
    /// pushguest 是 2026-09-05 才换上的，连续几百个请求会不会被限流还没验证过。
    /// </summary>
    public static string? ReadBoardMemberHost(string settingsPath)
    {
        var v = ReadString(settingsPath, "BoardMemberHost")?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// 东财终端那份板块成分股文件在哪；没配返回 null，由
    /// <c>EastMoneyTerminalBoardFile.DefaultPath</c> 兜底。只有通道选 terminal 时才用得上。
    /// </summary>
    public static string? ReadTerminalBoardFile(string settingsPath)
    {
        var v = ReadString(settingsPath, "TerminalBoardFile")?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>
    /// 终端文件多久没刷新就判定不可用（天）；没配或配了非法值返回 null，
    /// 由 <c>EastMoneyTerminalBoardFile.DefaultMaxAge</c>（3 天）兜底。
    ///
    /// 留这个开关是因为"多久算旧"取决于跑批频率：天天跑的话 3 天足够宽松；
    /// 要是只在周末跑一次，就得放到 8 天以上，否则每次都判定过期、成分股永远不更新。
    /// </summary>
    public static TimeSpan? ReadTerminalMaxAge(string settingsPath)
    {
        var v = ReadString(settingsPath, "TerminalBoardMaxAgeDays")?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? TimeSpan.FromDays(d) : null;
    }

    /// <summary>
    /// 分档资金流走哪条通道，规范化成 <c>"both"</c>／<c>"snapshot"</c>／<c>"perstock"</c>，
    /// 没配就是 <c>"both"</c>。
    ///
    ///   both     ＝ 先拉全市场当日快照（push2delay，约 60 个请求），再用剩下的时间逐股补历史。
    ///   snapshot ＝ 只要当天的，不补历史。历史已经补齐之后可以切到这个，一天几十秒。
    ///   perstock ＝ 只走逐股那条老路（push2his）。快照接口哪天挂了就用它顶着。
    ///
    /// 两条通道的数据 2026-09-06 逐条比对过、零差异，所以怎么组合都不会写出不一致的数据。
    /// </summary>
    public static string ReadMoneyFlowChannel(string settingsPath) =>
        (ReadString(settingsPath, "MoneyFlowChannel") ?? "both").Trim().ToLowerInvariant();

    /// <summary>
    /// 分档资金流快照打哪个域名；没配返回 null，由
    /// <c>EastMoneyMoneyFlowSnapshotProvider.DefaultHost</c>（push2delay）兜底。
    ///
    /// 留这个开关的理由跟 <see cref="ReadBoardMemberHost"/> 一样：东财这些镜像域名说没就没，
    /// 真出事时改一行配置就能换一个，不用等下一版程序。
    /// </summary>
    public static string? ReadMoneyFlowSnapshotHost(string settingsPath)
    {
        var v = ReadString(settingsPath, "MoneyFlowSnapshotHost")?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>读一个 true/false 设置；读不到就当 false。</summary>
    public static bool ReadBool(string settingsPath, string key)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            var text = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(text)) return false;
            using var doc = JsonDocument.Parse(text, Options);
            return doc.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind is JsonValueKind.True && v.GetBoolean();
        }
        catch { return false; }
    }

    /// <summary>
    /// 删掉一个键，**其余内容原样保留**。
    ///
    /// ⚠ 会丢注释（序列化器不保留），所以只在真的需要改文件时调——目前只有老设置迁移。
    /// 返回是否真的改了文件：没有那个键就什么都不做，也就不会白白把注释洗掉。
    /// </summary>
    public static bool RemoveKey(string settingsPath, string key)
    {
        try
        {
            if (!File.Exists(settingsPath)) return false;
            var text = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (JsonNode.Parse(text, nodeOptions: null, Options) is not JsonObject obj) return false;
            if (!obj.Remove(key)) return false;      // 没这个键：别动文件，注释就保住了

            File.WriteAllText(settingsPath,
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            // 删不掉的后果只是下次启动多迁一次，不值得打扰用户
            return false;
        }
    }

    /// <summary>
    /// 文件不存在、是空的、或者根本解析不了时，写一份带注释的模板。
    /// **已有内容一个字都不动**——哪怕里面只有一个键，那也是用户自己填的。
    /// </summary>
    public static void EnsureTemplate(string settingsPath)
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                var text = File.ReadAllText(settingsPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // 能解析就说明是份正经配置，不管里面有几个键都别碰
                    try { using var _ = JsonDocument.Parse(text, Options); return; }
                    catch { /* 解析不了＝坏文件，下面覆盖成模板 */ }
                }
            }

            var dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(settingsPath, Template);
        }
        catch
        {
            // 写不出来只是少了份说明，程序照常跑（所有设置都有代码里的默认值）
        }
    }

    /// <summary>
    /// 模板正文。**加新配置项时记得同步这里**——它是用户唯一能看到"有哪些配置"的地方。
    ///
    /// 写法上守两条：
    ///   ① 每个可选值单独一行、写成完整的 <c>"键": "值",</c>，**去掉行首 <c>//</c> 就能用**；
    ///   ② 当前生效的那一行不注释，其余同名行注释掉——人一眼看得出现在用的是哪个。
    /// 除 BoardMemberChannel 外全部注释掉：默认值由代码决定，写死在文件里的话，
    /// 以后改了代码默认值，用户这份旧文件反而会盖过去。
    /// </summary>
    public const string Template = """
        // ════════════════════════════════════════════════════════════════════
        //  StockPlatform.Fetcher 设置
        //
        //  · 这是 JSONC：支持 // 注释，也允许末尾多余的逗号。
        //  · 怎么配：找到想改的项，**把那一行前面的 // 去掉**（同一个键只留一行），存盘，
        //    重启程序。行本身是完整的，照抄即可，不用自己拼键名。
        //  · 注释掉的项＝用代码里的默认值，行尾标了【默认】的就是那个默认值。
        //  · 程序只读这个文件、不会改写它（唯一例外是老版本设置迁移，那时注释会丢）。
        // ════════════════════════════════════════════════════════════════════
        {
          // ── 板块成分股走哪条取数通道 ────────────────────────────────────
          //  page    ＝ **操作页面**：打开行情中心的板块页，点表头「代码」、点「下一页」，
          //            把页面自己发出去的请求截下来。我们一个 URL 都不拼。【默认】
          //  browser ＝ WebView2 里注入 JSONP，URL 由我们自己拼（pz=100，请求数少 5 倍）。
          //  http    ＝ 纯 HttpClient，最快，但最容易被切。
          //  terminal＝ 读**东财终端客户端**下发到本地的文件，一次读盘拿全量、一个请求都不发。
          //            1031 个板块 94,056 条，是东财自己的口径（跟已抓到的 405 个板块逐只比过，
          //            402 个完全一致，差的 3 个是库里还没收录的新股）。
          //            ⚠ 前提：装了东方财富终端，并且**定期开一次**让它刷新文件。
          //            文件只在客户端启动时下发，人不开就一直是旧的——所以超过
          //            TerminalBoardMaxAgeDays 天没刷新会直接判定通道不可用、本轮不更新。
          //
          //  ⚠ 为什么默认是最慢的 page（2026-09-05 实测）：同一时刻、同一个浏览器、
          //  同一个 IP，**导航到接口 URL 是 503，打开板块页点页码是 200**。服务端认的是
          //  请求的形状（页面 JSONP 带 cb/Referer/Sec-Fetch-Dest:script，导航是 document）。
          //  通不了的话请求再少也没用，所以宁可用 pz=20、多花几倍请求数。
          //
          //  代价：一轮约 4800 个请求、跑 4~8 小时。收市后 16:00 开跑，第二天早上 8 点前收工。
          "BoardMemberChannel": "page",
          //"BoardMemberChannel": "browser",
          //"BoardMemberChannel": "http",
          //"BoardMemberChannel": "terminal",

          // ── 板块成分股打哪个域名 ────────────────────────────────────────
          //  不配（保持注释）＝ pushguest.eastmoney.com【默认】
          //
          //  pushguest 是行情中心板块页**点翻页时真正打的那个接口**（push2 只在首屏被打一次）。
          //  同一个路径、同一套参数，但走另一组前端，不在公司网关的域名拦截名单里。
          //  2026-09-05 逐条比对过：BK1629 三页 282 只跟前一天 push2 抓的完全一致。
          //  ⚠ 还没验证连续几百个请求会不会被限流——真被限了就把下面那行放出来退回 push2。
          //"BoardMemberHost": "push2.eastmoney.com",

          // ── 东财终端本地文件（只有 BoardMemberChannel = terminal 时才用）──────
          //  不配＝ C:\eastmoney\dfcf\data\hs_bk_crc_data_new.dat【默认】
          //  终端装在别处就把下面这行放出来改成实际路径。
          //"TerminalBoardFile": "C:\\eastmoney\\dfcf\\data\\hs_bk_crc_data_new.dat",
          //
          //  文件多久没刷新就算过期（天）。不配＝ 3【默认】
          //  为什么是 3 不是 7：成分股本身"抓过 7 天内不重抓"，这里再放到 7 天的话，
          //  会出现"拿 7 天前的文件更新、并标记成今天抓的"，库里最长陈到 14 天。
          //  只在周末跑一次的话，把它放到 8 以上，否则每次都判定过期。
          //"TerminalBoardMaxAgeDays": "3",

          // ── 分档资金流走哪条通道 ────────────────────────────────────────
          //  both     ＝ 先拉**全市场当日快照**（push2delay，约 60 个请求、一两分钟），
          //             再用剩下的时间逐股补历史（push2his）。【默认】
          //  snapshot ＝ 只要当天的，不补历史。等"还有 N 只历史不全"归零之后切到这个，
          //             这一项就变成每天几十秒的事。
          //  perstock ＝ 只走逐股那条老路。快照接口哪天挂了就用它顶着（慢：全市场 5500+ 个
          //             请求、跑几个小时，而且当天的新数据也得一只只补）。
          //
          //  ⚠ 快照只有**当天**：它是"某天全市场"的入口，历史缺口只有逐股那条补得了
          //  （接口给最近 120 个交易日）。两条的数据 2026-09-06 逐条比对过、零差异。
          //  ⚠ 快照要在**收盘清算之后**跑：盘中拿到的是半天的资金流，程序会认出来并拒绝入库。
          //"MoneyFlowChannel": "both",
          //"MoneyFlowChannel": "snapshot",
          //"MoneyFlowChannel": "perstock",

          // ── 分档资金流快照打哪个域名 ────────────────────────────────────
          //  不配（保持注释）＝ push2delay.eastmoney.com【默认】
          //
          //  为什么是它：排行接口 clist 本来在 push2 上，而 push2 在这台机器上被网关拦着
          //  （连 TCP 都不通）。2026-09-06 挨个试镜像域名，push2delay 通、而且接口完整。
          //  「delay」是延时行情，盘中滞后 15 分钟——收盘后取的是当日终值，对我们没影响。
          //"MoneyFlowSnapshotHost": "push2.eastmoney.com",

          // ── K线数据源 ──────────────────────────────────────────────────
          //"BarSource": "Tencent",   // 腾讯为主，某只票拿不到时自动回退新浪重试这一只【默认】
          //"BarSource": "Sina",      // 纯新浪，无回退
          //"BarSource": "EastMoney", // 东财。⚠ 本机网络下常年连不上，一般别选

          // ── 东财 push2 走哪块网卡出去 ──────────────────────────────────
          //  不配（保持注释）＝ 走系统默认路由。【默认】
          //  配了网卡名       ＝ 只把 push2 的请求钉在那块网卡上，别的数据源不受影响。
          //
          //  什么时候需要：公司有线网的网关曾按域名把 push2 拦掉（TCP 和 TLS 都通、
          //  一发 HTTP 请求就被切断、返回 0 字节），换到另一条链路就正常。
          //  网卡名照着"控制面板 → 网络连接"里显示的填。
          //"Push2NetworkInterface": "Wi-Fi",
          //"Push2NetworkInterface": "以太网",
        }

        """;
}
