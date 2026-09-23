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
///   ⑦ 深市：深交所按月一个请求、导出单位是份（换算成万份）、Bar 代码是 sz + 代码
///   ⑧ 最近 3 天的份额每轮都重抓（深交所 T 日晚间的值只是参考）
///
/// 全部打在临时 SQLite 上（假仓储验不到真 SQL），一行都不碰 current.sqlite。
/// </summary>
public class EtfTurnoverRecalcTests : IDisposable
{
    private static readonly DateOnly D0927 = new(2024, 9, 27);
    private static readonly DateOnly D0930 = new(2024, 9, 30);
    private static readonly DateOnly D1008 = new(2024, 10, 8);

    /// <summary>
    /// 深交所「基金规模·ETF」2024-09-30 的真实 xlsx 导出，裁成表头 + 3 行（159001 / 159915 / 159919），
    /// 原样保留 inlineStr 单元格和 dimension="A1"——openpyxl 就栽在这上面，所以要拿真文件测。
    /// </summary>
    private const string SzseSampleXlsxBase64 = "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";

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

        Assert.Equal(3, _shares.GetDays("sh").Count);
        var r = Sh(task);
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

        Assert.Equal(2, Sh(task).Written);   // 不复权 + 回测序列
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

        Assert.Equal(3, Sh(task).Missing);   // 09-30 三个口径；10-08 没成交不算
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

