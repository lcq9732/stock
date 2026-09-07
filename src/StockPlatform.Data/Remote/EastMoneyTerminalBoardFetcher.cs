using System.Net.Http;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 板块成分股走**东财终端客户端落在本地的文件**——第四条通道（2026-09-06 新增）。
///
/// 前三条（http / browser / page）的差别只在"怎么把请求发出去"，本质都是逐个板块打 push2。
/// 这条根本不发请求：东财 PC 客户端启动时会把全市场板块成分股下发到本地
/// （<see cref="EastMoneyTerminalBoardFile"/>），读一次盘就是全量。
///
/// ════ 收益 ════
/// 前三条通道跑一轮三小时起、还随时被限流打断，实测抓了几天攒到 405 个板块 21,041 条；
/// 这条一次读盘 1031 个板块 94,056 条，耗时以毫秒计，且完全不受出口 IP 风控影响。
/// 口径也不是第三方近似——跟库里已抓到的 405 个板块逐只比对，402 个完全一致，
/// 差异的 3 个是新股上市、库里还没收录。
///
/// ════ 代价 ════
/// 换来一个**运行时依赖**：得有人定期开一次东方财富终端，文件才会刷新。
/// 这个依赖是硬的，而且失效时没有任何征兆（文件还在、格式还对、只是停在几天前），
/// 所以 <see cref="EastMoneyTerminalBoardFile.Load"/> 强制查文件时间，过期就整条通道不可用。
///
/// ════ 板块列表仍走 HTTP ════
/// 只有成分股换了。板块列表要的是涨跌幅、成交额、领涨股这些**行情**字段
/// （见 <see cref="Logic.Models.Board"/>），本地文件里只有名单没有行情，给不了。
/// 这跟 <see cref="EastMoneyBoardPageFetcher"/> 的分工是一样的：基类的
/// <see cref="EastMoneyBoardFetcherBase.FetchBoardListAsync"/> 照常走 <see cref="GetAsync"/>。
/// </summary>
public class EastMoneyTerminalBoardFetcher : EastMoneyBoardFetcherBase
{
    private readonly EastMoneyTerminalBoardFile _file;
    private readonly HttpClient _http;
    private bool _ready;

    public EastMoneyTerminalBoardFetcher(RateLimiter limiter, EastMoneyTerminalBoardFile file,
                                         HttpClient? httpClient = null,
                                         string? bindNetworkInterface = null)
        // 板块之间不歇：那一档是为了把请求节奏做得像真人翻页，而这条通道压根不发请求。
        : base(limiter, memberHost: null, boardSwitchPause: TimeSpan.Zero)
    {
        _file = file;
        _http = httpClient ?? new HttpClient(
            NetworkInterfaceBinder.CreateHandler(bindNetworkInterface, Report));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
        _http.Timeout = TimeSpan.FromSeconds(25);
    }

    public override string DescribeChannel() => _ready
        ? $"成分股：东财终端本地文件（{_file.BoardCount} 个板块，文件时间 {_file.FileTime:MM-dd HH:mm}）"
        : "成分股：东财终端本地文件不可用，本轮抓不了";

    /// <summary>
    /// **完全不节流，每轮全量覆盖**（2026-09-06 按用户要求改）。
    ///
    /// 另外三条通道要 7 天的节流，是因为重抓一遍要跑三小时、还随时被限流；这条读一次本地
    /// 文件就是全量，实测 948 个板块 1 秒钟跑完。既然重来一遍几乎不要钱，就没有任何理由
    /// 让库里留着旧值——尤其是**别的通道留下的半截数据**：切过来的第一轮就出现过这情况，
    /// 早上用 page 通道抓的 53 个板块因为"当天已抓过"被跳过，于是那几个板块一直缺着
    /// 当天新上市的股票。
    ///
    /// 语义见 <see cref="IBoardFetcher.MemberFreshFor"/>：小于等于 0 表示不节流，
    /// 调用方会把"新鲜"的时间线推到 <see cref="DateTime.MaxValue"/>，于是没有任何记录
    /// 算得上新鲜，全部重新覆盖一遍。
    /// </summary>
    public override TimeSpan MemberFreshFor => TimeSpan.Zero;

