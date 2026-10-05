using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【停复牌】（2026-09-30，见 doc/suspension-design.md）：官网接口解析、全天停牌判据、本地存取、任务。
///
/// 判据那几条的例子全部取自 2026-09-30 拿官网记录跟本地 day_raw 逐日比对时的真实记录
/// （上交所 2010~2025 抽样 8.7 万天、深交所 2007~2026 全量 28 万天）。
/// 解析用的是沙箱里抓回来的真实响应（上交所 JSON 只留了解析用到的 pageHelp；深交所是原样 xlsx）。
/// 存取和任务全部打在临时 SQLite 上（假仓储验不到真 SQL）。
/// </summary>
public class SuspensionTests : IDisposable
{
    // ─────────────────── 真实响应样本 ───────────────────

    /// <summary>上交所·股票 2026-09-29（pageSize=3 截的前三条：连续停牌、临时停牌全天、可转债）。</summary>
    private const string SseStockJson = """
        {"pageHelp":{"data":[
        {"startStopDate":"20260929","stopReason":"拟筹划重大资产重组","productCode":"600293","endStopReason":"","controlType":"TR","endStopDate":"","stopTime":"","type":"LXTP","productName":"三峡新材"},
        {"startStopDate":"20260929","stopReason":"重要公告","productCode":"600363","endStopReason":"重要公告","controlType":"TR","endStopDate":"20260929","stopTime":"WH","type":"LSTP","productName":"联创光电"},
        {"startStopDate":"20260928","stopReason":"重要公告","productCode":"110815","endStopReason":"重要公告","controlType":"CB","endStopDate":"20261009","stopTime":"","type":"LXTP","productName":"九丰定01"}
        ],"pageCount":5,"pageNo":1,"pageSize":3,"total":13}}
        """;

    /// <summary>上交所·基金 2026-09-29（盘中停 915 的两只 + 拟退市连续停牌的一只）。</summary>
    private const string SseFundJson = """
        {"pageHelp":{"data":[
        {"endStopType":"LSTP","startStopReason":"根据基金管理人申请","endStopDate":"20260929","endType":"","secCode":"501225","expandAbbr":"全球芯片LOF","startType":"null","startStopDate":"20260929","endStopReason":"根据基金管理人申请","startStopType":"LSTP","dateSource":"2","secAbbr":"全球芯片","stopTime":"915"},
        {"endStopType":"LSTP","startStopReason":"根据基金管理人申请","endStopDate":"20260929","endType":"","secCode":"513100","expandAbbr":"纳指ETF国泰","startType":"null","startStopDate":"20260929","endStopReason":"根据基金管理人申请","startStopType":"LSTP","dateSource":"2","secAbbr":"纳指ETF","stopTime":"915"},
        {"endStopType":"","startStopReason":"拟退市摘牌","endStopDate":"","endType":"","secCode":"512390","expandAbbr":"中国低波ETF平安","startType":"null","startStopDate":"20260907","endStopReason":"","startStopType":"LXTP","dateSource":"2","secAbbr":"MSCI低波","stopTime":""}
        ],"pageCount":2,"pageNo":1,"pageSize":3,"total":4}}
        """;

    /// <summary>深交所「停复牌提示」2026-09-29 一天的原样 xlsx 导出（6 行，含 300527 那条「1天」）。</summary>
    private const string SzseXlsx0929 = "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";

    /// <summary>深交所 2004-01 的导出：那个月没有记录，只有表头。</summary>
    private const string SzseXlsxEmpty = "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";

    // ─────────────────── ① 解析 ───────────────────

    private static List<SuspensionRow> ParseSse(string json, Func<System.Text.Json.JsonElement, SuspensionRow?> map)
        => SseSuspensionProviderBase.ParsePage(json, "测试").Items.Select(map).OfType<SuspensionRow>().ToList();

