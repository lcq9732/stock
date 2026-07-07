using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

public static class SqliteStockMetaUpsert
{
    public static void Upsert(string dbFilePath, IEnumerable<(string Code, string Name)> stocks)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO StockMeta (code, name, exchange, list_date, last_updated)
            VALUES ($code, $name,
                COALESCE((SELECT exchange FROM StockMeta WHERE code = $code), ''),
                (SELECT list_date FROM StockMeta WHERE code = $code),
                $last_updated);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pUpdated = cmd.CreateParameter(); pUpdated.ParameterName = "$last_updated"; cmd.Parameters.Add(pUpdated);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var (code, name) in stocks)
        {
            pCode.Value = code;
            pName.Value = name;
            pUpdated.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static List<(string Code, string Name)> GetAll(string dbFilePath)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, name FROM StockMeta ORDER BY code;";
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return result;
    }
}
