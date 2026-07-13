using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>DDL for the shared SQLite schema (see doc/data-platform-design.md section 4). Idempotent.</summary>
public static class SqliteSchema
{
    public static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Bar (
                code TEXT NOT NULL,
                granularity TEXT NOT NULL,
                period_start TEXT NOT NULL,
                open REAL, close REAL, high REAL, low REAL,
                volume REAL, amount REAL, pct_chg REAL, turnover REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, granularity, period_start)
            );

            CREATE TABLE IF NOT EXISTS FundamentalMetric (
                code TEXT NOT NULL,
                metric_key TEXT NOT NULL,
                as_of_date TEXT NOT NULL,
                value REAL,
                source TEXT,
                fetched_at TEXT,
                PRIMARY KEY (code, metric_key, as_of_date)
            );

            CREATE TABLE IF NOT EXISTS NetInflow (
                code TEXT NOT NULL,
                period_start TEXT NOT NULL,
                main_net_inflow REAL,
                fetched_at TEXT,
                PRIMARY KEY (code, period_start)
            );

            CREATE TABLE IF NOT EXISTS StockMeta (
                code TEXT PRIMARY KEY,
                name TEXT,
                exchange TEXT,
                list_date TEXT,
                last_updated TEXT
            );

            CREATE TABLE IF NOT EXISTS OrderWinAnnouncement (
                code TEXT NOT NULL,
                name TEXT,
                title TEXT NOT NULL,
                publish_date TEXT NOT NULL,
                keyword TEXT,
                art_code TEXT,
                pdf_url TEXT,
                content TEXT,
                total_amount_yuan REAL,
                source TEXT,
                fetched_at TEXT,
                PRIMARY KEY (code, title, publish_date)
            );

            CREATE TABLE IF NOT EXISTS Board (
                board_code TEXT PRIMARY KEY,
                board_type INTEGER NOT NULL,   -- 0=概念/题材, 1=行业
                name TEXT,
                member_count INTEGER,
                change_pct REAL,
                amount REAL,
                leader_code TEXT,
                leader_name TEXT,
                as_of TEXT
            );

            CREATE TABLE IF NOT EXISTS BoardMember (
                board_code TEXT NOT NULL,
                stock_code TEXT NOT NULL,
                PRIMARY KEY (board_code, stock_code)
            );
            """;
        cmd.ExecuteNonQuery();

        // 老数据库文件（2026-07-09之前建的）已经有Bar/NetInflow表，上面CREATE TABLE IF NOT
        // EXISTS对已存在的表是空操作，不会补上新列——用ALTER TABLE显式迁移。加列前先检查是否已经
        // 存在（EnsureSchema要保持幂等可重复调用，且ALTER TABLE ADD COLUMN对已有同名列会直接报错）。
        AddColumnIfMissing(conn, "Bar", "fetched_at", "TEXT");
        AddColumnIfMissing(conn, "NetInflow", "fetched_at", "TEXT");
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string columnType)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        checkCmd.Parameters.AddWithValue("$column", column);
        var exists = Convert.ToInt64(checkCmd.ExecuteScalar()) > 0;
        if (exists) return;

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnType};";
        alterCmd.ExecuteNonQuery();
    }
}
