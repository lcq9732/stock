using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 融券余额补算（2026-09-16）——见 <see cref="SqliteMarginShortBalanceFiller"/>。
///
/// 病根：上交所接口的 <c>rqylje</c>（融券余额）**恒为 null**，而解析用的 GetNum 把 null 读成 0，
/// 于是 1675 只沪市票的融券余额历史上从来没有过非 0 值，而界面上显示成"融券余额 0"
/// ——看起来像"这只票没人做空"。深市那张 xlsx 第 6 列直接给了余额，所以只有沪市这一半瞎。
///
/// 这里钉死四件最容易搞反的事：
///   ① **只动沪市**——深市 31% 的行 short_balance 真的是 0，改了就是造假数据
///   ② **ETF 要按带前缀的代码找收盘价**——ETF 的K线存成 sh510050，两融表里是裸码 510050
///   ③ **补不上的留 NULL 不写 0**——写 0 就永久变成"确实没有融券"，再也补不回来
///   ④ **幂等**——补过的行重跑不再被选中
///   ⑤ **取价走不复权 day_raw，不走前复权 day**——公式里的收盘价是当日真实成交价，
///      而 day 是减法式前复权，高分红老股早年会被减成负数（茅台 2012-05-02：day = −127.19，
///      day_raw = 225.98）；没有 day_raw 的标的（眼下是 ETF）才回退 day
///
/// 全部用临时库，一行都不碰 current.sqlite。
/// </summary>
public class MarginShortBalanceFillTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMarginRepository _margin;
    private readonly SqliteBarRepository _bars;

    public MarginShortBalanceFillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shortbal_{Guid.NewGuid():N}.sqlite");
        _bars = new SqliteBarRepository(_dbPath);
        _bars.EnsureSchema();
        _margin = new SqliteMarginRepository(_dbPath);
        _margin.EnsureSchema();
    }

    /// <summary>
    /// ⚠ **不要在这里调 <c>SqliteConnection.ClearAllPools()</c>**。
    ///
    /// 那是**进程级**的全局操作，会把连接池里所有连接（包括其它测试类此刻正在用的）
    /// 从底层关掉——xunit 默认并行跑不同测试类，于是 native 层去访问已释放的连接，
    /// **整个 testhost 崩溃**。表现是"Test host process crashed"，而且崩在第几个测试完全随机
    /// （2026-09-16 实测第 76 / 481 / 563 个各崩过一次），看起来特别像环境问题，
    /// 极难定位到这行。
    ///
    /// 临时文件删不掉就留着——反正在系统 temp 目录，文件名带 Guid 不会撞。
    /// 拿"可能留几个临时文件"换"测试套件不随机崩"，这个交易划算得很。
    /// </summary>
    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!,
                                             Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 还被连接池占着，留给系统清理 */ }
    }

    private static readonly DateTime Day = new(2026, 9, 14);

    /// <summary>
    /// 沪市个股：源头没给融券余额（null），按「余量 × 收盘价」补出来。
    /// 数字取自实测——贵州茅台 2026-09-14 余量 127,848 股、收盘 1277.96，
    /// 东财公布的融券余额 163,384,630 元，乘出来分毫不差。
    /// </summary>
    [Fact]
    public void 沪市个股_按余量乘收盘价补出融券余额()
    {
        SaveBar("600519", Day, 1277.96);
        SaveMargin("600519", Day, shortVolume: 127_848, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(1, r.Rows);
        Assert.Equal(127_848 * 1277.96, ReadShortBalance("600519", Day)!.Value, 2);
    }

    /// <summary>
    /// 历史行存的是 **0**（不是 null）——GetNum 把源头的 null 读成 0 留下的。
    /// 沪市的 0 全是假的（实测 3,139,554 行沪市数据里 short_balance>0 的有 0 行），
    /// 所以 0 也要当成"缺值"补上。
    /// </summary>
    [Fact]
    public void 沪市历史行的零也要补_那是null被读成的()
    {
        SaveBar("600036", Day, 41.83);
        SaveMargin("600036", Day, shortVolume: 9_771_516, shortBalance: 0);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(1, r.Rows);
        Assert.Equal(9_771_516 * 41.83, ReadShortBalance("600036", Day)!.Value, 2);
    }

    /// <summary>
    /// **深市一行都不许碰**。深市的 short_balance 是交易所直接给的真值，
    /// 而且 31% 的行本来就是 0（当天确实没有融券余量）——把"是不是 0"当判据会把真 0 改掉。
    /// </summary>
    [Fact]
    public void 深市不动_它的零是真的()
    {
        SaveBar("000001", Day, 11.85);
        SaveMargin("000001", Day, shortVolume: 8_118_837, shortBalance: 0);   // 深市，真 0
        SaveBar("300750", Day, 337.11);
        SaveMargin("300750", Day, shortVolume: 655_074, shortBalance: 220_831_996);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(0, r.Rows);
        Assert.Equal(0, ReadShortBalance("000001", Day));                      // 没被改成 9600 万
        Assert.Equal(220_831_996, ReadShortBalance("300750", Day)!.Value, 2);  // 原值不动
    }

    /// <summary>
    /// ETF 的K线在 <c>Bar</c> 里**带市场前缀**（sh510050），两融表里是裸码（510050）。
    /// 只按裸码找收盘价会让本来有数据的 ETF 也算成"补不上"。
    /// </summary>
    [Fact]
    public void ETF按带前缀的代码找收盘价()
    {
        SaveBar("sh510050", Day, 3.256);          // ETF 的K线是这个形式
        SaveMargin("510050", Day, shortVolume: 36_564_540, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(1, r.Rows);
        Assert.Equal(36_564_540 * 3.256, ReadShortBalance("510050", Day)!.Value, 2);
    }

    /// <summary>
    /// ⭐ **取价必须走 day_raw（不复权），不能走 day（前复权）。**
    ///
    /// <c>day</c> 是源给的**减法式**前复权（原价减去此后累计分红），高分红老股的早年价格会被
    /// 减成**负数**。这里的数字就是贵州茅台 2012-05-02 的实测值：<c>day</c> = −127.19、
    /// <c>day_raw</c> = 225.98（真实收盘约 232）。它 6005 根 <c>day</c> 里有 3529 根（59%）
    /// <c>close &lt;= 0</c>。
    ///
    /// 走错口径有两层后果，第二层更隐蔽：负价行被 <c>close &gt; 0</c> 挡掉，看着像"没有K线
    /// 补不上"；而**没被挡掉的那些也全是错的**——2020-06-01 的茅台会按 1160.24 算而不是
    /// 1419.50，低 18%，越往前错得越多。见 doc/missing-instruments-design.md §2。
    /// </summary>
    [Fact]
    public void 有day_raw时取价走不复权_不被前复权的负价带偏()
    {
        SaveBar("600519", Day, -127.19);                                    // 前复权：负价
        SaveBar("600519", Day, 225.98, Granularity.DayRaw);                 // 不复权：真实成交价
        SaveMargin("600519", Day, shortVolume: 100_000, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(1, r.Rows);
        Assert.Equal(100_000 * 225.98, ReadShortBalance("600519", Day)!.Value, 2);
    }

    /// <summary>
    /// 没有 <c>day_raw</c> 的标的**回退 <c>day</c>**，不是整个跳过。
    ///
    /// 眼下 ETF 一只都没有 <c>day_raw</c>（doc/etf-backtest-granularity-design.md 那套方案还没
    /// 实施），两融的 4637 个标的里只有 3836 个有。口径必须**逐标的**定：一刀切的话，要么个股
    /// 陪着 ETF 一起用错口径，要么 ETF 的融券余额整块补不出来。
    /// ETF 是纯分红的加法式失真（约 5% 偏低、不会为负），等 ETF 的 day_raw 补上后重跑即可变准。
    /// </summary>
    [Fact]
    public void 没有day_raw的标的回退前复权_ETF目前就是这种()
    {
        SaveBar("sh510050", Day, 3.256);                                    // 只有 day，没有 day_raw
        SaveMargin("510050", Day, shortVolume: 1_000_000, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(1, r.Rows);
        Assert.Equal(1_000_000 * 3.256, ReadShortBalance("510050", Day)!.Value, 2);
    }

    /// <summary>
    /// 没有当日收盘价 → **留 NULL，不写 0**。真正会走到这里的是**停牌日**（有融券余量但当天
    /// 不交易），加上 <c>Bar</c> 里一根都没有的 15 只标的（689009、12 只退市/换码股、2 只已清盘
    /// ETF，见 doc/missing-instruments-design.md）。写 0 就永久变成"确实没有融券"，再也补不回来。
    /// </summary>
    [Fact]
    public void 没有收盘价时留NULL不写零()
    {
        // 故意不存K线
        SaveMargin("600200", Day, shortVolume: 1_000_000, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(0, r.Rows);
        Assert.Equal(1, r.NoClose);
        Assert.Null(ReadShortBalance("600200", Day));   // 还是 NULL，不是 0
    }

    /// <summary>融券余量本来就是 0 的行不用补——余额确实该是 0，不是缺值。</summary>
    [Fact]
    public void 余量为零的行不补()
    {
        SaveBar("600030", Day, 26.35);
        SaveMargin("600030", Day, shortVolume: 0, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill();

        Assert.Equal(0, r.Rows);
        Assert.Equal(0, r.Days);     // 连这一天都不该被选进待补名单
    }

    /// <summary>幂等：补过一遍之后再跑是空转。中断重跑靠的就是这个，不用记断点。</summary>
    [Fact]
    public void 幂等_跑第二遍是空转()
    {
        SaveBar("600519", Day, 1277.96);
        SaveMargin("600519", Day, shortVolume: 127_848, shortBalance: null);
        var filler = new SqliteMarginShortBalanceFiller(_dbPath);

        var first = filler.Fill();
        var second = filler.Fill();

        Assert.Equal(1, first.Rows);
        Assert.Equal(0, second.Rows);
        Assert.Equal(0, second.Days);
    }

    /// <summary>sinceDate 只处理那天及以后——日更时不必每次扫全历史。</summary>
    [Fact]
    public void 指定起始日时只补那天及以后()
    {
        var older = new DateTime(2026, 9, 1);
        SaveBar("600519", older, 1300.00);
        SaveMargin("600519", older, shortVolume: 100_000, shortBalance: null);
        SaveBar("600519", Day, 1277.96);
        SaveMargin("600519", Day, shortVolume: 127_848, shortBalance: null);

        var r = new SqliteMarginShortBalanceFiller(_dbPath).Fill(sinceDate: Day);

        Assert.Equal(1, r.Rows);
        Assert.Null(ReadShortBalance("600519", older));       // 早于起始日，没动
        Assert.NotNull(ReadShortBalance("600519", Day));
    }

    // ─────────────────── 造数据 ───────────────────

    private void SaveBar(string code, DateTime day, double close,
                         string granularity = Granularity.Day) =>
        _bars.InsertOrRefreshUnconfirmed(new[]
        {
            new Bar
            {
                Code = code, Granularity = granularity, PeriodStart = day,
                Open = close, Close = close, High = close, Low = close,
                Volume = 1000, Amount = close * 1000, Turnover = 0.5,
            },
        });

    private void SaveMargin(string code, DateTime day, double shortVolume, double? shortBalance) =>
        _margin.InsertOrIgnore(new[]
        {
            new MarginDetailRow
            {
                Code = code, TradeDate = day, Name = code,
                MarginBalance = 1e8, MarginBuy = 1e7,
                ShortVolume = shortVolume, ShortBalance = shortBalance,
                FetchedAt = DateTime.Now,
            },
        });

    private double? ReadShortBalance(string code, DateTime day)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT short_balance FROM MarginDetail WHERE code=$c AND trade_date=$d;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd"));
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToDouble(v);
    }
}