    /// <summary>取到的全存（2026-09-30 用户定）：可转债也留，品种记在 ControlType；9 列都有字段。</summary>
    [Fact]
    public void 上交所股票_可转债也存_每一列都有字段()
    {
        var rows = ParseSse(SseStockJson, SseStockSuspensionProvider.MapStock);

        Assert.Equal(3, rows.Count);
        Assert.Equal("CB", rows.Single(r => r.Code == "110815").ControlType);
        var a = rows.Single(r => r.Code == "600293");
        Assert.Equal(("TR", "三峡新材", "拟筹划重大资产重组"), (a.ControlType, a.Name, a.Reason));
        Assert.Equal(("sh", "LXTP", new DateOnly(2026, 9, 29)), (a.Market, a.Kind, a.StartDay!.Value));
        Assert.Null(a.EndDay);                               // 还没复牌
        var b = rows.Single(r => r.Code == "600363");
        Assert.Equal(("LSTP", "WH"), (b.Kind, b.StopTime));
        Assert.Equal(new DateOnly(2026, 9, 29), b.EndDay);
        Assert.Equal("重要公告", b.EndReason);
    }

    /// <summary>基金查询 13 列都有字段（LOF 也存）。</summary>
    [Fact]
    public void 上交所基金_字段名不同也认得_13列都存()
    {
        var rows = ParseSse(SseFundJson, SseFundSuspensionProvider.MapFund);

        Assert.Equal(3, rows.Count);
        Assert.Equal(("LSTP", "915"), (rows[1].Kind, rows[1].StopTime));
        Assert.Equal(("512390", "LXTP"), (rows[2].Code, rows[2].Kind));
        var r = rows[1];
        Assert.Equal(("纳指ETF", "纳指ETF国泰", "根据基金管理人申请", "根据基金管理人申请"),
                     (r.Name, r.FullName, r.Reason, r.EndReason));
        Assert.Equal(("LSTP", "null", "", "2"), (r.EndKind, r.StartType, r.EndType, r.DateSource));
    }

    [Fact]
    public void 上交所总数从pageHelp读()
        => Assert.Equal(13, SseSuspensionProviderBase.ParsePage(SseStockJson, "测试").Total);

    /// <summary>出错时整个响应包在圆括号里、success=false——要抛，不能当成"那个月没有"。</summary>
    [Fact]
    public void 上交所报错要抛_不能当成空()
    {
        const string err = """({"jsonCallBack":"null","success":"false","errorMsg":"SOA service parameter [startStopDate: 2026-09-29] has format error","errorType":"ExceptionInterceptor"})""";
        var ex = Assert.Throws<InvalidOperationException>(() => SseSuspensionProviderBase.ParsePage(err, "测试"));
        Assert.Contains("format error", ex.Message);
    }

    [Fact]
    public void 上交所返回的不是JSON要抛()
        => Assert.Throws<RateLimitedException>(() => SseSuspensionProviderBase.ParsePage("<html>拦截</html>", "测试"));

    [Fact]
    public void 深交所xlsx_时刻拆成日期和开市或几点()
    {
        var rows = SzseSuspensionProvider.Parse(Convert.FromBase64String(SzseXlsx0929));

        Assert.Equal(6, rows.Count);
        var r = rows.Single(x => x.Code == "300527");
        Assert.Equal(("sz", "1天"), (r.Market, r.Kind));
        Assert.Equal((new DateOnly(2026, 9, 29), SuspensionRow.AtOpen), (r.StartDay!.Value, r.StartTime));
        Assert.Equal((new DateOnly(2026, 9, 30), SuspensionRow.AtOpen), (r.EndDay!.Value, r.EndTime));
        var etf = rows.Single(x => x.Code == "159972");
        Assert.Equal(("1小时", "10:30:00"), (etf.Kind, etf.EndTime));
    }

    [Fact]
    public void 深交所没有记录的月份只有表头_返回空()
        => Assert.Empty(SzseSuspensionProvider.Parse(Convert.FromBase64String(SzseXlsxEmpty)));

    [Fact]
    public void 深交所返回的不是xlsx要抛()
        => Assert.Throws<RateLimitedException>(() => SzseSuspensionProvider.Parse("<html/>"u8.ToArray()));

    // ─────────────────── ② 全天停牌判据 ───────────────────

    private static readonly List<DateOnly> Cal = BuildCalendar();

    /// <summary>2025-01-02 ~ 2025-02-28 的工作日当交易日（春节那几天在测试里用不到）。</summary>
    private static List<DateOnly> BuildCalendar()
    {
        var list = new List<DateOnly>();
        for (var d = new DateOnly(2025, 1, 2); d <= new DateOnly(2025, 2, 28); d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) list.Add(d);
        return list;
    }

