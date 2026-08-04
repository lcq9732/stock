using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>StockIndustry 表（证监会行业分类）的存取。行业是全量快照，整体覆盖写入。</summary>
public class SqliteIndustryRepository
{
    private readonly string _connectionString;

    public SqliteIndustryRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    public void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
    }

    public void Upsert(IEnumerable<StockIndustry> rows)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO StockIndustry (code, class_code, class_name, major_name, fetched_at)
            VALUES ($code, $cls, $clsName, $major, $fetched);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pCls = cmd.CreateParameter(); pCls.ParameterName = "$cls"; cmd.Parameters.Add(pCls);
        var pClsName = cmd.CreateParameter(); pClsName.ParameterName = "$clsName"; cmd.Parameters.Add(pClsName);
        var pMajor = cmd.CreateParameter(); pMajor.ParameterName = "$major"; cmd.Parameters.Add(pMajor);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            pCode.Value = r.Code;
            pCls.Value = r.ClassCode;
            pClsName.Value = r.ClassName;
            pMajor.Value = r.MajorName;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>code → 可用的最细行业名（大类优先、门类兜底）。给界面展示和行业中性化用。</summary>
    public Dictionary<string, string> GetBestByCode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, class_name, major_name FROM StockIndustry;";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var major = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var cls = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var best = !string.IsNullOrEmpty(major) ? major : cls;
            if (!string.IsNullOrEmpty(best)) result[reader.GetString(0)] = best;
        }
        return result;
    }
}
