using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// StockMeta 的存取。2026-07-15 起这张表不只装个股，还装指数/ETF/板块（各带 type 区分），让它们也能
/// 在"查询"页被搜到、看行情。但**个股相关的用法（拉取当天的抓取清单、各选股页补名称）仍只要个股**：
/// <see cref="GetAll"/> 只返回 type='stock'（含老库 type=NULL 的行）；要拿全部标的用
/// <see cref="GetAllInstruments"/>。写入时 <see cref="Upsert(string, IEnumerable{ValueTuple{string, string}}, string)"/>
/// 显式带 type——个股默认 'stock'，指数/ETF/板块由抓取流程分别以 'index'/'etf'/'board' 写入。
/// </summary>
public static class SqliteStockMetaUpsert
{
    public const string TypeStock = "stock";
    public const string TypeIndex = "index";
    public const string TypeEtf = "etf";
    public const string TypeBoard = "board";

    /// <summary>写入/更新一批标的的 code+name+type（INSERT OR REPLACE，保留已有的 exchange/list_date）。
    /// 不显式传 type 时默认按个股 'stock' 处理。</summary>
    public static void Upsert(string dbFilePath, IEnumerable<(string Code, string Name)> stocks, string type = TypeStock)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO StockMeta (code, name, type, exchange, list_date, last_updated)
            VALUES ($code, $name, $type,
                COALESCE((SELECT exchange FROM StockMeta WHERE code = $code), ''),
                (SELECT list_date FROM StockMeta WHERE code = $code),
                $last_updated);
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
        var pType = cmd.CreateParameter(); pType.ParameterName = "$type"; cmd.Parameters.Add(pType);
        var pUpdated = cmd.CreateParameter(); pUpdated.ParameterName = "$last_updated"; cmd.Parameters.Add(pUpdated);

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        pType.Value = type;
        foreach (var (code, name) in stocks)
        {
            pCode.Value = code;
            pName.Value = name;
            pUpdated.Value = now;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>只返回**个股**（type='stock'，以及老库里 type 为 NULL 的行）——个股列表(拉取当天的抓取
    /// 全集)和各选股页补名称都只要个股，绝不能混入指数/ETF/板块。</summary>
    public static List<(string Code, string Name)> GetAll(string dbFilePath)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, name FROM StockMeta WHERE type = 'stock' OR type IS NULL ORDER BY code;";
        var result = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return result;
    }

    /// <summary>返回**全部标的**（个股+指数+ETF+板块）及其 type，供"查询"页搜索用。老库 type=NULL 归为
    /// 'stock'。</summary>
    public static List<(string Code, string Name, string Type)> GetAllInstruments(string dbFilePath)
    {
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, name, type FROM StockMeta ORDER BY code;";
        var result = new List<(string, string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? TypeStock : reader.GetString(2)));
        return result;
    }
}
