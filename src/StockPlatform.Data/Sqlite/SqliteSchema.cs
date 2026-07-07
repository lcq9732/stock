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

            CREATE TABLE IF NOT EXISTS StockMeta (
                code TEXT PRIMARY KEY,
                name TEXT,
                exchange TEXT,
                list_date TEXT,
                last_updated TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }
}
