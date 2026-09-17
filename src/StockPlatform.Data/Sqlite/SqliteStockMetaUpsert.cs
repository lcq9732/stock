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
    /// <summary>已退市个股（2026-07-29）——故意不用 'stock'：<see cref="GetAll"/>（拉取当天/拉取全部的
    /// 本地抓取清单）只认 'stock'，退市股不该被日常抓取轮询；但查询页/FactorLab 按代码前缀选池时能看到。</summary>
    public const string TypeDelisted = "delisted";

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
        // ⚠ 用 ON CONFLICT 而不是 INSERT OR REPLACE，为的是那句 type 的 CASE：
        // **已经标成 delisted 的，不许被 'stock' 覆盖回去**（2026-09-17 加）。
        //
        // 为什么需要：在市名单源对"什么叫在市"的口径不一致——上交所的 stockType=10（沪市全量）
        // 里就含 18 只**已经退市**的票（600193 退市创兴、600355 *ST精伦 那批，K线停在 80~600 天前）。
        // 没有这道保护的话，日更的【刷新名册】把它们写成 'stock'、周期组的【补全退市名单】
        // 再写回 'delisted'，两边来回翻，而退市股一旦变回 'stock' 就重新进入日常轮询，
        // 每天几百个必然落空的请求（project_dividend_delisted_gap 当初就是为了避免这个）。
        //
        // 退市是不可逆的状态，所以让 delisted 赢。真有"恢复上市"那种极罕见情况，
        // 手工把 DelistedStock 和这张表里的 delisted 行删掉即可。
        cmd.CommandText = """
            INSERT INTO StockMeta (code, name, type, exchange, list_date, last_updated)
            VALUES ($code, $name, $type, '', NULL, $last_updated)
            ON CONFLICT(code) DO UPDATE SET
                name = excluded.name,
                type = CASE WHEN StockMeta.type = 'delisted' AND excluded.type = 'stock'
                            THEN 'delisted' ELSE excluded.type END,
                last_updated = excluded.last_updated;
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

    /// <summary>
    /// 按 type 取标的。<see cref="GetAll"/> 只给个股（它被 20 多处共用，选股页、Mobile 都靠它补名称，
    /// 混进别的类型会污染候选池，所以那个方法不能动），需要别的组合就用这个。
    ///
    /// 眼下唯一的用途是**分红送配抓取**：它要的是 stock + delisted。退市股的分红以前一直没抓过——
    /// 2026-09-06 查出来 2016 年后 617 条除权缺口里 562 条（91%）是退市股，而新浪明明有数据
    /// （实测 600705 有 36 条到 1996 年、600837 有 26 条到 1995 年）。回测要消除幸存者偏差，
    /// 恰恰最需要退市股的完整复权。
    /// </summary>
    public static List<(string Code, string Name)> GetByTypes(string dbFilePath, params string[] types)
    {
        if (types.Length == 0) return new List<(string, string)>();
        using var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);

        using var cmd = conn.CreateCommand();
        // 老库 type 为 NULL 的行算作个股（跟 GetAll 的口径保持一致）
        var ps = types.Select((_, i) => $"$t{i}").ToList();
        var nullClause = types.Contains("stock") ? " OR type IS NULL" : "";
        cmd.CommandText = $"SELECT code, name FROM StockMeta WHERE type IN ({string.Join(",", ps)}){nullClause} ORDER BY code;";
        for (int i = 0; i < types.Length; i++) cmd.Parameters.AddWithValue($"$t{i}", types[i]);
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
