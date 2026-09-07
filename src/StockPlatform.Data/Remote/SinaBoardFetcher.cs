using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Remote;

/// <summary>
/// ⚠ <b>2026-09-03 起已不再接线</b>，板块整体改用 <see cref="EastMoneyBoardFetcher"/>。
/// 原因是这套分类严重老化：175 个概念板块里没有存储芯片/算力/液冷/AI芯片/CPO/先进封装/
/// 人形机器人，占着位置的却是"融资融券""社保重仓""成渝特区""业绩预升"这类根本不是产业链的东西；
/// 而且 <c>BoardMember</c> 接口每页上限 100 条（<c>num=1000</c> 会被服务端忽略），大板块只能翻页拿。
/// 东财实测 1031 个板块、5656 只股票、93867 条归属关系，主题一个不缺，请求数反而更少。
///
/// 类留着不删是因为它是一份完整可用的实现，万一东财整体不可用还能手工接回去（在
/// App.xaml.cs 里把 boardFetcher 换回来即可）；**但不要指望它当自动回退**——板块是快照，
/// 抓取失败时库里保留上次的东财数据，比用这 224 个老板块覆盖上去有价值得多。
///
/// 以下是原实现说明：
/// 抓取新浪财经的板块行情——概念/题材板块（newFLJK.php?param=class）和行业板块（newSinaHy.php），
/// 外加板块成分股（Market_Center.getHQNodeData?node=...）。选新浪是因为：① 东方财富在该用户环境
/// 基本连不上；② 腾讯公开接口不暴露板块分类；③ 新浪本来就已经在用（资金流向、股票列表）。回包
/// 是 GBK，跟 <see cref="SinaNetInflowFetcher"/> 一样注册 CodePages 后手工按 GBK 解码。
///
/// 板块列表回包形如 `var S_Finance_bankuai_class = {"gn_hwqc":"gn_hwqc,华为汽车,97,均价,涨跌,
/// 涨跌幅,量,成交额,sz300680,领涨涨幅,领涨价,领涨涨额,隆盛科技", ...};` —— JSON 对象里每个值是
/// 逗号分隔的一行字段。
/// </summary>
public class SinaBoardFetcher : IBoardFetcher
{
    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;

    public event Action<string>? OnStatus
    {
        add => _rateLimiter.OnStatus += value;
        remove => _rateLimiter.OnStatus -= value;
    }

    static SinaBoardFetcher()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SinaBoardFetcher(RateLimiter rateLimiter, HttpClient? httpClient = null)
    {
        _rateLimiter = rateLimiter;
        _http = httpClient ?? new HttpClient(CreateHandler());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("http://finance.sina.com.cn");
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    private static readonly Dictionary<BoardType, string> ListUrls = new()
    {
        [BoardType.Concept] = "http://money.finance.sina.com.cn/q/view/newFLJK.php?param=class",
        [BoardType.Industry] = "http://vip.stock.finance.sina.com.cn/q/view/newSinaHy.php",
        // 地域板块新浪没有对应接口。这个类本来就是"东财全挂了才手工换回来"的备用，
        // 缺一类也比编一份假名单强——下面显式抛，不让它悄悄变成空列表。
    };

    public Task<List<Board>> FetchBoardListAsync(BoardType type, CancellationToken ct = default) =>
        _rateLimiter.RunAsync(() => FetchBoardListInternalAsync(type, ct), ct);

    private async Task<List<Board>> FetchBoardListInternalAsync(BoardType type, CancellationToken ct)
    {
        if (!ListUrls.TryGetValue(type, out var listUrl))
            throw new NotSupportedException(
                $"新浪没有{type.Label()}板块的接口——这个源只有概念和行业两类。");
        var body = await GetGbkAsync(listUrl, ct);
        var now = DateTime.Now;

        int open = body.IndexOf('{');
        int close = body.LastIndexOf('}');
        if (open < 0 || close <= open) return new List<Board>();
        var json = body.Substring(open, close - open + 1);

        var result = new List<Board>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var raw = prop.Value.GetString();
            if (string.IsNullOrEmpty(raw)) continue;
            var f = raw.Split(',');
            if (f.Length < 13) continue;

            double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var changePct);
            double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount);
            int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count);

            result.Add(new Board
            {
                BoardCode = prop.Name,
                Type = type,
                Name = f[1],
                MemberCount = count,
                ChangePct = changePct,
                Amount = amount,
                LeaderCode = StripPrefix(f[8]),
                LeaderName = f[12],
                AsOf = now,
            });
        }
        return result;
    }

    /// <summary>跟东财那几条走网络的通道一样按 7 天算（见 <see cref="IBoardFetcher.MemberFreshFor"/>）。</summary>
    public TimeSpan MemberFreshFor => TimeSpan.FromDays(7);

    public Task<List<string>> FetchMembersAsync(string boardCode, CancellationToken ct = default) =>
        _rateLimiter.RunAsync(() => FetchMembersInternalAsync(boardCode, ct), ct);

    private async Task<List<string>> FetchMembersInternalAsync(string boardCode, CancellationToken ct)
    {
        var url = "http://vip.stock.finance.sina.com.cn/quotes_service/api/json_v2.php/Market_Center.getHQNodeData" +
                  $"?page=1&num=1000&sort=&asc=0&node={boardCode}&symbol=&_s_r_a=page";
        var body = await GetGbkAsync(url, ct);
        var codes = new List<string>();
        if (string.IsNullOrWhiteSpace(body) || body == "null") return codes;

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return codes;
        foreach (var item in doc.RootElement.EnumerateArray())
            if (item.TryGetProperty("code", out var codeEl) && codeEl.GetString() is { Length: > 0 } c)
                codes.Add(c);
        return codes;
    }

    private async Task<string> GetGbkAsync(string url, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(url, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            throw new RateLimitedException(
                $"无法连接新浪财经板块接口：{detail}（可能是代理/防火墙问题，也可能是触发了反爬限流）", ex);
        }
        return bytes.Length == 0 ? "" : Encoding.GetEncoding("GBK").GetString(bytes);
    }

    /// <summary>去掉新浪/腾讯的两位交易所前缀（sh/sz/bj）得到纯 6 位代码。</summary>
    private static string StripPrefix(string symbol) =>
        symbol.Length > 2 && (symbol.StartsWith("sh") || symbol.StartsWith("sz") || symbol.StartsWith("bj"))
            ? symbol.Substring(2) : symbol;
}