    private static readonly DateOnly AsOf = new(2025, 2, 28);
    private static DateOnly D(int m, int d) => new(2025, m, d);

    private static SuspensionRow Sse(string kind, string stop, DateOnly start, DateOnly? end) =>
        new(SuspensionSource.SseStock, "sh", "600000", "", start, "", end, "", kind, stop, "");

    private static SuspensionRow Sz(string kind, DateOnly? sd, string st, DateOnly? ed, string et) =>
        new(SuspensionSource.Szse, "sz", "000001", "", sd, st, ed, et, kind, "", "");

    private static HashSet<DateOnly> Full(IReadOnlyList<DateOnly>? bars, params SuspensionRow[] rows)
        => SuspensionRule.FullDays(rows, Cal, AsOf, bars);

    [Fact]
    public void 上交所连续停牌_两头都含()
        => Assert.Equal([D(1, 20), D(1, 21), D(1, 22)], Full(null, Sse("LXTP", "", D(1, 20), D(1, 22))).Order());

    [Fact]
    public void 上交所临时停牌写着全天才算()
    {
        Assert.Equal([D(1, 13)], Full(null, Sse("LSTP", "WH", D(1, 13), D(1, 13))));
        Assert.Empty(Full(null, Sse("LSTP", "915", D(1, 13), D(1, 13))));   // 停到 10:30，当天有日K
        Assert.Empty(Full(null, Sse("LSTP", "13", D(1, 13), D(1, 13))));
    }

    [Fact]
    public void 上交所还没复牌_算到停牌后第一根日K之前_没有日K就到今天()
    {
        var row = Sse("LXTP", "", D(2, 24), null);
        Assert.Equal(5, Full(null, row).Count);                              // 2-24 ~ 2-28
        Assert.Equal([D(2, 24), D(2, 25)], Full([D(2, 21), D(2, 26)], row).Order());
    }

    /// <summary>「1天」：开市停、次日开市复牌——停的是当天，复牌日有交易（133 条全部对上）。</summary>
    [Fact]
    public void 深交所1天_复牌日当天有交易()
        => Assert.Equal([D(1, 24)], Full(null, Sz("1天", D(1, 24), "open", D(1, 27), "open")));

    [Fact]
    public void 深交所1小时和盘中临时停牌_一天都不算()
    {
        Assert.Empty(Full(null, Sz("1小时", D(1, 9), "open", D(1, 9), "10:30:00")));
        Assert.Empty(Full(null, Sz("", D(1, 23), "09:34:03", D(1, 23), "09:44:04")));
    }

    /// <summary>盘中才停、次日开市复牌：当天有交易，次日复牌也有交易——一天都不算。</summary>
    [Fact]
    public void 深交所盘中停到次日开市_一天都不算()
        => Assert.Empty(Full(null, Sz("停牌", D(1, 23), "10:56:00", D(1, 24), "open")));

    /// <summary>长期停牌：「停牌」没有复牌时刻，配上之后最早的一条「取消停牌」（835 天全部对上）。</summary>
    [Fact]
    public void 深交所停牌配取消停牌()
    {
        var days = Full(null,
            Sz("停牌", D(1, 10), "open", null, ""),
            Sz("取消停牌", null, "", D(1, 13), "open"));
        Assert.Equal([D(1, 10)], days);
    }

    /// <summary>老数据的「取消停牌」有时把日期写在停牌时间那一列（300332 在 2016-10-26）。</summary>
    [Fact]
    public void 深交所取消停牌的日期写在停牌时间列也认()
    {
        var days = Full(null,
            Sz("停牌", D(1, 20), "open", null, ""),
            Sz("取消停牌", D(1, 23), "open", null, ""));
        Assert.Equal([D(1, 20), D(1, 21), D(1, 22)], days.Order());
    }

    /// <summary>「特停」之后没有取消记录、次日就来一条「1小时」＝特停只停了一天（000016 在 2008-03-12）。</summary>
    [Fact]
    public void 深交所没有取消记录_下一条事件就是终点()
    {
        var days = Full(null,
            Sz("特停", D(1, 13), "open", null, ""),
            Sz("1小时", D(1, 14), "open", D(1, 14), "10:30:00"));
        Assert.Equal([D(1, 13)], days);
    }

