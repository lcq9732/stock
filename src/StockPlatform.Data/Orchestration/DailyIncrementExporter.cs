using Microsoft.Data.Sqlite;
using StockPlatform.Data.Sqlite;

namespace StockPlatform.Data.Orchestration;

/// <summary>
/// 从 Fetcher 的完整库(current.sqlite)里抽出"某一天 D"的增量,写成一个小 sqlite,供上传 GitHub
/// Releases 分发(见 doc/data-platform-design.md 2026-07-14 变更记录)。抽取范围:
///   Bar：D 当天的日线 + 覆盖 D 的那根周线/月线(周/月线每天都在变，必须带上；周期起点是
///        该周/月首个交易日，用 ">= 周一/月一号" 精确圈出当前这根)；
///   NetInflow / FundamentalMetric / OrderWinAnnouncement：as-of/日期 = D 的行；
///   StockMeta：全部(很小，一并带上，新股上市也能同步)。
/// 分析程序用 <see cref="SqliteMerger"/> 把它 INSERT OR REPLACE 并进本地 total.sqlite——"今天"这类
/// 会被盘后修正的行靠 REPLACE 覆盖，历史行重复并入也安全。
/// </summary>
public static class DailyIncrementExporter
{
    public static void Export(string currentDbPath, DateTime date, string outSqlitePath)
    {
        if (File.Exists(outSqlitePath)) File.Delete(outSqlitePath);

        // 先建好空的目标增量库(schema 与主库一致)。
        using (var init = new SqliteConnection($"Data Source={outSqlitePath}"))
        {
            init.Open();
            SqliteSchema.EnsureSchema(init);
        }

        string d = date.ToString("yyyy-MM-dd");
        string monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd");
        string firstOfMonth = new DateTime(date.Year, date.Month, 1).ToString("yyyy-MM-dd");

        using var conn = new SqliteConnection($"Data Source={outSqlitePath}");
        conn.Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $src AS src";
            attach.Parameters.AddWithValue("$src", currentDbPath);
            attach.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        void Run(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // 日线取当天；周/月线取"覆盖 D 的当前那根"(period_start >= 本周一 / 本月一号，且是最新一根)。
        Run($@"INSERT OR REPLACE INTO Bar SELECT * FROM src.Bar WHERE
                (granularity='day'   AND period_start LIKE '{d}%') OR
                (granularity='week'  AND period_start >= '{monday}') OR
                (granularity='month' AND period_start >= '{firstOfMonth}')");
        Run($"INSERT OR REPLACE INTO NetInflow SELECT * FROM src.NetInflow WHERE period_start LIKE '{d}%'");
        Run($"INSERT OR REPLACE INTO FundamentalMetric SELECT * FROM src.FundamentalMetric WHERE as_of_date LIKE '{d}%'");
        Run($"INSERT OR REPLACE INTO OrderWinAnnouncement SELECT * FROM src.OrderWinAnnouncement WHERE publish_date LIKE '{d}%'");
        Run("INSERT OR REPLACE INTO StockMeta SELECT * FROM src.StockMeta");
        tx.Commit();

        using (var detach = conn.CreateCommand())
        {
            detach.CommandText = "DETACH DATABASE src";
            detach.ExecuteNonQuery();
        }
        SqliteConnection.ClearPool(conn);
    }
}
