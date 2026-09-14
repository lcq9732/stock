using Microsoft.Data.Sqlite;
using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 分档资金流那两条**取数通道**的等价性与自述（2026-09-14）。
///
/// ════ 为什么要钉这些 ════
/// 逐股补历史现在有两条通道：HttpClient 直连，和真浏览器里做 JSONP。它们打的是同一个
/// 接口、写的是**同一张表**——一旦 URL 或解析在某一条上偷偷跑偏，库里就会出现"一部分票
/// 是一条通道抓的、另一部分是另一条抓的"这种对不上账的局面，而且事后极难查。
/// 所以：URL 只能有一个出处，解析只能有一份代码，这里就是那道闸。
/// </summary>
public class MoneyFlowChannelTests
{
    // ───────────────────── URL 与解析：两条通道共用一份 ─────────────────────

    /// <summary>
    /// 两条通道拿到的 URL 必须逐字相同——它们都只能从 <see cref="MoneyFlowKlineParser.BuildUrl"/> 取。
    /// 浏览器那条只是在后面额外挂一个 JSONP 的 <c>&amp;cb=</c>。
    /// </summary>
    [Fact]
    public void 两条通道的URL同出一源()
    {
        var url = MoneyFlowKlineParser.BuildUrl(EastMoneyMoneyFlowProvider.Host, "600000");

        Assert.StartsWith("https://push2his.eastmoney.com/api/qt/stock/fflow/daykline/get", url);
        Assert.Contains("secid=1.600000", url);
        Assert.Contains("lmt=0", url);
        Assert.Contains("klt=101", url);
        Assert.Contains($"fields1={MoneyFlowKlineParser.Fields1}", url);
        Assert.Contains($"fields2={MoneyFlowKlineParser.Fields2}", url);
    }

    /// <summary>
    /// secid 前缀走 <c>MarketClassifier</c>。920 是**北交所**——按"6/9 开头＝沪市"那套老规则
    /// 会拼成 1.920xxx，东财回 data:null，没异常、没报错，342 只票就那样静默抓不到了。
    /// </summary>
    [Theory]
    [InlineData("600000", "1.600000")]
    [InlineData("000001", "0.000001")]
    [InlineData("920099", "0.920099")]
    public void secid前缀按市场判(string code, string expected)
    {
        Assert.Contains($"secid={expected}", MoneyFlowKlineParser.BuildUrl("h", code));
    }

    /// <summary>13 段齐了才算一行；f51..f63 依次是日期、五档净额、五档占比、收盘价、涨跌幅。</summary>
    [Fact]
    public void 解析一行取十三段()
    {
        var json = "{\"rc\":0,\"data\":{\"klines\":["
                 + "\"2026-09-11,100,-70,-30,40,60,1.0,-0.7,-0.3,0.4,0.6,10.5,1.25\"]}}";

        var rows = MoneyFlowKlineParser.Parse("600000", json, new DateTime(2026, 9, 14));

        var r = Assert.Single(rows);
        Assert.Equal("600000", r.Code);
        Assert.Equal(new DateTime(2026, 9, 11), r.TradeDate);
        Assert.Equal(100, r.MainNet);
        Assert.Equal(-70, r.SmallNet);
        Assert.Equal(-30, r.MidNet);
        Assert.Equal(40, r.BigNet);
        Assert.Equal(60, r.SuperNet);
        Assert.Equal(0.6, r.SuperRatio);
        Assert.Equal(10.5, r.ClosePrice);
        Assert.Equal(1.25, r.ChangeRate);
    }

    /// <summary>停牌/退市/secid 拼错时接口回 data:null——空列表，**不是**异常。
    /// 调用方要靠这个把"接口说没有"跟"抓失败"分开计数。</summary>
    [Fact]
    public void 没数据回空列表()
    {
        Assert.Empty(MoneyFlowKlineParser.Parse("x", "{\"rc\":100,\"data\":null}", DateTime.Now));
        Assert.Empty(MoneyFlowKlineParser.Parse("x", "{\"rc\":0,\"data\":{\"klines\":[]}}", DateTime.Now));
    }

