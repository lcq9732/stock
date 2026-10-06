using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Remote;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Scheduling;
using StockPlatform.Scheduling.Tasks;
using StockPlatform.Tasks;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 【ETF日K】【ETF日K·不复权】的增量：窗口里没有交易日就不发请求（2026-10-06）。
///
/// 节假日实测：ETF日K 1694 只逐只请求 27 分钟、不复权那项有除权事件的 325 只逐只请求 9 分钟，
/// 都写入 0 行。判据是 <c>IncrementalWindowCalculator.NoTradingDayIn</c>（纯函数那几条在
/// <see cref="BarHolidaySkipTests"/>），这里用真 SQLite 的 TradingDay 表按**请求次数**验它接上了。
/// </summary>
public class EtfHolidaySkipTests : IDisposable
{
    private readonly string _tmp;
    private readonly FetchPaths _paths;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteDividendRepository _divs;
    private readonly SqliteTradingDayRepository _tradingDays;
    private readonly IManifestStore _manifest;

    private static readonly string[] Etfs = ["sh510300", "sz159915"];

    /// <summary>本地水位：三天前（已确认）。窗口是 [前天, 今天]。</summary>
    private static DateTime Watermark => DateTime.Today.AddDays(-3);

    public EtfHolidaySkipTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"etfHoliday_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _paths = new FetchPaths(_tmp);
        _bars = new SqliteBarRepository(_paths.CurrentDb);
        _bars.EnsureSchema();
        _divs = new SqliteDividendRepository(_paths.CurrentDb);
        _divs.EnsureSchema();
        _tradingDays = new SqliteTradingDayRepository(_paths.CurrentDb);
        _tradingDays.EnsureSchema();
        _manifest = new JsonManifestStore(_paths.ManifestPath);
        SqliteStockMetaUpsert.Upsert(_paths.CurrentDb, Etfs.Select(c => (c, "ETF" + c)),
                                     SqliteStockMetaUpsert.TypeEtf);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { /* 临时目录 */ }
    }

    /// <summary>这几只这个口径都追到 <see cref="Watermark"/>，且是收盘后抓的。</summary>
    private void SeedUpToWatermark(string granularity, params string[] codes)
    {
        var rows = new List<Bar>();
        foreach (var code in codes)
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
    /// 不跳周末——只关心"窗口里有没有交易日"，跳了反而让结果随测试当天变。
    /// </summary>
    private void SeedCalendar(int closedDays)
    {
        var days = new List<(DateOnly, string)>();
        for (var d = Watermark.AddDays(-30); d <= DateTime.Today.AddDays(10); d = d.AddDays(1))
            if (d <= Watermark || d > Watermark.AddDays(closedDays))
                days.Add((DateOnly.FromDateTime(d), ITradingDayRepository.SzseSource));
        _tradingDays.Upsert(days);
    }

    /// <summary>给 sh510300 记一次除权事件（让不复权那项走"必须抓"那一路）。</summary>
    private void SeedExDividend(string code)
        => _divs.ReplaceByCode(code,
        [
            new DividendRow
            {
                Code = code, AnnounceDate = Watermark.AddDays(-60), Progress = "实施",
                ExDate = Watermark.AddDays(-50), FetchedAt = DateTime.Now,
            },
        ]);

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

    private EtfBarTask NewEtfBarTask(CountingFetcher f)
        => new(_paths,
               new BarSourceHolder(new NamedBarSource("Mock", f, new MockStockListProvider(() => []))),
               new MockStockListProvider(() => Etfs.Select(c => new StockListEntry(c, "ETF" + c)).ToList()),
               _manifest, _tradingDays);

    private static async Task<(TaskRunResult Result, List<string> Log)> RunAsync<T>(FetchTaskBase<T> task)
    {
        var log = new List<string>();
        task.OnProgress += p => log.Add(p.Text);
        var result = await task.RunAsync(new TaskRunArgs(Mode: FetchMode.Incremental), CancellationToken.None);
        return (result, log);
    }

    // ── 【ETF日K】────────────────────────────────────────────────

    /// <summary>⭐ 水位之后到今天全是休市：一个请求都不发。</summary>
    [Fact]
    public async Task ETF日K_窗口全在休市里_不发请求()
    {
        SeedUpToWatermark(Granularity.Day, Etfs);
        SeedCalendar(closedDays: 3);
        var f = new CountingFetcher();

        var (result, log) = await RunAsync(NewEtfBarTask(f));

        Assert.Equal(0, f.Calls);
        Assert.True(result.NothingToDo);
        Assert.Contains(log, l => l.Contains($"其中 {Etfs.Length} 只") && l.Contains("没有交易日"));
    }

    /// <summary>对照：休市只到昨天、今天开市——照常抓。</summary>
    [Fact]
    public async Task ETF日K_窗口里有交易日_照常抓()
    {
        SeedUpToWatermark(Granularity.Day, Etfs);
        SeedCalendar(closedDays: 2);
        var f = new CountingFetcher();

        await RunAsync(NewEtfBarTask(f));

        Assert.Equal(Etfs.Length, f.Calls);
    }

    // ── 【ETF日K·不复权】──────────────────────────────────────────

    /// <summary>
    /// 一只有除权事件（必须抓那一路）、一只没有（本地复制那一路）。
    /// 两条序列都追到水位、值相等，所以没事件那只本来就零请求。
    /// </summary>
    private CountingFetcher SeedRaw()
    {
        SeedUpToWatermark(Granularity.Day, Etfs);
        SeedUpToWatermark(Granularity.DayRaw, Etfs);
        SeedExDividend("sh510300");
        return new CountingFetcher();
    }

    /// <summary>⭐ 有除权事件的那只，窗口全在休市里也不发请求。</summary>
    [Fact]
    public async Task 不复权_有除权事件_窗口全在休市里_不发请求()
    {
        var f = SeedRaw();
        SeedCalendar(closedDays: 3);

        var (_, log) = await RunAsync(new EtfRawBarTask(_paths, _bars, _divs, f, _manifest, _tradingDays));

        Assert.Equal(0, f.Calls);
        Assert.Contains(log, l => l.Contains("其中 1 只有除权事件、但上次之后没有新交易日"));
    }

    /// <summary>对照：今天开市——有事件那只照常抓，没事件那只照旧零请求。</summary>
    [Fact]
    public async Task 不复权_有除权事件_窗口里有交易日_照常抓()
    {
        var f = SeedRaw();
        SeedCalendar(closedDays: 2);

        await RunAsync(new EtfRawBarTask(_paths, _bars, _divs, f, _manifest, _tradingDays));

        Assert.Equal(1, f.Calls);
    }

    /// <summary>对照：没注入日历就是老行为——有事件那只照发请求。</summary>
    [Fact]
    public async Task 不复权_没有日历_照常抓()
    {
        var f = SeedRaw();
        SeedCalendar(closedDays: 3);

        await RunAsync(new EtfRawBarTask(_paths, _bars, _divs, f, _manifest));

        Assert.Equal(1, f.Calls);
    }
}
