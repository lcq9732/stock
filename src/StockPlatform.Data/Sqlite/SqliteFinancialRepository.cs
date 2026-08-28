using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>FinancialReport 表（财务报表关键科目）的存取。每次抓取返回该股全部历史，整体覆盖
/// （DELETE+INSERT，与股东数据的 ReplaceByCode 同一套语义）。</summary>
public class SqliteFinancialRepository : IFinancialRepository
{
    private readonly string _connectionString;

    public SqliteFinancialRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    public void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
    }

    public void ReplaceByCode(string code, IEnumerable<FinancialValue> rows)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM FinancialReport WHERE code = $code;";
            del.Parameters.AddWithValue("$code", code);
            del.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO FinancialReport (code, report_date, metric_key, value, fetched_at)
            VALUES ($code, $date, $key, $value, $fetched);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pDate = cmd.CreateParameter(); pDate.ParameterName = "$date"; cmd.Parameters.Add(pDate);
        var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
        var pValue = cmd.CreateParameter(); pValue.ParameterName = "$value"; cmd.Parameters.Add(pValue);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        // 顺便记下最新报告期给 FinancialFetchState 用。**不能事后 rows.Max()**——rows 是
        // IEnumerable，二次遍历对延迟求值的序列可能重算甚至取不到值。
        DateTime? maxDate = null;
        foreach (var r in rows)
        {
            if (maxDate == null || r.ReportDate > maxDate.Value) maxDate = r.ReportDate;
            pCode.Value = code;
            pDate.Value = r.ReportDate.ToString("yyyy-MM-dd");
            pKey.Value = r.Key;
            pValue.Value = r.Value;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }

        // 抓取状态跟数据在**同一个事务**里写——否则中途崩了会出现"数据是新版、状态还是旧版"
        // （下次白重抓一遍，不致命）或者更糟的"状态是新版、数据没写进去"（新科目永远补不上）。
        using (var st = conn.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = """
                INSERT OR REPLACE INTO FinancialFetchState (code, keys_version, report_date, fetched_at)
                VALUES ($code, $ver, $date, $fetched);
                """;
            st.Parameters.AddWithValue("$code", code);
            st.Parameters.AddWithValue("$ver", FinancialKeys.Version);
            st.Parameters.AddWithValue("$date",
                maxDate.HasValue ? maxDate.Value.ToString("yyyy-MM-dd") : (object)DBNull.Value);
            st.Parameters.AddWithValue("$fetched", now);
            st.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<FinancialSnapshot> GetAllByCode(string code)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_date, metric_key, value FROM FinancialReport
            WHERE code = $code AND value IS NOT NULL
            ORDER BY report_date DESC;
            """;
        cmd.Parameters.AddWithValue("$code", code);

        var byDate = new Dictionary<DateTime, FinancialSnapshot>();
        var order = new List<DateTime>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!DateTime.TryParseExact(reader.GetString(0), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!byDate.TryGetValue(d, out var snap))
            {
                snap = new FinancialSnapshot { ReportDate = d };
                byDate[d] = snap;
                order.Add(d);
            }
            snap.Values[reader.GetString(1)] = reader.GetDouble(2);
        }
        return order.Select(d => byDate[d]).ToList();
    }

    public Dictionary<string, (DateTime ReportDate, int KeysVersion)> GetFetchStateByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, keys_version, report_date FROM FinancialFetchState WHERE report_date IS NOT NULL;";
        var result = new Dictionary<string, (DateTime, int)>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) continue;
            if (!DateTime.TryParseExact(reader.GetString(2), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            result[reader.GetString(0)] = (d, reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
        }
        return result;
    }

    public Dictionary<string, FinancialSnapshot> GetLatestSnapshotByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        // 每只股票只取它自己最新那一期的全部科目——相关子查询在 (code, report_date) 主键上走索引，
        // 比把 268 万行全拉回内存再筛快得多。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.code, f.report_date, f.metric_key, f.value
            FROM FinancialReport f
            WHERE f.report_date = (SELECT MAX(report_date) FROM FinancialReport WHERE code = f.code);
            """;
        var result = new Dictionary<string, FinancialSnapshot>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            if (!result.TryGetValue(code, out var snap))
            {
                snap = new FinancialSnapshot { ReportDate = DateTime.Parse(reader.GetString(1)) };
                result[code] = snap;
            }
            if (!reader.IsDBNull(3)) snap.Values[reader.GetString(2)] = reader.GetDouble(3);
        }
        return result;
    }

    public Dictionary<string, List<(int Year, double NetProfitParent)>> GetRecentAnnualNetProfitByCode(int count)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        // 只取 12-31 的年报口径（A股财报是年内累计，只有12-31那期等于全年）。一次全捞回来再在
        // 内存里按 code 截取最近 N 年——比每只股票发一次查询快得多（5000+ 只 × 一次往返的差别）。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT code, report_date, value FROM FinancialReport
            WHERE metric_key = '{FinancialKeys.NetProfitParent}' AND report_date LIKE '%-12-31'
            ORDER BY code, report_date;
            """;
        var result = new Dictionary<string, List<(int, double)>>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(2)) continue;
            var code = reader.GetString(0);
            var year = int.Parse(reader.GetString(1)[..4]);
            if (!result.TryGetValue(code, out var list)) result[code] = list = new List<(int, double)>();
            list.Add((year, reader.GetDouble(2)));
        }
        // ORDER BY 保证了年份升序，这里只留最近 count 个
        foreach (var (code, list) in result)
            if (list.Count > count) result[code] = list.GetRange(list.Count - count, count);
        return result;
    }

    /// <summary>每个代码本地最新的报告期——给"按报告期跳过"的增量逻辑用（财报一季度才变一次，
    /// 已经有最新一期的股票不用再发请求）。</summary>
    public Dictionary<string, DateTime> GetLatestReportDateByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, MAX(report_date) FROM FinancialReport GROUP BY code;";
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = DateTime.Parse(reader.GetString(1));
        }
        return result;
    }
}
