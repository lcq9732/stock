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

    /// <summary>库里现有多少行——给任务层那道"这轮比上次少太多就整轮放弃"的护栏用。</summary>
    public int Count()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM StockIndustry;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// **整表换成这一份**（2026-09-10）：一个事务里清空 + 全量写入，并记下来源。
    ///
    /// 为什么不能用 <see cref="Upsert"/> 逐条覆盖：两个源的大类名分属证监会分类的不同修订版
    /// （"开采辅助活动" vs "开采专业及辅助性活动"），逐条覆盖会把新源没提到的票留在旧名上，
    /// 同一个行业于是裂成两个分组，行业中性化直接受影响——而且不报任何错。
    /// 见 <see cref="IndustrySources"/> 和 doc/industry-source-eastmoney-design.md §5。
    ///
    /// 调用方要保证传进来的是**完整快照**（护栏在 IndustryTask 里：比库里少 5% 以上就不该走到这）。
    /// </summary>
    public void ReplaceAll(IEnumerable<StockIndustry> rows, string source)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM StockIndustry;";
            del.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO StockIndustry (code, class_code, class_name, major_name, source, fetched_at)
            VALUES ($code, $cls, $clsName, $major, $source, $fetched);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pCls = cmd.CreateParameter(); pCls.ParameterName = "$cls"; cmd.Parameters.Add(pCls);
        var pClsName = cmd.CreateParameter(); pClsName.ParameterName = "$clsName"; cmd.Parameters.Add(pClsName);
        var pMajor = cmd.CreateParameter(); pMajor.ParameterName = "$major"; cmd.Parameters.Add(pMajor);
        var pSource = cmd.CreateParameter(); pSource.ParameterName = "$source"; cmd.Parameters.Add(pSource);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            pCode.Value = r.Code;
            pCls.Value = r.ClassCode;
            pClsName.Value = r.ClassName;
            pMajor.Value = r.MajorName;
            pSource.Value = source;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
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