    /// <summary>缺段的行整行跳过，不能把半行写进库。</summary>
    [Fact]
    public void 不足十三段的行跳过()
    {
        var json = "{\"data\":{\"klines\":[\"2026-09-11,1,2,3\","
                 + "\"2026-09-12,100,-70,-30,40,60,1.0,-0.7,-0.3,0.4,0.6,10.5,1.25\"]}}";

        var rows = MoneyFlowKlineParser.Parse("600000", json, DateTime.Now);

        Assert.Equal(new DateTime(2026, 9, 12), Assert.Single(rows).TradeDate);
    }

    // ───────────────────── 浏览器通道的自述 ─────────────────────

    /// <summary>
    /// 节奏参数就是 publish/moneyflow-transfer/moneyflow-fetch.html 里实跑验证过的那套。
    /// 钉住它是因为**这些数字是用真金白银的封禁换来的**：东财按累计请求数切
    /// （实测连发 16~35 个），谁顺手把间隔调快、把歇取消，这一项就又抓不到了。
    /// </summary>
    [Fact]
    public void 浏览器通道的节奏与已验证的html一致()
    {
        var p = ChromeCdpMoneyFlowFetcher.Pace.Verified;

        Assert.Equal(TimeSpan.FromSeconds(2), p.Delay);
        Assert.Equal(10, p.BatchMin);
        Assert.Equal(15, p.BatchMax);
        Assert.Equal(TimeSpan.FromMinutes(2), p.RestMin);
        Assert.Equal(TimeSpan.FromMinutes(5), p.RestMax);
    }

    [Fact]
    public async Task 两条通道各自报阈值()
    {
        await using var browser = new ChromeCdpMoneyFlowFetcher(
            Path.Combine(Path.GetTempPath(), "mf-cdp-test"), port: 19334);
        var http = new EastMoneyMoneyFlowProvider(new RateLimiter(1, TimeSpan.Zero));

        Assert.Equal(25, browser.GiveUpAfterConsecutiveFailures);
        Assert.Equal(15, http.GiveUpAfterConsecutiveFailures);
        Assert.Null(browser.PausedUntil);
        Assert.NotEqual(browser.ChannelName, http.ChannelName);
    }

    /// <summary>
    /// 直连那条必须把「连不上」跟「被限流」分开说：前者等多久都不会好（网关按域名拦的），
    /// 后者等着就行。2026-09-11 把前者报成后者，人照着"等一会儿"等了一整天。
    /// </summary>
    [Fact]
    public void 直连通道区分连不上和被限流()
    {
        var http = new EastMoneyMoneyFlowProvider(new RateLimiter(1, TimeSpan.Zero));

        Assert.Contains("网关", http.ExhaustedVerdict("无法连接东财资金流接口（600000）：xxx"));
        Assert.Contains("browser", http.ExhaustedVerdict("无法连接东财资金流接口（600000）：xxx"));
        Assert.Contains("限流", http.ExhaustedVerdict("服务端回了 403"));
        Assert.DoesNotContain("网关", http.ExhaustedVerdict("服务端回了 403"));
    }

    // ───────────────────── 任务侧：通道细节一律由通道自报 ─────────────────────

    /// <summary>可以随意设定阈值和失败行为的假通道。</summary>
    private sealed class FakeChannel(int giveUpAfter, Func<string, List<NetInflowDetail>> reply)
        : IMoneyFlowDetailFetcher
    {
        public List<string> Asked { get; } = [];
#pragma warning disable CS0067   // 这个假通道不发状态
        public event Action<string>? OnStatus;
#pragma warning restore CS0067
        public string ChannelName => "假通道";
        public DateTime? PausedUntil => null;
        public int GiveUpAfterConsecutiveFailures => giveUpAfter;
        public string ExhaustedVerdict(string? lastError) => "通道自己说的收尾原因";

        public Task<List<NetInflowDetail>> FetchAsync(string code, CancellationToken ct = default)
        {
            Asked.Add(code);
            return Task.FromResult(reply(code));
        }
    }

