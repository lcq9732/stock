using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 入库单位统一第一批（2026-10-20，见 doc/unit-normalization-inventory.md）：
/// ShareLift / HolderChange / IpoIssue 的存量迁移（真 SQLite）和分析程序事件栏的显示文字。
///
/// 起因：事件栏把 301489 的限售解禁显示成"2969.61股，25.19万元，占流通 0.43%"，
/// 实际是 2969.61 万股、25.19 亿元、42.68%——库里存的是万股/万元/小数，读的人当成了股/元/百分数。
/// </summary>
public class UnitNormalizationBatch1Tests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"unit1_{Guid.NewGuid():N}.sqlite");

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

    /// <summary>
    /// 老库（迁移之前写进去的万股/万元/小数/亿元）→ 启动一次就换成股/元/百分数；再启动不会再乘一遍。
    /// 新库建表时直接记上"已迁移"（空表换了也是空），以后新抓的行本来就是新单位。
    /// </summary>
    [Fact]
    public void 老库启动时换一次_再启动不再换()
    {
        EnsureSchema();
        // 模拟"迁移上线前的老库"：去掉迁移记录，写入老单位的行
        Exec($"DELETE FROM DataMigration WHERE id = '{SqliteDataMigrations.UnitBatch1}';");
        Exec("""
            INSERT INTO ShareLift (code, free_date, share_type, lift_shares, lift_market_cap, free_ratio, total_ratio,
                                   pre_free_shares, non_free_shares, before20_change)
            VALUES ('000065', '2026-08-03', '定向增发机构配售股份', 9005.6285, 83031.89477, 0.084558417368, 0.077538329655,
                    106501.8573, 9642.3586, -4.81064483);
            INSERT INTO HolderChange (code, notice_date, holder_name, end_date, change_shares, change_ratio, after_shares, after_ratio)
            VALUES ('002203', '2026-09-05', '海亮集团有限公司', '2026-09-04', 1519.76, 0.663142359589, 42000.0, 28.04),
                   ('600000', '2026-09-05', '某股东', '2026-09-04', -12.5, NULL, NULL, NULL);
            INSERT INTO IpoIssue (code, is_beijing, issue_num, issue_price, dec_sumfina, netsumfina, online_va_shares, top_apply_marketcap)
            VALUES ('002051', 0, 6000, 7.4, 4.44, 4.227041, 2764748.5, NULL),
                   ('920229', 1, 1100, 15.67, 172370000, 148708374.56, 57544021500, 732.5725),
                   ('832317', NULL, 100, 5, 123456789, NULL, NULL, NULL);
            """);

        for (int run = 0; run < 2; run++)          // 第二次启动必须什么都不改
        {
            EnsureSchema();

            Assert.Equal(90_056_285d, One("SELECT lift_shares FROM ShareLift"));
            Assert.Equal(830_318_947.7, One("SELECT lift_market_cap FROM ShareLift"));
            Assert.Equal(8.4558417368, One("SELECT free_ratio FROM ShareLift"));
            Assert.Equal(7.7538329655, One("SELECT total_ratio FROM ShareLift"));
            Assert.Equal(1_065_018_573d, One("SELECT pre_free_shares FROM ShareLift"));
            Assert.Equal(96_423_586d, One("SELECT non_free_shares FROM ShareLift"));
            Assert.Equal(-4.81064483, One("SELECT before20_change FROM ShareLift"));     // 本来就是百分数：不动

            Assert.Equal(15_197_600d, One("SELECT change_shares FROM HolderChange WHERE code='002203'"));
            Assert.Equal(420_000_000d, One("SELECT after_shares FROM HolderChange WHERE code='002203'"));
            Assert.Equal(0.663142359589, One("SELECT change_ratio FROM HolderChange WHERE code='002203'"));   // 本来就是百分数
            Assert.Equal(28.04, One("SELECT after_ratio FROM HolderChange WHERE code='002203'"));
            Assert.Equal(-125_000d, One("SELECT change_shares FROM HolderChange WHERE code='600000'"));      // 减持带符号
            Assert.Null(One("SELECT after_shares FROM HolderChange WHERE code='600000'"));                  // 空的还是空

            Assert.Equal(60_000_000d, One("SELECT issue_num FROM IpoIssue WHERE code='002051'"));
            Assert.Equal(7.4, One("SELECT issue_price FROM IpoIssue WHERE code='002051'"));
            Assert.Equal(444_000_000d, One("SELECT dec_sumfina FROM IpoIssue WHERE code='002051'"));        // 沪深：亿元
            Assert.Equal(422_704_100d, One("SELECT netsumfina FROM IpoIssue WHERE code='002051'"));
            Assert.Equal(27_647_485_000d, One("SELECT online_va_shares FROM IpoIssue WHERE code='002051'")); // 沪深：万股

            Assert.Equal(11_000_000d, One("SELECT issue_num FROM IpoIssue WHERE code='920229'"));
            Assert.Equal(172_370_000d, One("SELECT dec_sumfina FROM IpoIssue WHERE code='920229'"));        // 北交所：元，不动
            Assert.Equal(148_708_374.56, One("SELECT netsumfina FROM IpoIssue WHERE code='920229'"));
            Assert.Equal(57_544_021_500d, One("SELECT online_va_shares FROM IpoIssue WHERE code='920229'")); // 北交所：股，不动
            Assert.Equal(7_325_725d, One("SELECT top_apply_marketcap FROM IpoIssue WHERE code='920229'"));

            Assert.Equal(123_456_789d, One("SELECT dec_sumfina FROM IpoIssue WHERE code='832317'"));        // 没标 is_beijing：按号段判北交所
            Assert.Equal(1_000_000d, One("SELECT issue_num FROM IpoIssue WHERE code='832317'"));
        }

        using var c = Open();
        Assert.True(SqliteDataMigrations.IsApplied(c, SqliteDataMigrations.UnitBatch1));
        Assert.Equal(6d, One($"SELECT rows FROM DataMigration WHERE id = '{SqliteDataMigrations.UnitBatch1}'"));
    }

    [Fact]
    public void 新库建表时就记上已迁移()
    {
        EnsureSchema();
        using var c = Open();
        Assert.True(SqliteDataMigrations.IsApplied(c, SqliteDataMigrations.UnitBatch1));
        Assert.Equal(0d, One($"SELECT rows FROM DataMigration WHERE id = '{SqliteDataMigrations.UnitBatch1}'"));
    }

    /// <summary>
    /// 迁移出来的值必须跟"新抓的值"逐位相同：都走 <see cref="UnitScale"/>。
    /// double 直接乘的话 4.44×1e8 会是 444000000.00000006，跟新抓的对不上。
    /// </summary>
    [Fact]
    public void 换算没有浮点尾巴()
    {
        Assert.Equal(444_000_000d, UnitScale.Apply(4.44, UnitScale.Yi));
        Assert.Equal(830_318_947.7, UnitScale.Apply(83031.89477, UnitScale.Wan));
        Assert.Equal(8.4558417368, UnitScale.Apply(0.084558417368, UnitScale.Percent));
        Assert.Null(UnitScale.Apply(null, UnitScale.Wan));
        Assert.Equal(1.5, UnitScale.Apply(1.5, 1m));
    }

    /// <summary>301489 那条就是起因：存成股/元/百分数以后，事件栏原样显示就对了。增减持不再自己拼"万股"。</summary>
    [Fact]
    public void 事件栏显示限售解禁和增减持()
    {
        EnsureSchema();
        var future = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
        var recent = DateTime.Today.AddDays(-5).ToString("yyyy-MM-dd");
        Exec($"""
            INSERT INTO ShareLift (code, free_date, share_type, lift_shares, lift_market_cap, free_ratio)
            VALUES ('301489', '{future}', '首发原股东限售股份', 29696100, 2519000000, 42.68);
            INSERT INTO HolderChange (code, notice_date, holder_name, end_date, direction, change_shares, change_ratio, after_ratio)
            VALUES ('301489', '{recent}', '某股东', '{recent}', '减持', -15197600, 0.66, 28.04);
            """);

        var events = new SqliteStockEventSource(_dbPath).Read("301489");
        var lift = Assert.Single(events, e => e.Category == WatchCategory.ShareLift);
        Assert.Contains("2969.61万股，25.19亿元，占流通 42.68%", lift.Text);
        var holder = Assert.Single(events, e => e.Category == WatchCategory.HolderChange);
        Assert.Contains("1519.76万股", holder.Text);
        Assert.Contains("占 0.66%", holder.Text);
    }
}
