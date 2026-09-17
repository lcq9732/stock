using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【分档资金流快照】任务本身的行为（2026-09-12 从合并的那一项拆出来）。
///
/// ════ 为什么单独有这一组 ════
/// 这一项是全库时效性最强的数据之一：接口只给**最近一个交易日**，当天收盘后没跑，
/// 下一个交易日开盘一到就永久取不回来（2026-09-09 全市场整天缺失就是这么丢的，
/// 事后补一天要走逐股通道 5500 个请求）。所以三件事必须钉死：
///   ① 盘中拿到的半天数据**不能入库**——写进去会污染当天那一行，事后完全看不出来；
///   ② 盘中那一轮要记「本轮没开工」而不是「完成」，否则计划以为今天做完了、当天不再来；
///   ③ 一行都没拿到要**失败**，不能静悄悄过去——那意味着今天这一天正在丢。
/// 解析本身（13 个字段怎么对应）的测试在 MoneyFlowSnapshotTests，那是 provider 的事。
/// </summary>
public class MoneyFlowSnapshotTaskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mfSnap_{Guid.NewGuid():N}");
    private readonly FetchPaths _paths;
    private readonly SqliteNetInflowDetailRepository _repo;

    public MoneyFlowSnapshotTaskTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "local"));
        _paths = new FetchPaths(_dir);
        _repo = new SqliteNetInflowDetailRepository(_paths.CurrentDb);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ───────────────────────── 假服务端 ─────────────────────────

    /// <summary>翻页：第一页给 body，之后给空 diff（provider 据此收工）。</summary>
    private sealed class PageHandler(string firstPage) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = Calls == 1 ? firstPage : EmptyPage;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>f124 是行情时间戳（Unix 秒，本地时区）——"收盘了没有"全看它。</summary>
    private static long Stamp(DateTime local) =>
        (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    /// <summary>一页：total＝服务端自报的全市场只数，codes＝这一页里有数据的票。</summary>
    private static string Page(DateTime quoteTime, int total, params string[] codes)
    {
        // 故意不用 raw string：JSON 里全是花括号，跟插值的 {{ }} 搅在一起极易写错。
        var rows = codes.Select(c =>
            "{" + $"\"f2\":7.01,\"f3\":0.43,\"f12\":\"{c}\",\"f62\":-878110.0,\"f66\":-2844000.0,\"f69\":-2.16," +
            $"\"f72\":1965890.0,\"f75\":1.49,\"f78\":-5148156.0,\"f81\":-3.91,\"f84\":6026266.0,\"f87\":4.58," +
            $"\"f124\":{Stamp(quoteTime)},\"f184\":-0.67" + "}");
        return "{" + $"\"rc\":0,\"data\":" + "{" +
               $"\"total\":{total},\"diff\":[{string.Join(",", rows)}]" + "}}";
    }

    /// <summary>空页：provider 据此收工。</summary>
    private const string EmptyPage = "{\"rc\":0,\"data\":{\"total\":0,\"diff\":[]}}";

    private static EastMoneyMoneyFlowSnapshotProvider NewProvider(PageHandler handler) =>
        new(new RateLimiter(maxConcurrency: 1, delayBetweenRequests: TimeSpan.Zero,
                            batchSize: 10_000),
            new HttpClient(handler));

    /// <summary>
    /// 造出收尾核对要的那三份本地数据：交易日历、在市名册、当天的个股日K。
    ///
    /// 判据是"当天有日K的个股有多少只，资金流就该有多少行"，所以这三样缺一不可——
    /// 少了日历判不出是哪个交易日，少了名册判不出日K自己到位没有。
    /// </summary>
    private void SeedLocal(DateTime day, int roster, int bars)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();

        void Exec(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"INSERT OR REPLACE INTO TradingDay(day, source) VALUES('{day:yyyy-MM-dd}', 'test');");
        for (int i = 0; i < roster; i++)
        {
            var code = (600000 + i).ToString();
            Exec($"INSERT OR REPLACE INTO StockMeta(code, name, type) VALUES('{code}', 'T{i}', 'stock');");
            if (i < bars)
                Exec("INSERT OR REPLACE INTO Bar(code, granularity, period_start, close) "
                   + $"VALUES('{code}', 'day', '{day:yyyy-MM-dd} 00:00:00', 1.0);");
        }
        tx.Commit();
    }

    /// <summary>直接往资金流表里塞几行（模拟"上一轮只抓到一半"）。</summary>
    private void SeedFlow(DateTime day, int rows)
    {
        using var conn = new SqliteConnection($"Data Source={_paths.CurrentDb}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < rows; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO NetInflowDetail(code, trade_date, main_net) "
                            + $"VALUES('{600000 + i}', '{day:yyyy-MM-dd}', 1.0);";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private async Task<(TaskRunResult Result, List<TaskProgress> Progress)> RunAsync(
        PageHandler handler, bool withAudit = false)
    {
        var task = new MoneyFlowSnapshotTask(_repo, NewProvider(handler),
            withAudit ? new SqliteMoneyFlowDayAudit(_paths.CurrentDb) : null);
        var seen = new List<TaskProgress>();
        task.OnProgress += p => seen.Add(p);
        var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);
        return (result, seen);
    }

    /// <summary>收盘清算之后的行情时间（15:34），非交易日跑也是这个形状。</summary>
    private static DateTime AfterClose => DateTime.Today.AddDays(-1).AddHours(15).AddMinutes(34);

    /// <summary>盘中（11:30 午休停住的那个时刻）。</summary>
    private static DateTime Intraday => DateTime.Today.AddHours(11).AddMinutes(30);

    // ───────────────────────── 测试 ─────────────────────────

    [Fact]
    public async Task 收盘后的快照整天落库()
    {
        var handler = new PageHandler(Page(AfterClose, total: 3, "000001", "000002", "600519"));

        var (result, _) = await RunAsync(handler);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Null(result.SkippedReason);
        Assert.Empty(result.Errors);
        Assert.Equal(3, _repo.Count());
        Assert.Equal(AfterClose.Date, _repo.Query("000001", 1).Single().TradeDate);
    }

    [Fact]
    public async Task 盘中的半天数据不入库()
    {
        // 写进去会污染当天那一行，而且事后完全看不出来——这是这一项最要命的一条。
        var handler = new PageHandler(Page(Intraday, total: 3, "000001", "000002", "600519"));

        var (result, _) = await RunAsync(handler);

        Assert.Equal(0, _repo.Count());
        Assert.NotNull(result.SkippedReason);            // 记「本轮没开工」
        Assert.Contains("收盘清算", result.SkippedReason!);
    }

    [Fact]
    public async Task 盘中那轮不能记成完成_否则今天就不再来了()
    {
        // FetchPlanItem.AlreadyRanOn 只认 Ok。盘中这轮要是记成完成，收盘后就不会再跑，
        // 于是这一天的分档资金流永久缺失——2026-09-09 就是这么一天。
        var handler = new PageHandler(Page(Intraday, total: 3, "000001"));

        var (result, _) = await RunAsync(handler);

        Assert.NotNull(result.SkippedReason);
        Assert.False(result.NothingToDo);
    }

    [Fact]
    public async Task 一行都没拿到要失败_不能静悄悄过去()
    {
        // 接口变了或被限流时一行都没有。这一项漏一天就永久补不回来，所以必须是**失败**
        // 而不是"完成，0 行"——后者在界面上是个绿勾，没人会去看。
        var handler = new PageHandler(EmptyPage);

        var (result, _) = await RunAsync(handler);

        Assert.Equal(TaskState.Failed, result.State);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task 少收了几只要报出来()
    {
        // 服务端自报 5 只、没有停牌的，实收 2 只 —— 差额是"某页被限流截断"的唯一信号。
        // 不报的话表现出来只是"今天少几百只"，谁也不会发现。
        var handler = new PageHandler(Page(AfterClose, total: 5, "000001", "000002"));

        var (result, _) = await RunAsync(handler);

        Assert.Equal(2, _repo.Count());                  // 拿到的照样落库
        Assert.Contains(result.Errors, e => e.Contains("少了 3 只"));
    }

    // ──────────── 收尾回查库（2026-09-16 用户要求）────────────
    //
    // 抓取侧那条对账（自报 total − 停牌 − 实收）只证明"这一轮请求收全了"，证明不了
    // "库里真有那么多行"。这一组测的是落库之后再查一次库的那一步。

    [Fact]
    public async Task 收尾核对_库里齐了就正常完成()
    {
        SeedLocal(AfterClose.Date, roster: 3, bars: 3);
        var handler = new PageHandler(Page(AfterClose, total: 3, "600000", "600001", "600002"));

        var (result, progress) = await RunAsync(handler, withAudit: true);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Empty(result.Errors);
        Assert.Contains(progress, p => p.Text.Contains("已齐"));
    }

    [Fact]
    public async Task 收尾核对_库里就是不齐要整项失败_并说清为什么()
    {
        // 抓回来 1 只，可当天有日线的个股是 10 只——这就是"翻页被截断/整项没跑成"的样子。
        // 必须红：这份数据下一个交易日开盘后就永久取不回来了。
        SeedLocal(AfterClose.Date, roster: 10, bars: 10);
        var handler = new PageHandler(Page(AfterClose, total: 1, "600000"));

        var (result, progress) = await RunAsync(handler, withAudit: true);

        Assert.Equal(TaskState.Failed, result.State);
        Assert.Contains(result.Errors, e => e.Contains("差 9 只"));
        // 标红必须带上"为什么红"：差多少、拿什么比的、为什么现在就得补
        Assert.Contains(progress, p => p.Text.Contains("永久取不回来"));
        Assert.Contains(progress, p => p.Text.Contains("当天有日线的个股是 10 只"));
    }

    [Fact]
    public async Task 收尾核对_差一两只不算缺()
    {
        // 实测"当天个股日K只数 − 资金流行数"只出现过 0 和 +1，容差 2 是留给这个的。
        SeedLocal(AfterClose.Date, roster: 100, bars: 100);
        SeedFlow(AfterClose.Date, rows: 99);
        var handler = new PageHandler(Page(AfterClose, total: 99, "600000"));

        var (result, _) = await RunAsync(handler, withAudit: true);

        Assert.Equal(TaskState.Completed, result.State);
    }

    [Fact]
    public async Task 收尾核对_个股日K自己没到位时不判失败()
    {
        // 名册 100 只、当天日K只有 50 只 → 期望值本身不可信（日K还没抓完）。
        // 这时候判"资金流缺了"是把日K的锅算到这一项头上，会让人去重跑一个本来没错的任务。
        SeedLocal(AfterClose.Date, roster: 100, bars: 50);
        var handler = new PageHandler(Page(AfterClose, total: 1, "600000"));

        var (result, progress) = await RunAsync(handler, withAudit: true);

        Assert.Equal(TaskState.Completed, result.State);
        Assert.Contains(progress, p => p.Text.Contains("无法核对"));
    }

    [Fact]
    public async Task 收尾核对_本轮没开工而当天还空着_也要报出来()
    {
        // 熔断/盘中那种"没开工"的轮次最危险：什么都没抓，而当天要是还空着，
        // 没人提醒的话下一个交易日开盘它就永久没了。所以跳过的轮次也要回查库。
        SeedLocal(DateTime.Today, roster: 10, bars: 10);
        var handler = new PageHandler(Page(Intraday, total: 10, "600000"));

        var (result, _) = await RunAsync(handler, withAudit: true);

        Assert.NotNull(result.SkippedReason);            // 仍记「本轮没开工」，今天还能再来
        Assert.Contains(result.Errors, e => e.Contains("差 10 只"));
    }

    [Fact]
    public void 判据本身_没有交易日历时不下结论()
    {
        // 日历空着判不出"最近一个交易日是哪天"。这种状态要显示成"没法核对"，
        // 不能默认成"缺了"——那会天天红着，人很快就不看了。
        var status = new SqliteMoneyFlowDayAudit(_paths.CurrentDb).Check();

        Assert.Null(status.Day);
        Assert.False(status.IsAlert);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public void 快照归日更_补历史归定期()
    {
        // 拆这两项的全部意义就在这一条断言上（2026-09-12）：
        // 快照漏一天永久没了，必须日更；补历史 120 天内随时补，留在"空闲时补"。
        Assert.Equal(PlanGroupKind.Daily,
            FetchTaskCatalog.DefaultGroupOf(FetchActionId.FetchMoneyFlowSnapshot));
        Assert.Contains(FetchActionId.FetchMoneyFlowSnapshot, FetchTaskCatalog.DailyOrder);

        Assert.Equal(PlanGroupKind.Periodic,
            FetchTaskCatalog.DefaultGroupOf(FetchActionId.FetchMoneyFlowDetail));
        Assert.DoesNotContain(FetchActionId.FetchMoneyFlowDetail, FetchTaskCatalog.DailyOrder);
    }

    [Fact]
    public void 两项的数据源要分得开()
    {
        // 分开声明才能并行：快照走 push2delay、补历史走 push2his，是不同域名、独立计数。
        // 合在一项里时 Sources 同时声明了两个，等于把两条互不相干的通道绑成一个占用。
        var snap = FetchTaskCatalog.Info(FetchActionId.FetchMoneyFlowSnapshot).EffectiveSources;
        var back = FetchTaskCatalog.Info(FetchActionId.FetchMoneyFlowDetail).EffectiveSources;

        Assert.Contains(DataSourceId.EmPush2Delay, snap);
        Assert.Contains(DataSourceId.EmPush2His, back);
        Assert.Empty(snap.Intersect(back));
    }
}
