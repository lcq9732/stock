using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 年报子公司名单的本地存取（2026-09-11）。累积语义，**全类没有一句 DELETE**
/// （唯一的例外是重解析同一份 PDF 时先清掉它自己那一期的行，见 <see cref="Save"/>）。
/// </summary>
public class SqliteCompanySubsidiaryRepository : ICompanySubsidiaryRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteCompanySubsidiaryRepository(string dbFilePath)
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

    public void Save(string code, DateTime reportDate, int parserVersion,
                     IReadOnlyList<CompanySubsidiary> items)
    {
        var date = reportDate.ToString(DateFormat, CultureInfo.InvariantCulture);

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 重解析时先清掉这一期的旧行。规则改了之后**旧规则捞出来的错名字必须消失**——
        // 只做 upsert 的话它们会一直留在库里，还会继续连出错边。
        // 这是全类唯一的 DELETE，范围严格限定在这一份 PDF 自己的结果内。
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM CompanySubsidiary WHERE code = $c AND report_date = $d;";
            del.Parameters.AddWithValue("$c", code);
            del.Parameters.AddWithValue("$d", date);
            del.ExecuteNonQuery();
        }

        if (items.Count > 0)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO CompanySubsidiary (code, report_date, name, hold_pct, source_page)
                VALUES ($c, $d, $n, $p, $sp)
                ON CONFLICT(code, report_date, name) DO UPDATE SET
                    hold_pct = excluded.hold_pct, source_page = excluded.source_page;
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var n in new[] { "$c", "$d", "$n", "$p", "$sp" })
            { var q = ins.CreateParameter(); q.ParameterName = n; ins.Parameters.Add(q); p[n] = q; }

            foreach (var x in items)
            {
                p["$c"].Value = x.Code;
                p["$d"].Value = date;
                p["$n"].Value = x.Name;
                p["$p"].Value = (object?)x.HoldPct ?? DBNull.Value;
                p["$sp"].Value = x.SourcePage;
                ins.ExecuteNonQuery();
            }
        }

        // ⚠ 水位线跟名单在**同一个事务**里。空名单也要写——found_count = 0 说明版式认不出来，
        //   那是要记下来的事实，不记的话每轮都会重试这份认不出来的 PDF。
        using (var st = conn.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = """
                INSERT INTO SubsidiaryParseState
                    (code, report_date, parser_version, found_count, parsed_at)
                VALUES ($c, $d, $v, $n, $at)
                ON CONFLICT(code, report_date) DO UPDATE SET
                    parser_version = excluded.parser_version,
                    found_count    = excluded.found_count,
                    parsed_at      = excluded.parsed_at;
                """;
            st.Parameters.AddWithValue("$c", code);
            st.Parameters.AddWithValue("$d", date);
            st.Parameters.AddWithValue("$v", parserVersion);
            st.Parameters.AddWithValue("$n", items.Count);
            st.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
            st.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public HashSet<(string Code, DateTime ReportDate)> GetParsed(int parserVersion)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 规则版本低于当前的不算已解析——要重跑
        cmd.CommandText = "SELECT code, report_date FROM SubsidiaryParseState WHERE parser_version >= $v;";
        cmd.Parameters.AddWithValue("$v", parserVersion);

        var set = new HashSet<(string, DateTime)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out var d))
                set.Add((r.GetString(0), d));
        return set;
    }

    public Dictionary<string, string> GetNameToParent()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 一个名字只认一个母公司。**歧义的一律丢掉**——集团内交叉持股、或者两家公司的子公司
        // 重名时，硬挑一个就是在造错边。HAVING 这一句就是在做这件事。
        cmd.CommandText = """
            SELECT name, MIN(code) FROM CompanySubsidiary
            GROUP BY name HAVING COUNT(DISTINCT code) = 1;
            """;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public (int Rows, int Parents, int Parsed, int Empty) GetStats()
    {
        using var conn = Open();
        int N(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            return Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
        return (N("SELECT COUNT(*) FROM CompanySubsidiary"),
                N("SELECT COUNT(DISTINCT code) FROM CompanySubsidiary"),
                N("SELECT COUNT(*) FROM SubsidiaryParseState"),
                N("SELECT COUNT(*) FROM SubsidiaryParseState WHERE found_count = 0"));
    }
}
