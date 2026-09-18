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
    /// 东财终端那份**板块层级树**文件在哪（2026-09-07）；没配返回 null，由
    /// <c>EastMoneyTerminalHierarchyProvider.DefaultPath</c> 兜底。
    ///
    /// 跟 <see cref="ReadTerminalBoardFile"/> 是**两份不同的文件**：那个是"板块里有哪些股票"，
    /// 这个是"板块的父子关系"。而且这一份跟 BoardMemberChannel 无关——不管成分股走哪条通道，
    /// 层级树都只有终端本地这一个来源。
    /// </summary>
    public static string? ReadTerminalHierarchyFile(string settingsPath)
    {
        var v = ReadString(settingsPath, "TerminalHierarchyFile")?.Trim();
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
    /// 逐股补历史那条通道的**请求由谁发**，规范化成 <c>"browser"</c>／<c>"http"</c>，
    /// 没配就是 <c>"browser"</c>（2026-09-14 起的默认）。
    ///
    ///   browser ＝ 真 Chrome/Edge 里做 JSONP（ChromeCdpMoneyFlowFetcher）。
    ///   http    ＝ 程序里的 HttpClient 直连（EastMoneyMoneyFlowProvider）。
    ///
    /// ⚠ 这跟 <see cref="ReadMoneyFlowChannel"/> 是**两个维度**：那个决定"抓不抓逐股这一段"，
    /// 这个决定"这一段的请求由谁发出去"。两条的 URL、fields、解析完全一样
    /// （见 <c>MoneyFlowKlineParser</c>），怎么切都不会写出不一致的数据。
    ///
    /// 默认为什么是 browser：本机公司网关按域名把 push2his 整个拦了——HttpClient 那条
    /// TCP/TLS 都通、一发请求就被切、收到 0 字节，**等多久都不会好**；同一时刻真浏览器
    /// 能稳定取到（2026-09-13 实跑验证过一整份清单）。留着 http 这个值是因为别的机器／
    /// 别的网络出口未必有这道拦截，那边直连更省事（不用起浏览器）。
    /// </summary>
    public static string ReadMoneyFlowBackfillTransport(string settingsPath) =>
        (ReadString(settingsPath, "MoneyFlowBackfillTransport") ?? "browser").Trim().ToLowerInvariant();

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

    /// <summary>
    /// 龙虎榜概要走哪个源，规范化成 <c>"em"</c>／<c>"sina"</c>，没配就是 <c>"em"</c>
    /// （2026-09-09 起的默认）。取值用 <c>LhbSources</c> 里的常量，跟写进
    /// <c>Lhb.source</c> 列的是同一套字符串——配置里写的和库里存的对得上，查起来不用猜。
    ///
    /// 换到东财的理由是**口径**不是数量：它的上榜原因是交易所原文，跟 LhbSeat.explanation
    /// 同源，两张表才能按 (日期,代码,原因) join。留着新浪是因为 datacenter 哪天不通了得有退路
    /// ——但真退回去要知道代价：新浪的原因是归并过的粗类，对应值跟原因错配。
    /// </summary>
    public static string ReadLhbSource(string settingsPath) =>
        (ReadString(settingsPath, "LhbSource") ?? "em").Trim().ToLowerInvariant();

    /// <summary>
    /// 行业分类走哪个源，规范化成 <c>"eastmoney"</c>／<c>"sina"</c>，没配就是 <c>"eastmoney"</c>
    /// （2026-09-10 起的默认）。取值用 <c>IndustrySources</c> 里的常量，跟写进
    /// <c>StockIndustry.source</c> 列的是同一套字符串。
    ///
    /// 换到东财的理由是**覆盖和一致性**：东财一份数据给两级，大类覆盖 6006 只（新浪 3886 只）、
    /// 门类 19 个标准名（两所那套在库里出现过 32 种叫法）；且实测"库里有、东财无"为 0 只。
    /// ⚠ 新浪那条**不是能随手切回去的退路**：2026-09-10 实机比对发现它的大类是整组错位的
    /// （114 只化工股被标成"金属制品、机械和设备修理业"，23 只造纸股被标成"黑色金属冶炼"…
    /// 共 557 只、占 16%），详见 ExchangeSinaIndustryProvider 类注释。留它只是留档。
    /// ⚠ 两个源的大类名分属证监会分类的不同修订版，所以**换源必须整表重写**，不能逐条覆盖。
    /// </summary>
    public static string ReadIndustrySource(string settingsPath) =>
        (ReadString(settingsPath, "IndustrySource") ?? "eastmoney").Trim().ToLowerInvariant();

    /// <summary>
    /// 财务报表走哪个源，规范化成 <c>"sina"</c>／<c>"eastmoney"</c>，没配就是 <c>"eastmoney"</c>
    /// （2026-09-10 起的默认）。
    ///
    /// 换过去的依据是 193 只票 508,681 格的逐格比对，以及一条更根本的判断：
    /// **东财有持牌券商和行情终端，客户拿它的 F10 下单，错了有人投诉**；新浪财经是资讯门户，
    /// 没有交易业务，F10 错十年也没人报。这解释了实测到的形态——东财近强远弱
    /// （2020 年后缺失 0.2%、2005 年前 4~6%），正是"有人用的部分才有投入"。
    /// 而今天查清的每一个个案，错的都是新浪：117,860 格幽灵 0（银行的应付账款/存货写成 0）、
    /// 数字截到万位、银行营业支出漏掉信用减值损失（462 亿 vs 东财 1166 亿）。
    ///
    /// 代价是老年代数据变浅（2005 年前东财缺 4~6%），已接受：那时上市公司才几百家、
    /// 会计准则也完全不同，对回测价值有限。
    ///
    /// 换过去之后：保险公司**仍走新浪**（东财整组不填赔付支出/退保金/保单红利/分保费用），
    /// 由 <c>FinancialSourceRouter</c> 按 ORG_TYPE 分流；银行/券商的净额科目从 B/S 专表取，
    /// 因为同一个 G 表列对银行是毛额、对券商是净额。
    /// </summary>
    public static string ReadFinancialSource(string settingsPath) =>
        (ReadString(settingsPath, "FinancialSource") ?? "eastmoney").Trim().ToLowerInvariant();

    /// <summary>
    /// 【拉取分红送配】用什么判断"谁要抓"（2026-09-18）。规范化成 <c>"eastmoney"</c>／<c>"none"</c>，
    /// 没配就是 <c>"eastmoney"</c>。
    ///
    /// <c>eastmoney</c>＝先问东财最近谁出了分红公告（含预案、进度更新），只抓这些加上到期兜底的；
    /// 一轮从 5902 个请求降到一两百。**值仍然取新浪**，东财只当索引——它当值源不合格
    /// （退市股全空、配股比例只在文本里），而且实测约 1% 漏检，那 1% 靠 90 天全量兜底捞回来。
    ///
    /// <c>none</c>＝不问，纯按水位线到期轮换（2026-09-18 之前的行为）。
    /// ⚠ 这只是**默认开关**：运行期索引拿不到（超时/限流/返回空）时任务本来就会自动退回
    /// 纯水位线并在日志里说一声，不需要人来改配置。
    /// </summary>
    public static string ReadDividendNoticeIndex(string settingsPath) =>
        (ReadString(settingsPath, "DividendNoticeIndex") ?? "eastmoney").Trim().ToLowerInvariant();

    /// <summary>
    /// 读一个整数设置；读不到、不是数字、或不在 [<paramref name="min"/>, <paramref name="max"/>]
    /// 区间内，一律返回 <paramref name="fallback"/>。
    ///
    /// 越界按默认处理而不是报错：这是**人手改的**文件，填个 0 或负数不该让抓取整条挂掉，
    /// 但也不能照着用（并发 0 就是永远等下去）。
    /// </summary>
    public static int ReadInt(string settingsPath, string key, int fallback, int min, int max)
    {
        try
        {
            if (!File.Exists(settingsPath)) return fallback;
            var text = File.ReadAllText(settingsPath);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            using var doc = JsonDocument.Parse(text, Options);
            if (!doc.RootElement.TryGetProperty(key, out var v)) return fallback;
            int? val = v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null,
            };
            return val is { } x && x >= min && x <= max ? x : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// 【拉取分红送配】那条新浪线的节奏（2026-09-18）：**一批发多少个、然后歇多久**。
    ///
    /// ════ 为什么要可配、默认为什么是这两个数 ════
    /// 2026-09-18 凌晨那一轮留下了 18 次熔断的完整记录，规律整齐得出奇：
    /// 新浪大约**每 18 分钟放行 100~150 个请求**，我们 2~3 分钟就把额度打光，
    /// 然后被拒、熔断、干等 15 分钟，再来一轮。有效吞吐 5.6 只/分钟（全市场要 17 小时），
    /// 而且每次撞墙还白发 15 个被拒请求（各带 2 次重试＝45 个）×18 次 ≈ 810 个无效请求。
    ///
    /// 所以默认改成 **90 个 / 歇 13 分钟**——踩在配额线以下匀速走，吞吐持平甚至略高，
    /// 但零熔断、零无效请求。
    ///
    /// ⚠ 这是从**一天**的数据推出来的，18 分钟/100 个会随时段浮动（那天 03:48 那一轮
    /// 放行了 600 只）。所以做成可配的：跑两天看日志里还撞不撞墙，再决定要不要调。
    /// </summary>
    public static (int BatchSize, TimeSpan RestDuration) ReadDividendPace(string settingsPath) =>
        (ReadInt(settingsPath, "DividendBatchSize", 90, 1, 5000),
         TimeSpan.FromMinutes(ReadInt(settingsPath, "DividendRestMinutes", 13, 0, 120)));

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

          // ── 东财终端的板块层级树文件（板块的父子关系）─────────────────────
          //  不配＝ C:\eastmoney\dfcf\data\IndustryBlockRelation.dat【默认】
          //  跟上面 TerminalBoardFile 是两份不同的文件：那个是"板块里有哪些股票"，
          //  这个是"三级行业挂在哪个二级下、二级挂在哪个一级下"。
          //  ⚠ 这一份不看文件新旧：行业分类一个季度都未必动一次，几天前的文件照样是对的；
          //    真解析错了会被"跟 StockIndustryEm 的层级对一遍"那道校验拦下来，比看时间戳可靠。
          //  没装东财终端就是这一项没数据，不影响板块名单本身。
          //"TerminalHierarchyFile": "C:\\eastmoney\\dfcf\\data\\IndustryBlockRelation.dat",

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

          // ── 逐股补历史的请求由谁发 ──────────────────────────────────────
          //  browser ＝ 起一个真 Chrome/Edge，在东财页面里用 JSONP 取。【默认】
          //  http    ＝ 程序里的 HttpClient 直连。
          //
          //  两条打的**是同一个 URL、同一组 fields**，解析也是同一份代码，差别只有
          //  「请求由谁发出去」——所以随便切，写进库的数据不会有差异。
          //
          //  ⚠ 这台机器上只能用 browser：公司网关按域名把 push2his 整个拦了，HttpClient
          //  那条 TCP/TLS 都通、一发请求就被切、收到 0 字节，等多久、换哪块网卡都不会好；
          //  同一时刻真浏览器能稳定取到（2026-09-13 拿 moneyflow-fetch.html 实跑验证过）。
          //  别的机器／别的网络出口未必有这道拦截，那边用 http 更省事（不用起浏览器）。
          //
          //  ⚠ browser 会**弹出一个浏览器窗口**并一直开着（非 headless 是有意的：无头更
          //  容易被认出来）。它用自己的 profile 目录，不碰你日常那个 Chrome。
          //  ⚠ 限流是按**出口 IP** 算的，板块那两项也走浏览器、共用同一个出口——
          //  别让它们跟这一项同时跑。
          //"MoneyFlowBackfillTransport": "browser",
          //"MoneyFlowBackfillTransport": "http",

          // ── 分档资金流快照打哪个域名 ────────────────────────────────────
          //  不配（保持注释）＝ push2delay.eastmoney.com【默认】
          //
          //  为什么是它：排行接口 clist 本来在 push2 上，而 push2 在这台机器上被网关拦着
          //  （连 TCP 都不通）。2026-09-06 挨个试镜像域名，push2delay 通、而且接口完整。
          //  「delay」是延时行情，盘中滞后 15 分钟——收盘后取的是当日终值，对我们没影响。
          //"MoneyFlowSnapshotHost": "push2.eastmoney.com",

          // ── 龙虎榜概要走哪个源 ──────────────────────────────────────────
          //  em   ＝ 东财 datacenter（RPT_DAILYBILLBOARD_DETAILSNEW）。【默认】
          //  sina ＝ 新浪龙虎榜每日页。出事时的退路。
          //
          //  2026-09-09 换到东财。两源逐条比对过（09-08 单日）：票集 55 vs 55 双向零差异、
          //  收盘价全对上、成交额换算比值精确 1.000000、数据起点同为 2004-06-25。
          //  换的理由是**口径**：东财的上榜原因是交易所原文，跟【拉取龙虎榜席位】那张表
          //  同源，两张表终于能按 (日期,代码,原因) join——"为什么上榜"和"谁在买"以前对不起来。
          //
          //  ⚠ 退回 sina 的代价：它把上榜原因归并成粗类（28 种 vs 交易所原文的几十种），
          //  而"对应值"仍跟着各自的原规则走，同一个 reason 下混着当日涨跌幅/两日累计/多日
          //  累计（2026-08-04 创业板那批就是）。真退回去，新写进来的行会跟已有的东财行
          //  在同一天里并存两套原因文本。
          //"LhbSource": "em",
          //"LhbSource": "sina",

          // ── 离线模拟总开关（仅 DEBUG 构建认这一项）──────────────────────
          //  true ＝ 把**所有已有模拟源**的数据源一次全换成"一个请求都不发"的版本
          //         （现有：资金净流入 / 龙虎榜 / 龙虎榜席位 / 大宗交易 / 融资余额 / 股东数据 /
          //          指数成分 / 指数权重 / 流通市值。完整清单和"还有哪些没有"见
          //          doc/offline-mock-design.md，那份文档才是权威）。
          //         K线另走上面的 "BarSource": "Mock"。
          //  用来在 Debug 实例里把整条链真跑一遍（四道闸、无条件重抓、空日名单、待办转交与复查、
          //  【拉取区间数据】那一整轮），既不发请求，也不跟正在抓数据的正式实例抢配额。
          //  做成一个总开关而不是每类一个，是因为逐类配必然漏——2026-09-18 就漏过、当场发了真请求。
          //
          //  ⚠ 打开它 ≠ 整个程序离线：还有一批源没有模拟版（席位、大宗、板块、公告、财务…），
          //    那些照样联网。**哪些有、哪些没有、怎么加新的，见 doc/offline-mock-design.md。**
          //  ⚠ 它写进库的是**假数据**。Release 构建根本不认这个值，
          //    Debug 实例的数据目录也跟正式实例天然隔离——两道闸都别拆。
          //"OfflineMock": "true",

          // ── 行业分类走哪个源 ────────────────────────────────────────────
          //  eastmoney ＝ 东财 F10（RPT_F10_ORG_BASICINFO 的 CSRC_INDUSTRY_NAME，一个字段两级）。【默认】
          //  sina      ＝ 两所门类 + 新浪 84 个行业节点逐个取成分股。
          //              ⚠ 已证**大类整组错位**（557 只、16%：化工股被标成金属修理业、造纸股被标成钢铁…），
          //                只作留档，别切回来——真要用得先修错位。
          //
          //  2026-09-10 换到东财。全量比对（doc/industry-source-eastmoney-design.md）：
          //  有大类的票 3886 → 6006 只，门类叫法 32 种 → 19 个标准名（原来沪深两所各说各话），
          //  "库里有、东财无" 0 只——是超集，换过去不掉数据。
          //  ⚠ 两个源的大类名分属证监会分类的不同修订版（"开采辅助活动" vs "开采专业及辅助性活动"），
          //    所以这张表是**整表重写**：切换或切回都会把 StockIndustry 全表换成新的那一版，
          //    不会出现新旧名并存把同一个行业裂成两个中性化分组的情况。
          //"IndustrySource": "eastmoney",
          //"IndustrySource": "sina",
          // ── 财务报表走哪个源 ────────────────────────────────────────────
          //  sina      ＝ 新浪 vDOWN_ 报表下载（中文行名匹配）。【默认】
          //  eastmoney ＝ 东财 F10（RPT_F10_FINANCE_*，固定英文列名，按 ORG_TYPE 选 G/B/S 表）。
          //
          //  2026-09-10 切成东财。依据：193 只票 508,681 格逐格比对（2020 年后缺失仅 0.2%），
          //  加上"东财有持牌券商和终端客户、错了有人投诉，新浪是资讯门户没人较真"这条判断。
          //  查清的个案错的都是新浪：11.8 万格幽灵 0、数字截到万位、银行营业支出漏掉信用减值。
          //  ⚠ 无论切到哪边，保险那 5 家（平安/太保/国寿/新华/人保）都走新浪——东财**整组不填**
          //    保险的支出科目（赔付支出/退保金/保单红利/分保费用三处全 null），而赔付率指标要用它。
          //  ⚠ 切过去之后要把 FinancialKeys.Version +1 才会全量重抓，否则老数据不会被替换。
          //"FinancialSource": "sina",
          //"FinancialSource": "eastmoney",

          // ── 分红送配：怎么决定"这轮抓哪些票" ─────────────────────────
          //  新浪的分红页没有时间参数（一次返回整页全历史），所以省不了"抓哪一段"，
          //  只能省"抓哪些票"。先问一句东财最近谁出了公告，一轮 5902 个请求就降到一两百。
          //  ⚠ 值仍然取新浪，东财只当索引：它当值源不合格（退市股全空、配股比例只在文本里），
          //    而且实测约 1% 漏检（它自己缺记录）——那 1% 靠 90 天全量兜底捞回来。
          //"DividendNoticeIndex": "eastmoney",  // 先问东财最近 45 天谁出了分红公告【默认】
          //"DividendNoticeIndex": "none",       // 不问，纯按水位线到期轮换（2026-09-18 之前的行为）

          // ── 分红送配：新浪那条线的节奏（发多少个、歇多久）─────────────
          //  2026-09-18 凌晨那轮 18 次熔断的记录显示：新浪大约**每 18 分钟放行 100~150 个请求**。
          //  我们 2~3 分钟打光额度 → 被拒 → 熔断干等 15 分钟 → 再来一轮，有效吞吐 5.6 只/分钟，
          //  还白发了约 810 个被拒请求。默认值就是踩在配额线以下匀速走，不去撞墙。
          //  ⚠ 那个 18 分钟/100 个会随时段浮动（那天 03:48 有一轮放行了 600 只），
          //    所以跑两天看日志里还撞不撞墙再调。撞了就把 BatchSize 调小或 RestMinutes 调大。
          //"DividendBatchSize": 90,      // 一批发多少个请求（默认 90）
          //"DividendRestMinutes": 13,    // 一批发完歇多少分钟（默认 13；填 0 就是不歇）

          // ── K线数据源 ──────────────────────────────────────────────────
          //"BarSource": "Tencent",   // 纯腾讯。失败自动重试 3 次（间隔 2/10 秒），仍失败进【重新拉取失败】名单【默认】
          //                          （2026-09-10 拆掉了"回退新浪"：新浪的成交量是股、腾讯主板是手，
          //                            每回退一次就往那只票历史里掺一段异口径的行，而回退实测只触发过 1 次）
          //"BarSource": "Sina",      // 纯新浪。⚠ 它只有前复权一种口径，不复权/后复权拿不到
          //"BarSource": "EastMoney", // 东财 push2his。2026-09-10 沙箱实测**可达**（600000/688001/
          //                          //  300750/920371 都正常返回），此前"常年连不上"的说法是
          //                          //  push2 被封那阵的判断，对 push2his 不成立。
          //                          //  ⚠ 但现有实现 fqt=1 写死，只支持前复权；换成主源之前还要
          //                          //  验三种复权口径、压测限流、确认退市股能不能拉。
          //
          //  三个源的成交量口径不同（腾讯主板给手/科创板给股、新浪一律给股、东财都给手），
          //  进库前统一换算成"手"，见 Logic/Services/BarVolumeUnit.cs。

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
