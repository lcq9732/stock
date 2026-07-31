using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>DelistedStock 表（终止上市公司名单）的存取。名单是全量快照，Upsert 用 INSERT OR REPLACE 整体刷新。</summary>
public class SqliteDelistedRepository
{
    private readonly string _connectionString;

    public SqliteDelistedRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    public void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
    }

    public void Upsert(IEnumerable<DelistedStockRow> rows)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 名单是全量快照，整体覆盖；但 tail_fetched_at 是本地进度、必须保留（否则每次刷新名单都会
        // 把"已经补过最后几天"的标记清掉，退市股又会被反复重抓）。
        cmd.CommandText = """
            INSERT OR REPLACE INTO DelistedStock (code, name, exchange, list_date, delist_date, fetched_at, tail_fetched_at)
            VALUES ($code, $name, $exchange, $list, $delist, $fetched,
                (SELECT tail_fetched_at FROM DelistedStock WHERE code = $code));
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pEx = cmd.CreateParameter(); pEx.ParameterName = "$exchange"; cmd.Parameters.Add(pEx);
        var pList = cmd.CreateParameter(); pList.ParameterName = "$list"; cmd.Parameters.Add(pList);
        var pDelist = cmd.CreateParameter(); pDelist.ParameterName = "$delist"; cmd.Parameters.Add(pDelist);
        var pFetched = cmd.CreateParameter(); pFetched.ParameterName = "$fetched"; cmd.Parameters.Add(pFetched);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in rows)
        {
            pCode.Value = r.Code;
            pName.Value = r.Name;
            pEx.Value = r.Exchange;
            pList.Value = (object?)r.ListDate?.ToString("yyyy-MM-dd") ?? DBNull.Value;
            pDelist.Value = (object?)r.DelistDate?.ToString("yyyy-MM-dd") ?? DBNull.Value;
            pFetched.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>还没尝试过补"最后几天"K线的代码（tail_fetched_at IS NULL）。</summary>
    public HashSet<string> GetTailPendingCodes()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code FROM DelistedStock WHERE tail_fetched_at IS NULL;";
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>标记这些代码已经尝试过补最后几天（成功或"确实没有更多数据"都算，之后永久跳过；
    /// 抓取失败的不要传进来，留待下次重试）。</summary>
    public void MarkTailFetched(IEnumerable<string> codes)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE DelistedStock SET tail_fetched_at = $at WHERE code = $code;";
        var pAt = cmd.CreateParameter(); pAt.ParameterName = "$at"; cmd.Parameters.Add(pAt);
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        pAt.Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var code in codes) { pCode.Value = code; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public List<DelistedStockRow> GetAll()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, name, exchange, list_date, delist_date FROM DelistedStock ORDER BY code;";
        var result = new List<DelistedStockRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DelistedStockRow
            {
                Code = reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Exchange = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ListDate = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                DelistDate = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
            });
        }
        return result;
    }
}
