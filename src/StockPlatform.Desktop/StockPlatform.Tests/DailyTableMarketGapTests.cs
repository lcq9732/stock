using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// **市场缺失判据**（2026-09-16）：某个核心交易所这天一行都没有 → 残缺日。
///
/// 起因是 2026-08-21 / 2026-09-02 两融只抓到沪市、深市整天没有，而当时两条防线都够不着：
/// 行数判据的阈值是全表统一的 0.2，可那两天有 1,998/4,097 ≈ **49%** 的行；
/// 整段回补的 have 判据只看"这天有没有行"，1,998 行就算"已有"，于是永远补不回来。
///
/// ════ 为什么核心市场基线取**全区间**频率、不取滚动窗口 ════
/// 滚动窗口（跟"偏少日"的 trailing median 一个形状）看着更合理，但实测在事件型表上大量误报：
/// 龙虎榜 14 天、大宗交易 78 天，全都是"缺北交所"——北交所上榜/大宗本来就稀疏，
/// 连着十天有、偶尔一天没有是常态，滚动窗口会把它当成"每天都该有"。
/// 全区间频率下北交所够不到 90%、不算核心，这两张表就是 0 误报，而 MarginDetail 那两天照样报。
/// 对照数据见 doc/partial-day-repair-design.md §3.2。
///
/// ════ 这套为什么要 40 天日历 ════
/// 代价是**小样本会自我屏蔽**：判据要求某市场在 ≥90% 的日子里出现过才算核心，
/// 5 天的日历里缺 1 天就是 80%，深市直接不算核心、判据自己把自己关掉了。
/// 真实的表有几千天（MarginDetail 3,999 天缺 2 天＝99.95%），够不着这个坎；
/// 但测试得给足样本，所以单开一个类，不去动 <see cref="DailyTableAuditTests"/> 那份 5 天的日历。
/// （同样的道理：某个市场要是缺了超过 10% 的天数，这条判据会失灵——那种系统性缺失
/// 属于"这张表根本没在抓某个市场"，量级完全不同，靠行数判据和空日判据去兜。）
/// </summary>
public class DailyTableMarketGapTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDailyTableAuditor _auditor;

    private const string Anchor = MarketIndexCatalog.ShanghaiCompositeSymbol;

    private static readonly SqliteDailyTableAuditor.Spec Margin =
        SqliteDailyTableAuditor.DailyTables.First(t => t.Table == "MarginDetail");

    /// <summary>40 个连续工作日：缺 1 天＝97.5%，稳稳越过 90% 的核心市场门槛。</summary>
    private static readonly List<DateTime> Cal = BuildCalendar(40);

    private static readonly DateTime Cutoff = Cal[^1];

    /// <summary>深市整天缺失的那一天——放在中间，前后都有正常日子当基准。</summary>
    private static readonly DateTime GapDay = Cal[20];

    private static List<DateTime> BuildCalendar(int count)
    {
        var days = new List<DateTime>();
        var d = new DateTime(2026, 1, 5);       // 周一
        while (days.Count < count)
        {
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) days.Add(d);
            d = d.AddDays(1);
        }
        return days;
    }

    public DailyTableMarketGapTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mktgap_{Guid.NewGuid():N}.sqlite");
        var bars = new SqliteBarRepository(_dbPath);
        bars.EnsureSchema();
        // 成交额恒定：这一套测市场判据，别让清淡日豁免插进来搅局
        bars.InsertOrRefreshUnconfirmed(Cal.Select(day => new Bar
        {
            Code = Anchor, Granularity = Granularity.Day, PeriodStart = day,
            Open = 1, Close = 1, High = 1, Low = 1, Volume = 1, Amount = 1000,
        }));
        _auditor = new SqliteDailyTableAuditor(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* 临时文件 */ }
    }

    /// <summary><paramref name="prefix"/>："60"＝沪市、"00"＝深市、"92"＝北交所。</summary>
    private void Insert(DateTime day, int rows, string prefix)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < rows; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR IGNORE INTO MarginDetail (trade_date, code, margin_balance) VALUES ($d, $c, 1);";
            cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$c", $"{prefix}{i:D4}");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>复刻 2026-08-21 的形态：沪市照常、深市整天没有，总行数正好掉一半。</summary>
    private void SeedWithGap()
    {
        foreach (var d in Cal)
        {
            Insert(d, 100, "60");
            if (d != GapDay) Insert(d, 100, "00");
        }
    }

    [Fact]
    public void 某个市场整天没数据_报残缺日()
    {
        SeedWithGap();

        var r = _auditor.Check(Margin, Anchor, Cutoff)!;

        var p = Assert.Single(r.PartialDays);
        Assert.Equal(GapDay, p.Day);
        Assert.True(p.Reason.HasFlag(SqliteDailyTableAuditor.PartialReason.MissingMarket));
        Assert.Equal([Exchange.Shenzhen], p.MissingMarkets);
        Assert.Empty(r.EmptyDays);      // 那天有 100 行，不是空日
    }

    /// <summary>真实那两天是 1,998/4,097≈49%——旧阈值 0.2 检不出，新阈值 0.7 和市场判据都该命中。</summary>
    [Fact]
    public void 缺一半行且缺市场_两条判据都命中()
    {
        SeedWithGap();

        var p = Assert.Single(_auditor.Check(Margin, Anchor, Cutoff)!.PartialDays);

        Assert.True(p.Reason.HasFlag(SqliteDailyTableAuditor.PartialReason.ThinRows));
        Assert.True(p.Reason.HasFlag(SqliteDailyTableAuditor.PartialReason.MissingMarket));
        Assert.Equal(100, p.Rows);      // 只剩沪市那 100 行
        Assert.Equal(200, p.Nearby);
    }

    /// <summary>旧阈值 0.2 对这种"正好掉一半"完全够不着——这是当初漏掉那两天的直接原因。</summary>
    [Fact]
    public void 旧阈值下行数判据够不着_所以市场判据是必需的()
    {
        SeedWithGap();

        var oldRatio = Margin with { ThinRatio = 0.2, CheckMarkets = false };
        Assert.Empty(_auditor.Check(oldRatio, Anchor, Cutoff)!.PartialDays);

        var newRatio = Margin with { ThinRatio = 0.7, CheckMarkets = false };
        Assert.Single(_auditor.Check(newRatio, Anchor, Cutoff)!.PartialDays);
    }

    /// <summary>
    /// **这条守的是整个功能的命门**：复查不能用 COUNT>0。
    ///
    /// 残缺日本来就有行，拿"有没有行"去复查，深市补没补上都会被判成"已补齐"、
    /// 从待办里静默划掉——那正是本次要修的 bug 换个地方重演（见 RetryTodoKind.PartialDay）。
    /// </summary>
    [Fact]
    public void 复查时那天仍缺市场_不算补齐()
    {
        SeedWithGap();

        var still = _auditor.CheckDays(Margin, Anchor, [GapDay]);

        var p = Assert.Single(still);   // 有 100 行，但深市还缺着 → 不能划掉
        Assert.Equal(GapDay, p.Day);
        Assert.Equal([Exchange.Shenzhen], p.MissingMarkets);
    }

    [Fact]
    public void 复查时已经补齐_返回空()
    {
        foreach (var d in Cal)
        {
            Insert(d, 100, "60");
            Insert(d, 100, "00");       // 深市那天也补上了
        }

        Assert.Empty(_auditor.CheckDays(Margin, Anchor, [GapDay]));
    }

    /// <summary>核心市场基线从数据自己算，不写死沪深——北交所是后来才有的，
    /// 写死会让 2021 年以前的数据全部误报。</summary>
    [Fact]
    public void 这张表从来没有过北交所_不因为缺它而报()
    {
        foreach (var d in Cal)
        {
            Insert(d, 100, "60");
            Insert(d, 100, "00");
        }

        Assert.Empty(_auditor.Check(Margin, Anchor, Cutoff)!.PartialDays);
    }

    /// <summary>
    /// 稀疏出现的市场不算核心——这正是事件型表不能用滚动窗口基线的原因：
    /// 北交所大宗/上榜本来就是偶发的，把它当成"每天都该有"会误报几十天。
    /// </summary>
    [Fact]
    public void 只在个别日子出现的市场_不算核心市场()
    {
        foreach (var d in Cal)
        {
            Insert(d, 100, "60");
            Insert(d, 100, "00");
        }
        // 北交所只在头 5 天出现（5/40 = 12.5%，够不到 90%）
        foreach (var d in Cal.Take(5)) Insert(d, 5, "92");

        Assert.Empty(_auditor.Check(Margin, Anchor, Cutoff)!.PartialDays);
    }
}
