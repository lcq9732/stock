using System.Net;
using System.Net.Http;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 板块名单走东财行情中心左侧菜单的数据源（2026-09-05）——**一个请求拿全量，完全不碰 push2**。
///
/// ════ 这是什么 ════
/// <c>quote.eastmoney.com/center/api/sidemenu_new.json</c> 是行情中心页面加载时一次性下载的
/// 静态 JSON（约 110KB），左侧那棵菜单树就是拿它渲染的——所以在页面上展开"沪深京板块"时
/// 一个网络请求都不会发。顶层 <c>bklist</c> 是平铺数组：
/// <code>{"market":90,"code":"BK0169","name":"四川板块","pinyin":"SCBK","type":1,"flag":0}</code>
///   · <c>type</c>：1=地域(31) 2=行业(497) 3=概念(504)，正对上 push2 的 <c>fs=m:90+t:{2|3}</c>
///   · <c>flag</c>：对行业是层级（1=一级 2=二级 3=三级），概念/地域恒 0；这里用不上
///
/// ════ 为什么换过来 ════
/// 原来这一步走 push2 <c>clist</c>，约 10 个请求、5 页翻页，而 push2 在用户环境**会弹图片
/// 验证码、要人守在电脑前过**（见 <see cref="EastMoneyBoardFetcher"/>）。换成这个之后：
///   · 普通 HttpClient 直接可达——quote 域名不在那个网关的拦截名单里（push2 才是），
///     不需要 WebView2 浏览器通道，也就不会弹验证；
///   · 不消耗 push2 配额，把整轮配额都留给真正需要它的【板块成分股】（约 2500 个请求）。
///
/// ════ 数据等价性（2026-09-05 逐条比对，不是推测）════
/// 拿它跟库里当天 08:01 用 push2 抓的官方名单逐条比：
///   · 概念 504 vs 504：**代码和名称一个不差**；
///   · 行业 496 vs 497：菜单多一个 <c>BK1362 其他多元金融</c>（三级行业）。
/// 差异方向是"菜单多"而非"菜单少"，对 <c>CommitStaged</c> 的删除语义是安全侧。
/// 热门主题（存储芯片/算力/液冷服务器/CPO/先进封装/人形机器人/固态电池）一个不缺。
///
/// ════ 拿不到什么 ════
/// **没有行情**——涨跌幅、成交额、领涨股都不在这份 JSON 里。但这一步本来就不取行情：
/// 那两个值由【板块指数合成】用本地成分股日K算出来回填（见 <c>IBoardRepository.UpdateQuotes</c>），
/// 这样口径还跟板块K线天然一致。所以只缺领涨股，而领涨股没有任何地方在用。
///
/// ════ 护栏 ════
/// push2 那条路靠"接口自报 total 跟实抓条数对不上就整轮弃写"防半截列表。这份 JSON 没有 total，
/// 换成 <see cref="CheckAgainstExisting"/>：跟库里上次的数量比，掉得太多就整轮放弃。
/// 这道检查**必须有**——正表是快照语义，"这轮没返回的板块＝已下架"会连 BoardMember 和
/// BoardMemberFetchState 一起删掉，而成分股跨好几轮才攒得齐、重抓要好几天，且全程不报错。
/// </summary>
public sealed class EastMoneySideMenuBoardListProvider
{
    public const string Url = "https://quote.eastmoney.com/center/api/sidemenu_new.json";

    /// <summary>
    /// 解析出来少于这个数就当"文件结构变了"而不是"板块真的变少了"。
    /// 实测概念 504 + 行业 497 = 1001，门槛 300 留了三倍余量。
    /// </summary>
    private const int MinPlausibleCount = 300;

    private readonly HttpClient _http;

    public event Action<string>? OnStatus;

    public EastMoneySideMenuBoardListProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/center/gridlist.html");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    /// <summary>跟着系统代理走——公司网络下不带这个连不出去（跟 SinaBoardFetcher 一样的处理）。</summary>
    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    /// <summary>
    /// 拉全量板块名单（概念 + 行业，**不含地域**）。一个请求，通常 1 秒内。
    /// 地域板块（type=1，31 个）不要：库里从来没存过它，抓回来只会让 CommitStaged
    /// 把它们当新板块写进正表，接着【板块成分股】又要多花 31 个 push2 请求去抓。
    /// </summary>
    public async Task<List<Board>> FetchBoardListAsync(CancellationToken ct = default)
    {
        string body;
        try
        {
            body = await _http.GetStringAsync(Url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"取东财板块菜单失败：{ex.InnerException?.Message ?? ex.Message}（{Url}）", ex);
        }

        var boards = Parse(body, DateTime.Now);
        OnStatus?.Invoke(
            $"板块名单取自东财行情中心菜单（不走 push2）：概念 {boards.Count(b => b.Type == BoardType.Concept)} 个、"
            + $"行业 {boards.Count(b => b.Type == BoardType.Industry)} 个。");
        return boards;
    }

    /// <summary>
    /// 解析 <c>bklist</c>。单独抽出来是为了能拿固定样例测——网络那半截测不了，解析这半截必须测。
    /// </summary>
    /// <exception cref="InvalidDataException">JSON 结构不是预期的样子，或解析出来的板块少得不合理。</exception>
    public static List<Board> Parse(string json, DateTime asOf)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"东财板块菜单不是合法 JSON：{ex.Message}", ex);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("bklist", out var list)
                || list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(
                    "东财板块菜单里没有 bklist 数组——文件结构变了，本轮不更新板块。");

            var result = new List<Board>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("code", out var codeEl)) continue;
                var code = codeEl.GetString();
                if (string.IsNullOrWhiteSpace(code)) continue;

                // type 1=地域（不要）2=行业 3=概念。认不出来的新类型一律跳过：
                // 宁可漏一类，也不要把不知道是什么的东西当板块写进正表。
                if (!item.TryGetProperty("type", out var typeEl)
                    || !typeEl.TryGetInt32(out var t)) continue;
                var boardType = t switch
                {
                    3 => (BoardType?)BoardType.Concept,
                    2 => BoardType.Industry,
                    _ => null,
                };
                if (boardType is not { } bt) continue;

                if (!seen.Add(code)) continue;   // 实测无重复，但重复也不能进库

                result.Add(new Board
                {
                    BoardCode = code,
                    Type = bt,
                    Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    MemberCount = 0,     // 由【板块成分股】按实际名单写
                    ChangePct = 0,       // 菜单没有行情，由【板块指数合成】回填
                    Amount = 0,
                    LeaderCode = "",
                    LeaderName = "",
                    AsOf = asOf,
                });
            }

            if (result.Count < MinPlausibleCount)
                throw new InvalidDataException(
                    $"东财板块菜单只解析出 {result.Count} 个板块（正常约 1000 个）——"
                    + "多半是文件结构变了而不是板块真的变少了，本轮不更新板块。");

            return result;
        }
    }

    /// <summary>
    /// 提交前的护栏：这一类抓到 <paramref name="fetched"/> 个，库里现有 <paramref name="existing"/> 个，
    /// 该不该写进正表。不该写时返回原因，该写时返回 null。
    ///
    /// 为什么按"比例掉得太多"判而不是"必须相等"：板块名单本来就会增减（新概念上线、旧板块下架），
    /// 要求相等等于永远提交不了。而**掉得多**才是危险信号——正表是快照语义，少掉的那些会被当成
    /// 已下架，连成分股一起删。5% ≈ 50 个板块，正常一天的增减远小于这个数。
    ///
    /// 库里是空的（首次抓取）时不设限：那时没有"上一次"可比，也没有成分股可删。
    /// </summary>
    public static string? CheckAgainstExisting(BoardType type, int fetched, int existing)
    {
        const double MaxShrinkRatio = 0.05;
        if (existing <= 0) return null;

        var shrink = (existing - fetched) / (double)existing;
        if (shrink <= MaxShrinkRatio) return null;

        var label = type.Label();
        return $"{label}板块名单比库里少了 {shrink:P1}（菜单 {fetched} 个 / 库里 {existing} 个）——"
             + "掉这么多不像是正常增减，本轮不更新，库里保留上次的完整快照。";
    }
}
