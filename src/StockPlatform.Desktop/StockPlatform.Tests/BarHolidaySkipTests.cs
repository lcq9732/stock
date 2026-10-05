using StockPlatform.Data.Orchestration;
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
/// 个股日K三口径的增量：窗口里没有交易日就不发请求（2026-10-05）。
///
/// 起因：计划层只跳周六周日，国庆 10-01、10-02 是工作日照跑；水位停在 09-30 的全市场
/// 每只窗口都是 [10-01, 今天]，三个口径各发 5572 个请求、各跑 1.5 小时、写入 0 行。
///
/// 判据在 <see cref="IncrementalWindowCalculator.NoTradingDayIn"/>（纯函数那几条），
/// 后面几条用真 SQLite 的 TradingDay 表 + 离线模拟源，按**请求次数**验它真的接上了。
/// </summary>
public class BarHolidaySkipTests : IDisposable
{
    // ── 判据本身 ──────────────────────────────────────────────

    private static readonly TradingCalendar NationalDay = new(new[]
    {
        new DateTime(2026, 9, 28), new DateTime(2026, 9, 29), new DateTime(2026, 9, 30),
        new DateTime(2026, 10, 8), new DateTime(2026, 10, 9),
    });

    [Fact]
    public void 窗口整段在假期里_判为没有交易日()
        => Assert.True(IncrementalWindowCalculator.NoTradingDayIn(
            NationalDay, new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)));

    [Fact]
    public void 假期后第一个交易日_窗口里有交易日()
        => Assert.False(IncrementalWindowCalculator.NoTradingDayIn(
            NationalDay, new DateTime(2026, 10, 1), new DateTime(2026, 10, 8)));

    /// <summary>最新那根是盘中抓的：起点回退到它自己（交易日），照样要重抓。</summary>
    [Fact]
    public void 最新一根盘中抓的_假期里也要重抓()
    {
        var start = IncrementalWindowCalculator.IncrementalStart(
            (new DateTime(2026, 9, 30), new DateTime(2026, 9, 30, 10, 0, 0)), new DateTime(2026, 10, 5), 3);
        Assert.Equal(new DateTime(2026, 9, 30), start);
        Assert.False(IncrementalWindowCalculator.NoTradingDayIn(NationalDay, start, new DateTime(2026, 10, 5)));
    }

    [Fact]
    public void 没有日历_放行()
    {
        Assert.False(IncrementalWindowCalculator.NoTradingDayIn(
            null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)));
        Assert.False(IncrementalWindowCalculator.NoTradingDayIn(
            new TradingCalendar([]), new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)));
    }

    /// <summary>新股回看 3 年，起点早于日历首日——日历不知道那段，放行。</summary>
    [Fact]
    public void 窗口起点早于日历首日_放行()
        => Assert.False(IncrementalWindowCalculator.NoTradingDayIn(
            NationalDay, new DateTime(2023, 10, 1), new DateTime(2026, 9, 27)));

    // ── 接到任务上（真 SQLite + 离线模拟源）─────────────────────

    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteTradingDayRepository _tradingDays;
    private readonly IManifestStore _manifest;

    /// <summary>本地水位：三天前（已确认）。窗口是 [前天, 今天]。</summary>
    private static DateTime Watermark => DateTime.Today.AddDays(-3);

    public BarHolidaySkipTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"barHoliday_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _tradingDays = new SqliteTradingDayRepository(_paths.CurrentDb);
        _tradingDays.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, [("600000", "测试600000"), ("000001", "测试000001")]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>两只票这个口径都追到 <see cref="Watermark"/>，且是收盘后抓的。</summary>
    private void SeedUpToWatermark(string granularity)
    {
        var rows = new List<Bar>();
        foreach (var code in new[] { "600000", "000001" })
            for (int i = 10; i >= 0; i--)
            {
                var d = Watermark.AddDays(-i);
                rows.Add(new Bar
                {
                    Code = code, Granularity = granularity, PeriodStart = d,
                    Open = MockBarFetcher.Price, Close = MockBarFetcher.Price,
                    High = MockBarFetcher.Price, Low = MockBarFetcher.Price,
                    Volume = 10000, Amount = 10000 * MockBarFetcher.Price, Turnover = 1.5,
                    FetchedAt = d.AddHours(20),
                });
            }
        _bars.InsertOrRefreshUnconfirmed(rows);
    }

    /// <summary>
    /// 日历：水位往前一个月天天开市，之后 <paramref name="closedDays"/> 天休市，再往后天天开市。
    /// 不跳周末——这里只关心"窗口里有没有交易日"，跟星期几无关，跳了反而让结果随测试当天变。
    /// </summary>
    private void SeedCalendar(int closedDays)
    {
        var days = new List<(DateOnly, string)>();
        for (var d = Watermark.AddDays(-30); d <= DateTime.Today.AddDays(10); d = d.AddDays(1))
            if (d <= Watermark || d > Watermark.AddDays(closedDays))
                days.Add((DateOnly.FromDateTime(d), ITradingDayRepository.SzseSource));
        _tradingDays.Upsert(days);
    }

    private sealed class CountingFetcher : IBarDataFetcher
    {
        private readonly MockBarFetcher _inner = new();
        public int Calls;
        public bool SupportsHfq => true;
        public event Action<string>? OnStatus { add => _inner.OnStatus += value; remove => _inner.OnStatus -= value; }
        public Task<(string Name, List<Bar> Bars)> FetchAsync(
            string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return _inner.FetchAsync(code, granularity, start, end, ct);
        }
    }

    private BarSourceHolder Holder(CountingFetcher f)
        => new(new NamedBarSource("Mock", f, new MockStockListProvider(() => [])));

    private static async Task<(TaskRunResult Result, List<string> Log)> RunAsync(BarFetchTaskBase task)
    {
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(Mode: FetchMode.Incremental), CancellationToken.None);
        return (result, log);
    }

    /// <summary>⭐ 水位之后到今天全是休市：前复权一个请求都不发。</summary>
    [Fact]
    public async Task 前复权_窗口全在休市里_不发请求()
    {
        SeedUpToWatermark(Granularity.Day);
        SeedCalendar(closedDays: 3);
        var f = new CountingFetcher();

        var (result, log) = await RunAsync(new StockDayBarTask(_paths, Holder(f), _manifest, _tradingDays));

        Assert.Equal(0, f.Calls);
        Assert.True(result.NothingToDo);
        Assert.Contains(log, l => l.Contains("其中 2 只") && l.Contains("没有交易日"));
    }

    /// <summary>对照：休市只到昨天、今天开市——照常抓。</summary>
    [Fact]
    public async Task 前复权_窗口里有交易日_照常抓()
    {
        SeedUpToWatermark(Granularity.Day);
        SeedCalendar(closedDays: 2);
        var f = new CountingFetcher();

        await RunAsync(new StockDayBarTask(_paths, Holder(f), _manifest, _tradingDays));

        Assert.Equal(2, f.Calls);
    }

    /// <summary>对照：没注入日历（或表空）就是老行为，照发请求。</summary>
    [Fact]
    public async Task 前复权_日历表空_照常抓()
    {
        SeedUpToWatermark(Granularity.Day);
        var f = new CountingFetcher();

        await RunAsync(new StockDayBarTask(_paths, Holder(f), _manifest, _tradingDays));

        Assert.Equal(2, f.Calls);
    }

    /// <summary>⭐ 不复权/后复权走同一个基类判据：连起飞前那次探测都不该发。</summary>
    [Theory]
    [InlineData(Granularity.DayRaw, FetchActionId.StepStockRawBars)]
    [InlineData(Granularity.DayHfq, FetchActionId.StepStockHfqBars)]
    public async Task 不复权后复权_窗口全在休市里_不发请求(string granularity, FetchActionId id)
    {
        SeedUpToWatermark(granularity);
        SeedCalendar(closedDays: 3);
        var f = new CountingFetcher();

        var (result, log) = await RunAsync(
            new StockAdjustedBarTask(_paths, Holder(f), _manifest, _tradingDays, id, granularity));

        Assert.Equal(0, f.Calls);
        Assert.True(result.NothingToDo);
        Assert.Contains(log, l => l.Contains("没有新交易日"));
    }

    [Theory]
    [InlineData(Granularity.DayRaw, FetchActionId.StepStockRawBars)]
    [InlineData(Granularity.DayHfq, FetchActionId.StepStockHfqBars)]
    public async Task 不复权后复权_窗口里有交易日_照常抓(string granularity, FetchActionId id)
    {
        SeedUpToWatermark(granularity);
        SeedCalendar(closedDays: 2);
        var f = new CountingFetcher();

        await RunAsync(new StockAdjustedBarTask(_paths, Holder(f), _manifest, _tradingDays, id, granularity));

        Assert.Equal(3, f.Calls);   // 探一只 + 两只
    }
}