        Assert.Equal(3, Sh(task).UnjudgeableBeforeFirstDay + Sh(task).UnjudgeableOther);
        Assert.Equal(0, Sh(task).Written);
        Assert.Equal(0, ReadTurnover("sh510150", Granularity.DayRaw, D0927));
    }

    [Fact]
    public async Task 已有份额的日子不再请求()
    {
        _shares.Upsert([new EtfShareRow("sh", "510150", D0927, 202_317.86)]);
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
    public async Task 份额源开始有数据以前的日子一个请求都不发()
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

        Assert.Equal(["sh511620"], Sh(task).NotInExchangeList);
    }

    [Fact]
    public async Task 深市_按月请求_Bar代码是sz加代码_补上空值()
    {
        // 159915 创业板ETF：2024-09-30 份额 4,256,845.4936 万份（深交所导出 42,568,454,936 份）
        SaveEtfBars(D1008, volume: 5_000_000, dayTurnover: 0, rawTurnover: 0, code: "sz159915");
        var sz = new FakeProvider("sz", new DateOnly(2016, 9, 26), EtfShareBatch.Month)
            .With(D0927, ("159915", 4_200_000))
            .With(D0930, ("159915", 4_256_845.4936));
        var task = NewTask(new FakeProvider(), sz);

        await task.RunAsync(new TaskRunArgs(FetchMode.Thorough), CancellationToken.None);

        // 三天在 2024-09 / 10 两个月里：两个请求，而不是三个
        Assert.Equal([(D0927, D0930), (D1008, D1008)], sz.Requests);
        Assert.Equal(3, task.LastResult!["sz"].Missing);
        Assert.Equal(Math.Round(5_000_000 / 4_256_845.4936, 2),
                     ReadTurnover("sz159915", Granularity.DayRaw, D1008));
    }

    [Fact]
    public void 请求规划_按月合并_最近三天有了也重抓()
    {
        var today = new DateOnly(2026, 9, 23);
        var cal = new[] { new DateOnly(2026, 8, 28), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 1),
                          new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23) };
        var have = cal.ToHashSet();   // 全都有了
        var plan = EtfShareFetchPlan.Build(cal, have, new HashSet<DateOnly>(), new DateOnly(2016, 9, 26),
                                           today, EtfShareBatch.Month);

        // 只有最近 3 天（09-20 之后）要重抓，合成一个 9 月的请求
        var req = Assert.Single(plan);
        Assert.Equal([new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 23)], req.Days);
    }

    [Fact]
    public void 请求规划_按天_确认没有的不再问()
    {
        var today = new DateOnly(2026, 9, 23);
        var cal = new[] { D0927, D0930, D1008 };
        var plan = EtfShareFetchPlan.Build(cal, new HashSet<DateOnly>(), new HashSet<DateOnly> { D0930 },
                                           new DateOnly(2012, 1, 4), today, EtfShareBatch.Day);
        Assert.Equal([D0927, D1008], plan.Select(p => p.From));
    }

    [Fact]
    public void 解析深交所_真实导出样本_单位份换算成万份()
    {
        var rows = SzseEtfShareProvider.Parse(Convert.FromBase64String(SzseSampleXlsxBase64), D0930, D0930);
        Assert.Equal(3, rows.Count);
        var cyb = rows.Single(r => r.Code == "159915");
        Assert.Equal("sz", cyb.Market);
        Assert.Equal("sz159915", cyb.BarCode);
        Assert.Equal(4_256_845.4936, cyb.SharesWan, 6);
        Assert.Contains(rows, r => r.Code == "159001");   // 货币 ETF 深交所是列的
    }

    [Fact]
    public void 解析深交所_返回区间外的日子要抛()
        => Assert.Throws<InvalidOperationException>(() => SzseEtfShareProvider.Parse(
            Convert.FromBase64String(SzseSampleXlsxBase64), D1008, D1008));

    [Fact]
    public void 解析深交所_不是xlsx要抛_不能当成空()
        => Assert.Throws<RateLimitedException>(() => SzseEtfShareProvider.Parse(
            System.Text.Encoding.UTF8.GetBytes("<html>请稍后再试</html>"), D0930, D0930));

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
        Assert.Equal(new EtfShareRow("sh", "510150", D0930, 349_717.86), rows[0]);
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

    private EtfTurnoverRecalcTask NewTask(params IEtfShareProvider[] providers) =>
        new(_shares, providers, _days, _noData, new SqliteEtfTurnoverStore(_dbPath));

    private static EtfTurnoverAuditResult Sh(EtfTurnoverRecalcTask task) => task.LastResult!["sh"];

    /// <summary>前复权、不复权、回测序列三个口径各一行，volume/amount/OHLC 相同，只有换手率按参数给。</summary>
    private void SaveEtfBars(DateOnly day, double volume, double dayTurnover, double rawTurnover,
                             string code = "sh510150")
    {
        foreach (var (g, t) in new[] { (Granularity.Day, dayTurnover), (Granularity.DayRaw, rawTurnover),
                                       (Granularity.DayAdj, rawTurnover) })
            _bars.InsertOrRefreshUnconfirmed(new[]
            {
                new Bar
                {
                    Code = code, Granularity = g, PeriodStart = day.ToDateTime(TimeOnly.MinValue),
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

    private sealed class FakeProvider(string market = "sh", DateOnly? firstDay = null,
                                      EtfShareBatch batch = EtfShareBatch.Day) : IEtfShareProvider
    {
        private readonly Dictionary<DateOnly, List<EtfShareRow>> _byDay = new();
        public List<DateOnly> Asked { get; } = [];
        public List<(DateOnly, DateOnly)> Requests { get; } = [];
        public string Market => market;
        public DateOnly FirstDay => firstDay ?? new DateOnly(2012, 1, 4);
        public EtfShareBatch Batch => batch;

#pragma warning disable CS0067   // 假 provider 不播报状态
        public event Action<string>? OnStatus;
#pragma warning restore CS0067

        public FakeProvider With(DateOnly day, params (string Code, double Wan)[] rows)
        {
            _byDay[day] = rows.Select(r => new EtfShareRow(market, r.Code, day, r.Wan)).ToList();
            return this;
        }

        public Task<List<EtfShareRow>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            Requests.Add((from, to));
            var rows = new List<EtfShareRow>();
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                Asked.Add(d);
                if (_byDay.TryGetValue(d, out var r)) rows.AddRange(r);
            }
            return Task.FromResult(rows);
        }
    }
}