    /// <summary>官网开了头没收尾：000656 在 2017-07-19 记了停牌，下一条在六年后，当天起天天有成交。</summary>
    [Fact]
    public void 深交所停牌后本地有日K_就算已复牌()
    {
        var row = Sz("停牌", D(1, 13), "open", null, "");
        Assert.Empty(Full([D(1, 10), D(1, 13), D(1, 14)], row));
        Assert.Equal(35, Full(null, row).Count);                              // 不给日K就一直算到今天（1-13 ~ 2-28）
    }

    /// <summary>同一天叠两条：01-23 盘中停到 01-24 开市，01-24 又有一条「1天」（159561 在 2025-01）——按天取并集。</summary>
    [Fact]
    public void 深交所叠在同一天的两条按天取并集()
    {
        var days = Full(null,
            Sz("停牌", D(1, 23), "10:56:00", D(1, 24), "open"),
            Sz("1天", D(1, 24), "open", D(1, 27), "open"));
        Assert.Equal([D(1, 24)], days);
    }

    [Fact]
    public void 复牌记录的几种写法都认()
    {
        foreach (var k in new[] { "取消停牌", "今起复牌", "复牌", "取消特停", "今起恢复交" })
            Assert.True(SuspensionRule.IsResume(Sz(k, null, "", D(1, 1), "open")), k);
        foreach (var k in new[] { "停牌", "特停", "1天", "今起停牌" })
            Assert.False(SuspensionRule.IsResume(Sz(k, D(1, 1), "open", null, "")), k);
    }

    [Fact]
    public void 问哪些月_没问过的加最近两个月()
    {
        var fetched = new HashSet<DateOnly> { new(2026, 7, 1), new(2026, 8, 1), new(2026, 9, 1) };
        var months = SuspensionFetchPlan.Build(new DateOnly(2026, 6, 1), fetched, new DateOnly(2026, 9, 30), all: false);
        Assert.Equal([new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)], months);

