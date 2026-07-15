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
                volume REAL, amount REAL, turnover REAL,
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

        // 2026-07-14 起不再存储涨跌幅——它是收盘价的派生值，全部改成读取时现算（存一份反而多一层
        // "每条写入路径都得同步更新"的负担，之前 pct_chg 常年为0正是这个坑，见 doc §9.5 的原则）。
        // 老库里已有的 pct_chg 列在这里删掉，保持"代码里没有、库里也没有"一致；DROP COLUMN 会重写
        // 整张表，856MB 的库首次执行需要几十秒，之后列已不在、此调用变成空操作。
        DropColumnIfExists(conn, "Bar", "pct_chg");
    }

    private static void DropColumnIfExists(SqliteConnection conn, string table, string column)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        checkCmd.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(checkCmd.ExecuteScalar()) == 0) return;

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
        alterCmd.ExecuteNonQuery();
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
