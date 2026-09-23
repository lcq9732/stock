using Microsoft.Data.Sqlite;
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
/// 【ETF换手率校正】（2026-09-23，见 doc/etf-turnover-recalc-design.md）。
///
/// 数字全部取自实测：510150 消费ETF招商 2024-09-30 成交 16,919,316 手；上交所份额
/// 09-27 = 202,317.86 万份、09-30 = 349,717.86 万份。腾讯前复权给 83.63（÷09-27），
/// 不复权给 48.38（÷09-30）——腾讯自己口径不统一，我们统一按前一交易日（2026-09-23 用户定）。
///
/// 钉死的几件事：
///   ① 算法是 ÷ **前一交易日**份额（不是当天）
///   ② 「日常增量」只报告、一行不写；「彻底重查」才写回，而且只动 turnover
///   ③ 空值（有成交、换手率 0）会被补上；没成交的 0 不动
///   ④ 没有前一交易日份额的行判不了，原样不动
///   ⑤ 上交所空响应：3 天以前的记进「确认没有」、下次不再问；近几天的不记
///   ⑥ 响应骨架不对要抛，不能当成空列表（否则那一天被永久判成没有数据）
///
/// 全部打在临时 SQLite 上（假仓储验不到真 SQL），一行都不碰 current.sqlite。
/// </summary>
public class EtfTurnoverRecalcTests : IDisposable
{
    private static readonly DateOnly D0927 = new(2024, 9, 27);
    private static readonly DateOnly D0930 = new(2024, 9, 30);
    private static readonly DateOnly D1008 = new(2024, 10, 8);

    private readonly string _dbPath;
    private readonly SqliteBarRepository _bars;
    private readonly SqliteEtfShareRepository _shares;
    private readonly SqliteTradingDayRepository _days;
    private readonly SqliteDailyFetchNoDataRepository _noData;

    public EtfTurnoverRecalcTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"etfturn_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _shares = new SqliteEtfShareRepository(_dbPath);
        _days = new SqliteTradingDayRepository(_dbPath);
        _noData = new SqliteDailyFetchNoDataRepository(_dbPath);
        _days.Upsert(new[] { D0927, D0930, D1008 }.Select(d => (d, "szse")));
    }

    /// <summary>不调 ClearAllPools——理由见 MarginShortBalanceFillTests.Dispose。</summary>
    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 还被连接池占着，留给系统清理 */ }
    }

    // ─────────────────── 判据（纯函数）───────────────────

    [Fact]
    public void 期望值_按前一交易日份额算_对上腾讯前复权()
    {
        Assert.Equal(83.63, EtfTurnoverRule.Expected(16_919_316, 202_317.86));
        // 除以当天份额得到的正是不复权接口那天给的值
        Assert.Equal(48.38, EtfTurnoverRule.Expected(16_919_316, 349_717.86));
    }

    [Theory]
    [InlineData(16_919_316.0, 83.63, 202_317.86, EtfTurnoverVerdict.Consistent)]
    [InlineData(16_919_316.0, 83.62, 202_317.86, EtfTurnoverVerdict.Consistent)]   // 末位差一格算一致
    [InlineData(16_919_316.0, 48.38, 202_317.86, EtfTurnoverVerdict.Wrong)]
    [InlineData(16_919_316.0, 0.0, 202_317.86, EtfTurnoverVerdict.Missing)]
    [InlineData(16_919_316.0, 83.63, null, EtfTurnoverVerdict.Unjudgeable)]
    [InlineData(0.0, 0.0, 202_317.86, EtfTurnoverVerdict.NotApplicable)]
    [InlineData(1.0, 0.0, 202_317.86, EtfTurnoverVerdict.Consistent)]            // 期望值本身就是 0.00
    public void 判据_五种结果(double volume, double stored, double? prevShare, EtfTurnoverVerdict want)
        => Assert.Equal(want, EtfTurnoverRule.Judge(volume, stored, prevShare));

    [Fact]
    public void 判据_库里是NULL也算空值()
        => Assert.Equal(EtfTurnoverVerdict.Missing, EtfTurnoverRule.Judge(16_919_316, null, 202_317.86));

    // ─────────────────── 任务（真 SQLite）───────────────────

    [Fact]
    public async Task 日常增量_补份额_只报告不写库()
    {
        SaveEtfBars(D0930, volume: 16_919_316, dayTurnover: 83.63, rawTurnover: 48.38);
        var provider = new FakeProvider()
            .With(D0927, ("510150", 202_317.86))
            .With(D0930, ("510150", 349_717.86))
            .With(D1008, ("510150", 397_117.86));

        var task = NewTask(provider);
        await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(3, _shares.GetDays().Count);
        var r = task.LastResult!;
        Assert.Equal(2, r.Wrong);          // 不复权 + 回测序列（它的换手率抄自不复权）
        Assert.Equal(0, r.Written);
        Assert.Equal(48.38, ReadTurnover("sh510150", Granularity.DayRaw, D0930));   // 没动
    }

    [Fact]
    public async Task 彻底重查_错值改对_只动换手率()
    {
        SaveEtfBars(D0930, volume: 16_919_316, dayTurnover: 83.63, rawTurnover: 48.38);
        var task = NewTask(new FakeProvider()
            .With(D0927, ("510150", 202_317.86))
            .With(D0930, ("510150", 349_717.86)));

        await task.RunAsync(new TaskRunArgs(FetchMode.Thorough), CancellationToken.None);

        Assert.Equal(2, task.LastResult!.Written);   // 不复权 + 回测序列
        Assert.Equal(83.63, ReadTurnover("sh510150", Granularity.DayAdj, D0930));
        Assert.Equal(83.63, ReadTurnover("sh510150", Granularity.DayRaw, D0930));
        Assert.Equal(83.63, ReadTurnover("sh510150", Granularity.Day, D0930));
        var (open, volume, amount) = ReadOthers("sh510150", Granularity.DayRaw, D0930);
        Assert.Equal(0.602, open);
        Assert.Equal(16_919_316, volume);
        Assert.Equal(956_041_900, amount);
    }

    [Fact]
    public async Task 彻底重查_空值补上_没成交的0不动()
    {
        SaveEtfBars(D0930, volume: 16_919_316, dayTurnover: 0, rawTurnover: 0);
        SaveEtfBars(D1008, volume: 0, dayTurnover: 0, rawTurnover: 0);
        var task = NewTask(new FakeProvider()
            .With(D0927, ("510150", 202_317.86))
            .With(D0930, ("510150", 349_717.86)));

        await task.RunAsync(new TaskRunArgs(FetchMode.Thorough), CancellationToken.None);

        Assert.Equal(3, task.LastResult!.Missing);   // 09-30 三个口径；10-08 没成交不算
        Assert.Equal(83.63, ReadTurnover("sh510150", Granularity.Day, D0930));
        Assert.Equal(83.63, ReadTurnover("sh510150", Granularity.DayRaw, D0930));
        Assert.Equal(0, ReadTurnover("sh510150", Granularity.Day, D1008));
    }

    [Fact]
    public async Task 没有前一交易日份额_判不了_原样不动()
    {
        // 09-27 是日历第一天：它没有前一交易日
        SaveEtfBars(D0927, volume: 2_166_324, dayTurnover: 11.17, rawTurnover: 0);
        var task = NewTask(new FakeProvider().With(D0927, ("510150", 193_917.86)));

        await task.RunAsync(new TaskRunArgs(FetchMode.Thorough), CancellationToken.None);

        Assert.Equal(3, task.LastResult!.UnjudgeableBefore2012 + task.LastResult.UnjudgeableOther);
        Assert.Equal(0, task.LastResult.Written);
        Assert.Equal(0, ReadTurnover("sh510150", Granularity.DayRaw, D0927));
    }

    [Fact]
    public async Task 已有份额的日子不再请求()
    {
        _shares.Upsert([new EtfShareRow("510150", D0927, 202_317.86)]);
        var provider = new FakeProvider().With(D0930, ("510150", 349_717.86)).With(D1008, ("510150", 1));

        await NewTask(provider).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.DoesNotContain(D0927, provider.Asked);
        Assert.Contains(D0930, provider.Asked);
    }

    [Fact]
    public async Task 空响应_三天以前的记进确认没有_下次不再问()
    {
        var provider = new FakeProvider();   // 每天都返回空
        await NewTask(provider).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        var confirmed = _noData.GetConfirmed(IDailyFetchNoDataRepository.EtfShareDataset);
        Assert.Equal(3, confirmed.Count);   // 三天都是 2024 年的，早就过了发布期

        var again = new FakeProvider();
        await NewTask(again).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);
        Assert.Empty(again.Asked);
    }

    [Fact]
    public async Task 空响应_近几天的不记_下次还要问()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        _days.Upsert([(today, "szse")]);
        await NewTask(new FakeProvider()).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.DoesNotContain(today, _noData.GetConfirmed(IDailyFetchNoDataRepository.EtfShareDataset));
    }

    [Fact]
    public async Task 二零一二年以前的日子一个请求都不发()
    {
        var old = new DateOnly(2011, 12, 30);
        _days.Upsert([(old, "szse")]);
        var provider = new FakeProvider();
        await NewTask(provider).RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.DoesNotContain(old, provider.Asked);
    }

    [Fact]
    public async Task 上交所没列的沪市ETF_单独报出来_不静默()
    {
        // 货币 ETF 不在上交所「ETF 规模」接口里：不报的话它们的换手率会安安静静停在 0
        ExecSql("INSERT INTO StockMeta (code, name, type) VALUES ('sh510150', '消费ETF招商', 'etf'), "
              + "('sh511620', '货币ETF国泰', 'etf');");
        var task = NewTask(new FakeProvider().With(D0927, ("510150", 202_317.86)));

        await task.RunAsync(new TaskRunArgs(FetchMode.Incremental), CancellationToken.None);

        Assert.Equal(["sh511620"], task.LastResult!.NotInSseList);
    }

    // ─────────────────── 解析（不联网）───────────────────

    [Fact]
    public void 解析_真实响应样本()
    {
        const string json = """
            {"actionErrors":[],"pageHelp":{"beginPage":1,"data":[
              {"STAT_DATE":"2024-09-30","ETF_TYPE":"单市","SEC_CODE":"510150","NUM":"1","SEC_NAME":"消费ETF招商","TOT_VOL":"349717.86"},
              {"STAT_DATE":"2024-09-30","ETF_TYPE":"单市","SEC_CODE":"510170","NUM":"2","SEC_NAME":"商品ETF","TOT_VOL":"23091.48"}
            ],"pageSize":2000,"total":2}}
            """;
        var rows = SseEtfShareProvider.Parse(json, D0930);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new EtfShareRow("510150", D0930, 349_717.86), rows[0]);
    }

    [Fact]
    public void 解析_没有数据是空列表()
        => Assert.Empty(SseEtfShareProvider.Parse("""{"pageHelp":{"data":[],"total":0}}""", D0930));

    [Theory]
    [InlineData("<html>请稍后再试</html>")]
    [InlineData("""{"result":[]}""")]
    public void 解析_骨架不对要抛_不能当成空(string body)
        => Assert.Throws<RateLimitedException>(() => SseEtfShareProvider.Parse(body, D0930));

    [Fact]
    public void 解析_一页没收完要抛()
        => Assert.Throws<InvalidOperationException>(() => SseEtfShareProvider.Parse(
            """{"pageHelp":{"data":[{"STAT_DATE":"2024-09-30","SEC_CODE":"510150","TOT_VOL":"1"}],"total":3000}}""", D0930));

    [Fact]
    public void 解析_返回别的日子要抛()
        => Assert.Throws<InvalidOperationException>(() => SseEtfShareProvider.Parse(
            """{"pageHelp":{"data":[{"STAT_DATE":"2024-09-27","SEC_CODE":"510150","TOT_VOL":"1"}],"total":1}}""", D0930));

    // ─────────────────── helpers ───────────────────

    private EtfTurnoverRecalcTask NewTask(IEtfShareProvider provider) =>
        new(_shares, provider, _days, _noData, new SqliteEtfTurnoverStore(_dbPath));

    /// <summary>前复权、不复权、回测序列三个口径各一行，volume/amount/OHLC 相同，只有换手率按参数给。</summary>
    private void SaveEtfBars(DateOnly day, double volume, double dayTurnover, double rawTurnover)
    {
        foreach (var (g, t) in new[] { (Granularity.Day, dayTurnover), (Granularity.DayRaw, rawTurnover),
                                       (Granularity.DayAdj, rawTurnover) })
            _bars.InsertOrRefreshUnconfirmed(new[]
            {
                new Bar
                {
                    Code = "sh510150", Granularity = g, PeriodStart = day.ToDateTime(TimeOnly.MinValue),
                    Open = 0.602, Close = 0.589, High = 0.604, Low = 0.532,
                    Volume = volume, Amount = 956_041_900, Turnover = t,
                },
            });
    }

    private void ExecSql(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private double? ReadTurnover(string code, string gran, DateOnly day)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT turnover FROM Bar WHERE code=$c AND granularity=$g AND substr(period_start,1,10)=$d;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", gran);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToDouble(v);
    }

    private (double Open, double Volume, double Amount) ReadOthers(string code, string gran, DateOnly day)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT open, volume, amount FROM Bar WHERE code=$c AND granularity=$g AND substr(period_start,1,10)=$d;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", gran);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetDouble(0), r.GetDouble(1), r.GetDouble(2));
    }

    private sealed class FakeProvider : IEtfShareProvider
    {
        private readonly Dictionary<DateOnly, List<EtfShareRow>> _byDay = new();
        public List<DateOnly> Asked { get; } = [];

#pragma warning disable CS0067   // 假 provider 不播报状态
        public event Action<string>? OnStatus;
#pragma warning restore CS0067

        public FakeProvider With(DateOnly day, params (string Code, double Wan)[] rows)
        {
            _byDay[day] = rows.Select(r => new EtfShareRow(r.Code, day, r.Wan)).ToList();
            return this;
        }

        public Task<List<EtfShareRow>> GetDayAsync(DateOnly day, CancellationToken ct = default)
        {
            Asked.Add(day);
            return Task.FromResult(_byDay.TryGetValue(day, out var rows) ? rows : []);
        }
    }
}