    /// <summary>连续失败到**通道自报**的阈值就收尾，且日志里说的是通道给的那句话。</summary>
    [Fact]
    public async Task 连续失败按通道自报的阈值收尾()
    {
        using var env = new TaskEnv();
        env.FiveStocksAllMissing();
        var channel = new FakeChannel(giveUpAfter: 2,
            _ => throw new InvalidOperationException("被切了"));

        var (result, progress) = await env.RunAsync(channel);

        // 阈值 2＝问到第 2 只就收尾，第 3 只不该被问
        Assert.Equal(2, channel.Asked.Count);
        Assert.Contains(progress, p => p.Text.Contains("通道自己说的收尾原因"));
        Assert.Contains(result.Errors, e => e.Contains("通道自己说的收尾原因"));
    }

    /// <summary>每轮上限可以按通道给（浏览器那条 20 只，直连那条 30 只）。</summary>
    [Fact]
    public async Task 每轮上限按通道给()
    {
        using var env = new TaskEnv();
        env.FiveStocksAllMissing();
        var channel = new FakeChannel(giveUpAfter: 99, code =>
            [new NetInflowDetail { Code = code, TradeDate = TaskEnv.Days[0], MainNet = 1 }]);

        var (_, progress) = await env.RunAsync(channel, maxPerRun: 2);

        Assert.Equal(2, channel.Asked.Count);
        Assert.Contains(progress, p => p.Text.Contains("每轮上限 2 只"));
    }

    /// <summary>补历史任务跑一轮要的那点家当：临时库、名册、日K（＝期望行数）。</summary>
    private sealed class TaskEnv : IDisposable
    {
        internal static readonly DateTime[] Days =
            [.. Enumerable.Range(1, 6).Select(i => DateTime.Today.AddDays(-i)).OrderBy(d => d)];

        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mfChan_{Guid.NewGuid():N}");
        private readonly FetchPaths _paths;
        private readonly SqliteNetInflowDetailRepository _repo;
        private readonly SqliteBarRepository _bars;

        public TaskEnv()
        {
            Directory.CreateDirectory(Path.Combine(_dir, "local"));
            _paths = new FetchPaths(_dir);
            _repo = new SqliteNetInflowDetailRepository(_paths.CurrentDb);
            _repo.EnsureSchema();
            _bars = new SqliteBarRepository(_paths.CurrentDb);
            _bars.EnsureSchema();
            var calendar = new SqliteTradingDayRepository(_paths.CurrentDb);
            calendar.EnsureSchema();
            calendar.Upsert(Days.Select(d => (DateOnly.FromDateTime(d), "szse")));
        }

        /// <summary>五只票，各自窗口内有日K、资金流一行都没有＝全都得排队。</summary>
        public void FiveStocksAllMissing()
        {
            string[] codes = ["000001", "000002", "000004", "600519", "600520"];
            SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, codes.Select(c => (c, c)));
            foreach (var code in codes)
                _bars.InsertOrRefreshUnconfirmed(Days.Select(d => new Bar
                {
                    Code = code, Granularity = "day", PeriodStart = d,
                    Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1,
                    FetchedAt = d.AddHours(20),
                }));
        }

        public async Task<(TaskRunResult Result, List<TaskProgress> Progress)> RunAsync(
            IMoneyFlowDetailFetcher channel, int? maxPerRun = null)
        {
            var task = new MoneyFlowBackfillTask(_paths, _repo, channel,
                                                 progressInterval: TimeSpan.Zero,
                                                 maxPerRun: maxPerRun);
            var seen = new List<TaskProgress>();
            task.OnProgress += p => seen.Add(p);
            var result = await task.RunAsync(new TaskRunArgs(), CancellationToken.None);
            return (result, seen);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }
}