        Assert.Equal(4, SuspensionFetchPlan.Build(new DateOnly(2026, 6, 1), fetched, new DateOnly(2026, 9, 30), all: true).Count);
    }

    // ─────────────────── ③ 本地存取（真 SQLite）───────────────────

    private readonly string _dbPath;
    private readonly SqliteSuspensionRepository _repo;

    public SuspensionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"susp_{Guid.NewGuid():N}.sqlite");
        _repo = new SqliteSuspensionRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 还被连接池占着，留给系统清理 */ }
    }

    /// <summary>
    /// 键不含结束日：还在停牌的那条复牌后补上结束日，要**覆盖**旧行，不能多出一行——
    /// 多出来的话那条"没有结束日"的旧行一直在，这只票就永远被判成还在停牌。
    /// </summary>
    [Fact]
    public void 复牌后补上结束日_覆盖旧行不多出一行()
    {
        _repo.Upsert([Sse("LXTP", "", D(1, 20), null)]);
        _repo.Upsert([Sse("LXTP", "", D(1, 20), D(1, 22))]);

        var rows = _repo.GetByBarCodes(["600000"])["600000"];
        var row = Assert.Single(rows);
        Assert.Equal(D(1, 22), row.EndDay);
    }

    [Fact]
    public void 按Bar代码取_ETF要连市场一起对()
    {
        _repo.Upsert([
            new SuspensionRow(SuspensionSource.SseFund, "sh", "510300", "", D(1, 6), "", D(1, 6), "", "LSTP", "WH", ""),
            new SuspensionRow(SuspensionSource.Szse, "sz", "300527", "", D(1, 7), "open", D(1, 8), "open", "1天", "", ""),
        ]);

        var got = _repo.GetByBarCodes(["sh510300", "sz510300", "300527"]);

        Assert.Single(got["sh510300"]);
        Assert.False(got.ContainsKey("sz510300"));          // 市场对不上
        Assert.Single(got["300527"]);                        // 个股裸码
    }

    [Fact]
    public void 每一列都落库读得回来()
    {
        var row = new SuspensionRow(SuspensionSource.SseFund, "sh", "513100", "纳指ETF", D(1, 6), "", D(1, 6), "",
            "LSTP", "915", "根据基金管理人申请", EndReason: "根据基金管理人申请", ControlType: "",
            EndKind: "LSTP", StartType: "null", EndType: "", DateSource: "2", FullName: "纳指ETF国泰");
        _repo.Upsert([row]);

        Assert.Equal(row, Assert.Single(_repo.GetByBarCodes(["sh513100"])["sh513100"]));
    }

    [Fact]
    public void 问过的月份记得住()
    {
        _repo.MarkMonthFetched(SuspensionSource.Szse, new DateOnly(2026, 9, 15), 152);
        Assert.Equal([new DateOnly(2026, 9, 1)], _repo.GetFetchedMonths(SuspensionSource.Szse));
        Assert.Empty(_repo.GetFetchedMonths(SuspensionSource.SseStock));
    }

    // ─────────────────── ④ 任务 ───────────────────

    private sealed class FakeProvider(string source, Func<DateOnly, List<SuspensionRow>> month) : ISuspensionProvider
    {
        public List<DateOnly> Asked { get; } = [];
        public string Source => source;
        public string Label => source;
        public DateOnly FirstMonth { get; init; } = DateOnly.FromDateTime(DateTime.Today).AddMonths(-3);
#pragma warning disable CS0067
        public event Action<string>? OnStatus;
#pragma warning restore CS0067
        public Task<List<SuspensionRow>> GetMonthAsync(DateOnly m, CancellationToken ct = default)
        {
            Asked.Add(m);
            return Task.FromResult(month(m));
        }
    }

    /// <summary>取到的全存：名册里没有的（可转债）也存，每个月都记成问过。</summary>
    [Fact]
    public async Task 取到的全存_每个月都记成问过()
    {
        var p = new FakeProvider(SuspensionSource.SseStock, m => m.Month == DateTime.Today.Month
            ? [
                new SuspensionRow(SuspensionSource.SseStock, "sh", "600293", "", m, "", null, "", "LXTP", "", ""),
                new SuspensionRow(SuspensionSource.SseStock, "sh", "512390", "", m, "", null, "", "LXTP", "", ""),
                new SuspensionRow(SuspensionSource.SseStock, "sh", "110815", "", m, "", null, "", "LXTP", "", "",
                                  ControlType: "CB"),   // 可转债，我们没有它的日K，照样存
              ]
            : []);

        var r = await new SuspensionTask([p], _repo)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Completed, r.State);
        Assert.Equal(4, p.Asked.Count);                                     // 三个月前起到本月
        Assert.Equal(4, _repo.GetFetchedMonths(SuspensionSource.SseStock).Count);
        var got = _repo.GetByBarCodes(["600293", "sh512390", "110815"]);
        Assert.True(got.ContainsKey("600293"));
        Assert.True(got.ContainsKey("sh512390"));
        Assert.True(got.ContainsKey("110815"));
    }

    [Fact]
    public async Task 第二轮只问没问过的和最近两个月()
    {
        var p = new FakeProvider(SuspensionSource.SseStock, _ => []);
        await new SuspensionTask([p], _repo).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        p.Asked.Clear();

        await new SuspensionTask([p], _repo).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        Assert.Equal(2, p.Asked.Count);

        p.Asked.Clear();
        await new SuspensionTask([p], _repo).RunAsync(new TaskRunArgs(FetchMode.FirstBackfill), CancellationToken.None);
        Assert.Equal(4, p.Asked.Count);                                     // 首次整段回补：全部重问
    }

    /// <summary>一路出错不连累另外两路；有一路出过错整项报失败（绿勾会盖住"那一路今天没取到"）。</summary>
    [Fact]
    public async Task 一路出错_别的路照跑_整项报失败()
    {
        var bad = new FakeProvider(SuspensionSource.SseStock, _ => throw new InvalidOperationException("接口挂了"));
        var good = new FakeProvider(SuspensionSource.Szse, _ => []);

        var r = await new SuspensionTask([bad, good], _repo)
            .RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(TaskState.Failed, r.State);
        Assert.Contains(r.Errors, e => e.Contains("接口挂了"));
        Assert.Single(bad.Asked);                                           // 出错那一路停在第一个月
        Assert.Equal(4, good.Asked.Count);
        Assert.Empty(_repo.GetFetchedMonths(SuspensionSource.SseStock));    // 没取到的月不记成问过
    }
}
