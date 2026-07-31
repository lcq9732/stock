using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>FinancialReport 表（财务报表关键科目）的存取。每次抓取返回该股全部历史，整体覆盖
/// （DELETE+INSERT，与股东数据的 ReplaceByCode 同一套语义）。</summary>
public class SqliteFinancialRepository
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
        foreach (var r in rows)
        {
            pCode.Value = code;
            pDate.Value = r.ReportDate.ToString("yyyy-MM-dd");
            pKey.Value = r.Key;
            pValue.Value = r.Value;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
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
