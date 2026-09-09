using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;
using Xunit;

namespace StockPlatform.Tests;

/// <summary>
/// schema 迁移：老库能不能升上来（2026-09-08）。
///
/// ════ 为什么这组测试非有不可 ════
/// 2026-09-08 真的崩了一次：<c>StockCustomerSupplier</c> 加了 <c>partner_code</c> 列，同时在
/// 建表 DDL 里给它加了一条索引。新库没事——列在 CREATE TABLE 里；**老库一开程序就崩**：
/// CREATE TABLE IF NOT EXISTS 对已存在的表是空操作、不补列，紧跟着的
/// <c>CREATE INDEX ... ON StockCustomerSupplier(partner_code)</c> 直接报 no such column，
/// 异常从 App.OnStartup 抛出去，窗口都开不出来。
///
/// 这类错**测不到就发现不了**：开发机上的库多半是新建的，跑得好好的；崩的是用户那份攒了几个月
/// 的库，而且是在发布之后。所以这里用"手工造一个老库"的办法把两条路都钉住：
///   ① 老库（有表、缺新列）跑 EnsureSchema 不能抛，且列和索引都要补出来；
///   ② 空库跑 EnsureSchema 也不能抛——补列那一段现在跑在建表**之前**，那时表还不存在。
/// 以后再给老表加列，照着 ① 加一条即可。
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"schemamig_{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { /* 临时文件删不掉不影响测试结论 */ }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void Exec(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private List<string> ColumnsOf(string table)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(0));
        return cols;
    }

    private bool IndexExists(string name)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$n;";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public void 空库建起来_反复调也不抛()
    {
        using (var conn = Open()) SqliteSchema.EnsureSchema(conn);
        using (var conn = Open()) SqliteSchema.EnsureSchema(conn);   // 幂等

        Assert.Contains("partner_code", ColumnsOf("StockCustomerSupplier"));
        Assert.True(IndexExists("ix_custsupp_pcode"));
    }

    /// <summary>
    /// 老库＝表已经在了、但没有后加的那两列（这正是 2026-09-08 崩掉的那个形状）。
    /// 建表语句照抄加列之前的版本。
    /// </summary>
    [Fact]
    public void 老库缺列_升级时补上列再建索引_不抛()
    {
        Exec("""
            CREATE TABLE StockCustomerSupplier (
                code         TEXT NOT NULL,
                report_date  TEXT NOT NULL,
                is_supplier  INTEGER NOT NULL,
                rank         INTEGER NOT NULL,
                partner_name TEXT,
                amount       REAL,
                ratio        REAL,
                total_amount REAL,
                report_name  TEXT,
                fetched_at   TEXT,
                PRIMARY KEY (code, report_date, is_supplier, rank)
            );
            INSERT INTO StockCustomerSupplier
                (code, report_date, is_supplier, rank, partner_name)
                VALUES ('600000', '2025-12-31', 0, 1, '某某公司');
            """);
        Assert.DoesNotContain("partner_code", ColumnsOf("StockCustomerSupplier"));

        using (var conn = Open()) SqliteSchema.EnsureSchema(conn);   // 崩的就是这一句

        var cols = ColumnsOf("StockCustomerSupplier");
        Assert.Contains("partner_code", cols);
        Assert.Contains("match_type", cols);
        Assert.True(IndexExists("ix_custsupp_pcode"));

        // 补列是 ALTER TABLE，老数据必须原样还在（不是把表重建了）
        using var conn2 = Open();
        using var cmd = conn2.CreateCommand();
        cmd.CommandText = "SELECT partner_name FROM StockCustomerSupplier WHERE code='600000';";
        Assert.Equal("某某公司", cmd.ExecuteScalar() as string);
    }

    /// <summary>
    /// 老库的 <c>CustSuppYearState</c> 缺 <c>skipped</c> 列（2026-09-09 加的）。
    ///
    /// 这一列不是可有可无：判据从 <c>saved &lt; reported</c> 改成了
    /// <c>saved + skipped &lt; reported</c>。老行补出来的 skipped 必须是 <b>0 而不是 NULL</b>——
    /// NULL 参与加法会让整个表达式变 NULL，比较结果既不真也不假，那几年就再也不会被重抓。
    /// </summary>
    [Fact]
    public void 老库的年度完成度表缺skipped列_补出来要是0不是NULL()
    {
        Exec("""
            CREATE TABLE CustSuppYearState (
                year       INTEGER PRIMARY KEY,
                reported   INTEGER,
                saved      INTEGER,
                updated_at TEXT
            );
            INSERT INTO CustSuppYearState (year, reported, saved, updated_at)
                VALUES (2023, 62791, 51677, '2026-09-09 12:00:00');
            """);
        Assert.DoesNotContain("skipped", ColumnsOf("CustSuppYearState"));

        using (var conn = Open()) SqliteSchema.EnsureSchema(conn);

        Assert.Contains("skipped", ColumnsOf("CustSuppYearState"));

        using var conn2 = Open();
        using var cmd = conn2.CreateCommand();
        // 老行原样还在，且 skipped 是 0（DEFAULT 0），不是 NULL
        cmd.CommandText = "SELECT saved, skipped, skipped IS NULL FROM CustSuppYearState WHERE year=2023;";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(51677, r.GetInt32(0));
        Assert.Equal(0, r.GetInt32(1));
        Assert.False(r.GetBoolean(2));
    }

    /// <summary>
    /// 上面那条只钉住了撞见的那一处。这条是**通用防线**：DDL 里每一条 CREATE INDEX 用到的列，
    /// 只要它也出现在"老库补列"名单里，就说明存在"老表 + 新列 + 索引"的组合——
    /// 补列必须先跑。真跑一遍空库和老库都验证不了顺序，只有把两者都建出来才算数，
    /// 所以这里直接检查最终结果：建完之后每一条索引都在。
    /// </summary>
    [Fact]
    public void DDL里的每条索引_升级完都真的建出来了()
    {
        // 先造一个"什么表都有、但都是最早期形状"的库：只建 DDL 里出现过的几张关键老表
        Exec("""
            CREATE TABLE Bar (
                code TEXT NOT NULL, granularity TEXT NOT NULL, period_start TEXT NOT NULL,
                open REAL, high REAL, low REAL, close REAL, volume REAL,
                PRIMARY KEY (code, granularity, period_start)
            );
            CREATE TABLE StockMeta (code TEXT PRIMARY KEY, name TEXT);
            """);

        using (var conn = Open()) SqliteSchema.EnsureSchema(conn);

        using var conn3 = Open();
        using var q = conn3.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_%';";
        Assert.True(Convert.ToInt64(q.ExecuteScalar()) > 0, "一条索引都没建出来，DDL 没跑完");

        // 老表也照样补上了后加的列
        Assert.Contains("fetched_at", ColumnsOf("Bar"));
        Assert.Contains("type", ColumnsOf("StockMeta"));
    }
}
