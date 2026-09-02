using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 定期报告预约披露日的本地存取（EarningsSchedule 表，2026-09-01 新增）。
/// 按 (code, report_period) 整条 upsert——预约日会变更，每次抓到的都是最新状态，直接覆盖。
/// </summary>
public class SqliteEarningsScheduleRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteEarningsScheduleRepository(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath}";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
    }

    private static string? Fmt(DateTime? d) => d?.ToString(DateFormat);
    private static DateTime? Parse(object? v)
        => v is string s && DateTime.TryParse(s, out var d) ? d : null;

    public int Upsert(IReadOnlyList<EarningsScheduleRow> rows)
    {
        if (rows.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO EarningsSchedule
                (code, report_period, appoint_date, change1, change2, change3, actual_date, fetched_at)
            VALUES ($c, $p, $a, $c1, $c2, $c3, $ac, $f)
            ON CONFLICT(code, report_period) DO UPDATE SET
                appoint_date = excluded.appoint_date,
                change1      = excluded.change1,
                change2      = excluded.change2,
                change3      = excluded.change3,
                actual_date  = excluded.actual_date,
                fetched_at   = excluded.fetched_at;
            """;
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pp = cmd.Parameters.Add("$p", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var p1 = cmd.Parameters.Add("$c1", SqliteType.Text);
        var p2 = cmd.Parameters.Add("$c2", SqliteType.Text);
        var p3 = cmd.Parameters.Add("$c3", SqliteType.Text);
        var pac = cmd.Parameters.Add("$ac", SqliteType.Text);
        var pf = cmd.Parameters.Add("$f", SqliteType.Text);
        string now = DateTime.Now.ToString(TimeFormat);

        int n = 0;
        foreach (var r in rows)
        {
            pc.Value = r.Code;
            pp.Value = r.ReportPeriod.ToString(DateFormat);
            pa.Value = (object?)Fmt(r.AppointDate) ?? DBNull.Value;
            p1.Value = (object?)Fmt(r.Change1) ?? DBNull.Value;
            p2.Value = (object?)Fmt(r.Change2) ?? DBNull.Value;
            p3.Value = (object?)Fmt(r.Change3) ?? DBNull.Value;
            pac.Value = (object?)Fmt(r.ActualDate) ?? DBNull.Value;
            pf.Value = now;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    private static EarningsScheduleRow Read(SqliteDataReader r) => new(
        r.GetString(0),
        DateTime.TryParse(r.GetString(1), out var p) ? p : default,
        Parse(r.IsDBNull(2) ? null : r.GetString(2)),
        Parse(r.IsDBNull(3) ? null : r.GetString(3)),
        Parse(r.IsDBNull(4) ? null : r.GetString(4)),
        Parse(r.IsDBNull(5) ? null : r.GetString(5)),
        Parse(r.IsDBNull(6) ? null : r.GetString(6)));

    private const string Cols =
        "code, report_period, appoint_date, change1, change2, change3, actual_date";

    /// <summary>某只股票的全部报告期记录，按报告期倒序。</summary>
    public List<EarningsScheduleRow> GetByCode(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM EarningsSchedule WHERE code=$c ORDER BY report_period DESC;";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        var list = new List<EarningsScheduleRow>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// 每只股票"下一次财报日"——取**还没实际披露**的报告期里最早的那个。
    /// 界面上的财报日列直接用它。已经全部披露完的股票不在返回里（那就是没有下一次可报）。
    /// </summary>
    public Dictionary<string, EarningsScheduleRow> GetUpcomingByCode()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM EarningsSchedule WHERE actual_date IS NULL ORDER BY report_period;";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, EarningsScheduleRow>(StringComparer.Ordinal);
        while (r.Read())
        {
            var row = Read(r);
            if (row.EffectiveDate is null) continue;
            // ORDER BY 保证先来的报告期更早，已有就不覆盖
            if (!map.ContainsKey(row.Code)) map[row.Code] = row;
        }
        return map;
    }

    /// <summary>某一个报告期的全部记录（复查时用来挑出还没披露的那些）。</summary>
    public List<EarningsScheduleRow> GetByPeriod(DateTime period)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM EarningsSchedule WHERE report_period=$p ORDER BY code;";
        cmd.Parameters.AddWithValue("$p", period.ToString(DateFormat));
        using var rd = cmd.ExecuteReader();
        var list = new List<EarningsScheduleRow>();
        while (rd.Read()) list.Add(Read(rd));
        return list;
    }

    /// <summary>本地已经存了哪些报告期（用来判断某一期的预约表是不是已经抓过）。</summary>
    public Dictionary<string, int> CountByPeriod()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT report_period, COUNT(*) FROM EarningsSchedule GROUP BY report_period;";
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>还没实际披露的记录数——就是"还要盯着看会不会改期"的那批。</summary>
    public int PendingCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EarningsSchedule WHERE actual_date IS NULL;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
