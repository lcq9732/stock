using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 把一个"增量/全量"sqlite 合并进目标 sqlite——ATTACH 源库 + 每张表 INSERT OR REPLACE。用于
/// GitHub 分发方案里，分析程序把下载来的每日增量(daily-*.zip 解压出的小 sqlite)并进本地 total.sqlite
/// （见 doc/data-platform-design.md 的 2026-07-14 变更记录）。两边都是同一套代码产出的 schema，列
/// 顺序一致，所以 `INSERT OR REPLACE INTO T SELECT * FROM src.T` 是安全的；INSERT OR REPLACE 让
/// 增量里"今天"这类会被修正的行覆盖旧值，历史行重复并入也不会报主键冲突。整体包在一个事务里。
///
/// (这套合并逻辑早期存在过、后来随废弃的网盘方案删掉了；2026-07-14 因改用 GitHub Releases 分发
/// 重新引入，只是数据来源从网盘换成了 release 资产。)
/// </summary>
public static class SqliteMerger
{
    private static readonly string[] Tables =
        { "Bar", "FundamentalMetric", "NetInflow", "StockMeta", "OrderWinAnnouncement" };

    public static void MergeInto(string targetDbPath, string sourceDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={targetDbPath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn); // 目标库可能是首次创建，先建好表结构

        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $src AS src";
            attach.Parameters.AddWithValue("$src", sourceDbPath);
            attach.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        foreach (var t in Tables)
        {
            // 源库里没有这张表(增量可能只含部分表)就跳过，不报错。
            using var check = conn.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM src.sqlite_master WHERE type='table' AND name=$t";
            check.Parameters.AddWithValue("$t", t);
            if (Convert.ToInt64(check.ExecuteScalar()) == 0) continue;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT OR REPLACE INTO {t} SELECT * FROM src.{t}";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        using var detach = conn.CreateCommand();
        detach.CommandText = "DETACH DATABASE src";
        detach.ExecuteNonQuery();

        SqliteConnection.ClearPool(conn); // 释放文件句柄，方便调用方随后删除临时源文件
    }
}
