using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 入库单位统一第二批（2026-10-20，见 doc/unit-normalization-inventory.md）的存量迁移（真 SQLite）：
/// Lhb.amount 万元→元、LhbSeat 比例和 BlockTrade.premium_ratio 小数→百分数、
/// EtfShare.shares_wan（万份）改名 shares 存份、删 CompanyProfile.reg_capital_wan。
/// 每张表一条迁移、一个事务。
/// </summary>
public class UnitNormalizationBatch2Tests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"unit2_{Guid.NewGuid():N}.sqlite");

    private static readonly string[] Batch2 =
    [
        SqliteDataMigrations.UnitBatch2Lhb, SqliteDataMigrations.UnitBatch2LhbSeat, SqliteDataMigrations.UnitBatch2BlockTrade,
        SqliteDataMigrations.UnitBatch2EtfShare, SqliteDataMigrations.UnitBatch2Profile,
    ];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath) + "*"))
            try { File.Delete(f); } catch { /* 临时文件 */ }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        SqliteSchema.EnsureSchema(c);
    }

    private void Exec(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private double? One(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToDouble(v);
    }

    private List<string> Columns(string table)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>把新建的库退回"第二批上线之前"的样子：去掉迁移记录、EtfShare 用老列名、CompanyProfile 带万元列。</summary>
    private void MakeLegacy()
    {
        EnsureSchema();
        Exec($"DELETE FROM DataMigration WHERE id IN ({string.Join(",", Batch2.Select(id => $"'{id}'"))});");
        Exec("""
            DROP TABLE EtfShare;
            CREATE TABLE EtfShare (
                market TEXT NOT NULL DEFAULT 'sh', code TEXT NOT NULL, trade_date TEXT NOT NULL,
                shares_wan REAL NOT NULL, fetched_at TEXT, PRIMARY KEY (code, trade_date));
            ALTER TABLE CompanyProfile ADD COLUMN reg_capital_wan REAL;
            """);
    }

    [Fact]
    public void 老库启动时换一次_再启动不再换()
    {
        MakeLegacy();
        Exec("""
            INSERT INTO Lhb (trade_date, stock_code, reason, amount, volume)
            VALUES ('2026-09-08', '000560', '日换手率达到20%的前5只证券', 217921.248, NULL);
            INSERT INTO LhbSeat (trade_date, code, is_buy, seat_code, explanation, seq, buy_ratio, sell_ratio)
            VALUES ('2026-09-04', '600127', 1, '10001', '某原因', 1, 0.01027924072, NULL),
                   ('2026-09-04', '600127', 0, '10002', '某原因', 1, NULL, 0.010923685302);
            INSERT INTO BlockTrade (trade_date, code, daily_rank, premium_ratio, deal_price, close_price)
            VALUES ('2020-06-10', '000338', 1, 0.075, 15.05, 14.0),
                   ('2026-09-04', '000007', 1, -0.000805152979, 12.41, 12.42);
            INSERT INTO EtfShare (market, code, trade_date, shares_wan)
            VALUES ('sh', '510150', '2024-09-27', 202317.86), ('sz', '159915', '2024-09-30', 4256845.4936);
            INSERT INTO CompanyProfile (code, reg_capital, reg_capital_wan) VALUES ('300750', 4626770410, 462677.041);
            """);

        for (int run = 0; run < 2; run++)          // 第二次启动必须什么都不改
        {
            EnsureSchema();

            Assert.Equal(2_179_212_480d, One("SELECT amount FROM Lhb"));
            Assert.Null(One("SELECT volume FROM Lhb"));

            Assert.Equal(1.027924072, One("SELECT buy_ratio FROM LhbSeat WHERE is_buy=1"));
            Assert.Null(One("SELECT sell_ratio FROM LhbSeat WHERE is_buy=1"));
            Assert.Equal(1.0923685302, One("SELECT sell_ratio FROM LhbSeat WHERE is_buy=0"));

            Assert.Equal(7.5, One("SELECT premium_ratio FROM BlockTrade WHERE code='000338'"));
            Assert.Equal(-0.0805152979, One("SELECT premium_ratio FROM BlockTrade WHERE code='000007'"));
            Assert.Equal(15.05, One("SELECT deal_price FROM BlockTrade WHERE code='000338'"));          // 别的列不动

            Assert.Contains("shares", Columns("EtfShare"));
            Assert.DoesNotContain("shares_wan", Columns("EtfShare"));
            Assert.Equal(2_023_178_600d, One("SELECT shares FROM EtfShare WHERE code='510150'"));
            Assert.Equal(42_568_454_936d, One("SELECT shares FROM EtfShare WHERE code='159915'"));

            Assert.DoesNotContain("reg_capital_wan", Columns("CompanyProfile"));
            Assert.Equal(4_626_770_410d, One("SELECT reg_capital FROM CompanyProfile"));
        }

        using var c = Open();
        foreach (var id in Batch2) Assert.True(SqliteDataMigrations.IsApplied(c, id), id);
    }

    /// <summary>
    /// 换手率逐位不变：份额改存份、成交量改存份（Bar.volume 也在 2026-10-20 改存股/份）以后，
    /// "(量 ÷ 100) ÷ (份 ÷ 1e4)"跟原来的"手 ÷ 万份"必须逐位相同——
    /// 【ETF换手率校正】会把算出来的值写回库，差一位就会凭空重写一批行。
    /// 这里拿各种量级的份额（两位小数的万份、深市整数份）各算一遍。
    /// </summary>
    [Theory]
    [InlineData(16_919_316, 202_317.86)]
    [InlineData(5_000_000, 4_256_845.4936)]
    [InlineData(1, 0.54)]
    [InlineData(123_456_789, 12_186_430.74)]
    [InlineData(33_333, 3.33)]
    public void 换手率跟改单位前逐位相同(double volumeLots, double sharesWan)
    {
        double old = Math.Round(volumeLots / sharesWan, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(old, EtfTurnoverRule.Expected(UnitScale.Apply(volumeLots, 100m), UnitScale.Apply(sharesWan, UnitScale.Wan)));
    }

    /// <summary>新库直接建出 shares 列、没有 reg_capital_wan，迁移照样记账（改 0 行）。</summary>
    [Fact]
    public void 新库直接是新结构()
    {
        EnsureSchema();
        Assert.Contains("shares", Columns("EtfShare"));
        Assert.DoesNotContain("shares_wan", Columns("EtfShare"));
        Assert.DoesNotContain("reg_capital_wan", Columns("CompanyProfile"));
        using var c = Open();
        foreach (var id in Batch2) Assert.True(SqliteDataMigrations.IsApplied(c, id), id);
    }

    /// <summary>新写入的行走仓储，读回来是份。</summary>
    [Fact]
    public void 仓储读写份()
    {
        var repo = new SqliteEtfShareRepository(_dbPath);
        repo.EnsureSchema();
        repo.Upsert([new EtfShareRow("sh", "510150", new DateOnly(2024, 9, 27), 2_023_178_600d)]);
        Assert.Equal(2_023_178_600d, repo.GetByCode("sh", "510150")[new DateOnly(2024, 9, 27)]);
    }
}