    /// <summary>
    /// 从本地文件拼一份**板块名单**，给【概念和行业板块】那一步用（2026-09-06）。
    ///
    /// 取不到就返回 false，调用方**继续走菜单 JSON**——而不是抛出去。这跟成分股那边
    /// （<see cref="FetchMembersAsync"/> 取不到就抛）是有意做成不一样的：
    /// 名单还有 sidemenu 和 push2 两条路可以更新，没道理因为本地文件不可用就让整项停摆；
    /// 而成分股拿不到就只能保留库里旧值，没有别的源可退。
    /// </summary>
    public bool TryGetBoardList(out List<Board> list, out string message)
    {
        list = new List<Board>();
        message = "";

        // 名单这一步排在成分股前面，此时 PrepareAsync 可能还没跑过，自己确保加载
        if (!_file.IsLoaded)
        {
            if (!_file.Load(out message)) { _ready = false; return false; }
        }
        else if (!_file.ReloadIfChanged(out message)) { _ready = false; return false; }

        _ready = true;
        list = _file.BuildBoardList();
        if (list.Count == 0)
        {
            message = "东财终端本地文件里没有行业/概念板块（只有地域？格式可能变了）。";
            return false;
        }

        // 按类型逐个数，不要写成"概念 = 总数 - 行业"——加了地域之后那样算会把地域并进概念
        var byType = string.Join("、", list.GroupBy(b => b.Type)
                                           .OrderBy(g => g.Key)
                                           .Select(g => $"{g.Key.Label()} {g.Count()}"));
        message = $"板块名单取自东财终端本地文件：{list.Count} 个"
                + $"（{byType}，文件时间 {_file.FileTime:yyyy-MM-dd HH:mm}）。";
        return true;
    }

    /// <summary>
    /// 加载并校验本地文件。返回值上层不看（见 FetchOrchestrator 里的调用），
    /// 所以**关键信息一定要发到日志**——文件过期时人得从日志里看到"去开一次客户端"，
    /// 而不是只看到一串"板块 xxx 抓取失败"。
    /// </summary>
    public override Task<bool> PrepareAsync(CancellationToken ct = default)
    {
        _ready = _file.Load(out var message);
        Report(_ready ? message : "⚠ " + message);
        return Task.FromResult(_ready);
    }

    /// <summary>
    /// 从本地文件取成分股。三种情况都**抛异常**而不是返回空列表——
    /// 上游 <c>ReplaceMembers</c> 是覆盖语义，拿空列表去更新等于把这个板块的成分股全删掉，
    /// 而且全程不报错。抛出去的话，上层会把这个板块记成失败、保留库里原有的名单、下轮重试
    /// （见 FetchBoardMembersCoreAsync 的 catch），这才是"取不到"该有的行为。
    /// </summary>
    public override Task<List<string>> FetchMembersAsync(string boardCode,
                                                         CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 客户端可能在抓取过程中重启并刷新了文件，顺手跟一下（没变就什么也不做）
        if (!_file.ReloadIfChanged(out var reloadMsg) )
        {
            _ready = false;
            if (reloadMsg.Length > 0) Report("⚠ " + reloadMsg);
        }
        else if (reloadMsg.Length > 0) Report(reloadMsg);

        if (!_file.IsLoaded || !_ready)
            throw new InvalidOperationException(
                "东财终端本地文件不可用（详见前面的日志），成分股本轮取不了。"
                + "多半是太久没开客户端——开一次东方财富终端、等两分钟让它下发完再跑。");

        var members = _file.TryGetMembers(boardCode);
        if (members == null)
            throw new InvalidOperationException(
                $"板块 {boardCode} 不在东财终端的本地文件里。"
                + "如果是刚上架的新板块，等客户端下次下发就有了；本轮保留库里原有名单。");

        if (members.Count == 0)
            throw new InvalidOperationException(
                $"板块 {boardCode} 在终端文件里成分股为 0 只——不拿它覆盖库里已有的名单。");

        return Task.FromResult(new List<string>(members));
    }

    /// <summary>只服务于板块列表（成分股不走网络）。实现跟其他通道一致。</summary>
    protected override async Task<string> GetAsync(string url, CancellationToken ct)
    {
        try
        {
            var body = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(body))
                throw new RateLimitedException("东财返回空响应（典型的限流表现）。");
            return body;
        }
        catch (RateLimitedException) { throw; }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            throw new RateLimitedException(
                $"东财连接被断开：{ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }
}
