using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// 「其他数据」里十大流通股东的分组/较上期，以及点股东名反查持仓（2026-11-16）。
/// 逻辑全在 SQL + 逐期比对里，所以跟 DossierCustomerSupplierTests 一样打在真的临时 SQLite 上。
///
/// 造的数据：股东「甲合伙」
///   · 300001：三期都在（2026-03 进榜 → 06 增持 → 09 不变），最新仍在前十，占 8%
///   · 300002：06 期在、09 期掉出前十（09 期名单里没有他）——已退出
///   · 600003：最新一期在，占 12%，且第一期就在（不能标"新进"）；另有一条增减持公告
/// 同名自然人「刘洋」只为验证 IsNaturalPerson。
/// </summary>
public class HolderPortfolioTests : IDisposable
{
    private readonly string _db;
    private const string Holder = "甲合伙(有限合伙)";

    public HolderPortfolioTests()
    {
        _db = Path.Combine(Path.GetTempPath(), $"holder-pf-{Guid.NewGuid():N}.sqlite");
        using var conn = new SqliteConnection($"Data Source={_db}");
        conn.Open();
        // 用产品 schema 建表——顺带验证新索引的 DDL 本身能跑
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StockMeta (code, name) VALUES ('300001','一号股份'), ('300002','二号科技'), ('600003','三号银行');

            INSERT INTO TopShareholder (code, report_date, kind, rank, holder_name, shares, ratio, share_type) VALUES
              ('300001','2026-03-31','float',2,'甲合伙(有限合伙)',1000,7.0,'境内法人股'),
              ('300001','2026-03-31','float',1,'乙公司',5000,30.0,'境内法人股'),
              ('300001','2026-03-31','float',3,'丙',800,5.0,'自然人股'),
              ('300001','2026-06-30','float',1,'乙公司',5000,30.0,'境内法人股'),
              ('300001','2026-06-30','float',2,'甲合伙(有限合伙)',1200,8.0,'境内法人股'),
              ('300001','2026-06-30','float',3,'丁',700,4.0,'自然人股'),
              ('300001','2026-09-30','float',1,'乙公司',4800,29.0,'境内法人股'),
              ('300001','2026-09-30','float',2,'甲合伙(有限合伙)',1200,8.0,'境内法人股'),
              ('300001','2026-09-30','float',3,'丁',700,4.0,'自然人股'),

              ('300002','2026-03-31','float',1,'戊',100,10.0,'境内法人股'),
              ('300002','2026-06-30','float',1,'甲合伙(有限合伙)',300,3.0,'境内法人股'),
              ('300002','2026-09-30','float',1,'戊',100,10.0,'境内法人股'),

              ('600003','2026-06-30','float',1,'甲合伙(有限合伙)',9000,12.0,'境内法人股'),
              ('600003','2026-09-30','float',1,'甲合伙(有限合伙)',9000,12.0,'境内法人股'),
              ('600003','2026-09-30','total',1,'甲合伙(有限合伙)',9000,10.0,'境内法人股'),

              ('300002','2026-09-30','float',2,'刘洋',50,0.5,'自然人股');

            INSERT INTO HolderChange (code, notice_date, holder_name, end_date, name, direction, change_shares,
                                      after_shares, after_ratio, start_date, average_price) VALUES
              ('600003','2026-08-05','甲合伙(有限合伙)','2026-08-01','三号银行','减持',-500,8500,9.5,'2026-07-20',6.78),
              ('699999','2026-08-05','甲合伙(有限合伙)','2026-08-01','无关','增持',100,100,0.1,NULL,NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { /* 临时文件 */ }
    }

    [Fact]
    public void Positions_CurrentFirstByRatioDesc_ThenExited()
    {
        var pf = new SqliteHolderPortfolioReader(_db).Read(Holder, TopShareholderRow.KindFloat);

        Assert.False(pf.IsNaturalPerson);
        Assert.Equal(new[] { "600003", "300001", "300002" }, pf.Positions.Select(p => p.Code));
        Assert.False(pf.Positions[0].Exited);
        Assert.Equal("三号银行", pf.Positions[0].Name);
        Assert.Equal(12.0, pf.Positions[0].Ratio);

        var exited = pf.Positions[2];
        Assert.True(exited.Exited);
        Assert.Equal("2026-06-30", exited.Period);
        Assert.Equal("2026-09-30", exited.StockLatestPeriod);
    }

    [Fact]
    public void History_SnapshotChangesAndExit_DateDescending()
    {
        var pf = new SqliteHolderPortfolioReader(_db).Read(Holder, TopShareholderRow.KindFloat);

        var h1 = pf.Positions.Single(p => p.Code == "300001").History;
        Assert.Equal(new[] { "2026-09-30", "2026-06-30", "2026-03-31" }, h1.Select(h => h.Date));
        Assert.Equal(new[] { "不变", "增持", "" }, h1.Select(h => h.Action));   // 第一期不知道之前，不标新进
        Assert.Equal("+200", h1[1].Change);

        var h2 = pf.Positions.Single(p => p.Code == "300002").History;
        Assert.Equal(new[] { ("2026-09-30", "退出前十"), ("2026-06-30", "新进") }, h2.Select(h => (h.Date, h.Action)));
    }

    [Fact]
    public void History_IncludesHolderChange_OnlyForListedCodes()
    {
        var pf = new SqliteHolderPortfolioReader(_db).Read(Holder, TopShareholderRow.KindFloat);

        Assert.DoesNotContain(pf.Positions, p => p.Code == "699999");
        var h3 = pf.Positions.Single(p => p.Code == "600003").History;
        Assert.Equal(new[] { "2026-09-30", "2026-08-01", "2026-06-30" }, h3.Select(h => h.Date));
        var notice = h3[1];
        Assert.Equal("增减持公告", notice.Source);
        Assert.Equal("减持", notice.Action);
        Assert.Equal("-500", notice.Change);
        Assert.Equal("6.78", notice.Price);
    }

    [Fact]
    public void KindTotal_ReadsOnlyTotalRows()
    {
        var pf = new SqliteHolderPortfolioReader(_db).Read(Holder, TopShareholderRow.KindTotal);
        Assert.Equal(new[] { "600003" }, pf.Positions.Select(p => p.Code));
        Assert.Equal(10.0, pf.Positions[0].Ratio);
    }

    [Fact]
    public void NaturalPerson_Flagged()
    {
        var pf = new SqliteHolderPortfolioReader(_db).Read("刘洋", TopShareholderRow.KindFloat);
        Assert.True(pf.IsNaturalPerson);
    }

    [Fact]
    public void Dossier_FloatHoldersFirst_GroupedWithChangeColumn()
    {
        var dossier = new SqliteStockDossierReader(_db).Read("300001");
        var s = dossier.Sections[0];
        Assert.Equal("十大流通股东", s.Title);
        Assert.Null(s.Error);
        Assert.Equal(0, s.GroupColumn);
        Assert.Equal(2, s.LinkColumn);
        Assert.Equal(TopShareholderRow.KindFloat, s.LinkTag);

        // 最新一期在前、组内按名次
        Assert.Equal(new[] { "2026-09-30", "2026-09-30", "2026-09-30" }, s.Rows.Take(3).Select(r => r[0]));
        var change = s.Rows.Select(r => (r[0], r[2], r[5])).ToList();
        Assert.Contains(("2026-09-30", "乙公司", "减 200"), change);
        Assert.Contains(("2026-09-30", Holder, "不变"), change);
        Assert.Contains(("2026-06-30", "丁", "新进"), change);
        Assert.Contains(("2026-03-31", "乙公司", ""), change);

        Assert.Contains("新进 1 / 退出 1", s.GroupHeaders["2026-06-30"]);
        Assert.Contains("退出：丙", s.GroupHeaders["2026-06-30"]);
        Assert.Contains("前十合计 42.00%", s.GroupHeaders["2026-03-31"]);
    }
}
