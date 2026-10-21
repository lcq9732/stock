using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 一次性的**存量数据**迁移（区别于 <see cref="SqliteSchema"/> 里的补列/建表：那些是改结构、天然幂等；
/// 这里是改值，乘两遍就错了，所以必须记账）。
///
/// 每条迁移一个 id，跑完在 <c>DataMigration</c> 表里记一行；有记录就不再跑。改值和记账在**同一个事务**里，
/// 中途出错整体回滚、下次启动重来——不会出现"换了一半"或"换完没记账、下次再乘一遍"。
/// Fetcher 和 Analyzer 谁先启动谁做（都走 <see cref="SqliteSchema.EnsureSchema"/>）。
///
/// 大表**一张表一条迁移、一个事务**，跑完做一次 WAL checkpoint：几百万行塞进一个事务会把 WAL 撑得很大
/// （板块指数合成那次单事务写 468 万行，WAL 涨到 162GB）；分开之后中途断了，做完的表也不用重来。
/// </summary>
public static class SqliteDataMigrations
{
    /// <summary>
    /// 入库单位统一第一批（2026-10-20，见 doc/unit-normalization-inventory.md）：
    /// ShareLift、HolderChange、IpoIssue 的万股/万元/亿元/小数换成股/元/百分数。
    /// 系数跟 provider 解析时用的是同一份（<see cref="UnitScale"/>、<see cref="IpoIssueColumns.UnitFactors"/>），
    /// 迁移出来的值跟重新抓的值逐位相同。
    /// </summary>
    public const string UnitBatch1 = "unit_batch1";

    /// <summary>第二批（2026-10-20）：Lhb.amount 万元→元。</summary>
    public const string UnitBatch2Lhb = "unit_batch2_lhb";

    /// <summary>第二批：LhbSeat.buy_ratio / sell_ratio 小数→百分数。</summary>
    public const string UnitBatch2LhbSeat = "unit_batch2_lhbseat";

    /// <summary>第二批：BlockTrade.premium_ratio 小数→百分数。</summary>
    public const string UnitBatch2BlockTrade = "unit_batch2_blocktrade";

    /// <summary>第二批：EtfShare.shares_wan（万份）改名 shares、存份。</summary>
    public const string UnitBatch2EtfShare = "unit_batch2_etfshare";

    /// <summary>第二批：删 CompanyProfile.reg_capital_wan（＝reg_capital÷1e4，6050 行全部吻合，纯重复）。</summary>
    public const string UnitBatch2Profile = "unit_batch2_profile";

    public static void Apply(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS DataMigration (
                    id TEXT PRIMARY KEY,
                    applied_at TEXT NOT NULL,
                    rows INTEGER NOT NULL      -- 这次迁移改了多少行（各表合计），留档用
                );
                -- 分批做的大迁移做到哪了（目前只有 Bar.volume 改存股，见 SqliteBarVolumeMigration）。
                -- 每批改值和挪游标在同一个事务里，停了从这里接着做。
                CREATE TABLE IF NOT EXISTS DataMigrationCursor (
                    id TEXT PRIMARY KEY,
                    last_rowid INTEGER NOT NULL,
                    rows INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Bar.volume 改存股（2026-10-20）是大表，不在启动时做，由【成交量改存股】任务分批做。
        // 这里只处理新库：Bar 一行都没有就直接记完成——以后写进来的本来就是股，没有可迁的。
        Run(conn, SqliteBarVolumeMigration.Id, tx =>
            !TableExists(conn, tx, "Bar") || !HasAnyRow(conn, tx, "Bar") ? 0 : null);

        Run(conn, UnitBatch1, tx =>
            ScaleRows(conn, tx, "ShareLift",
                [("lift_shares", UnitScale.Wan), ("lift_market_cap", UnitScale.Wan),
                 ("pre_free_shares", UnitScale.Wan), ("non_free_shares", UnitScale.Wan),
                 ("free_ratio", UnitScale.Percent), ("total_ratio", UnitScale.Percent)])
            + ScaleRows(conn, tx, "HolderChange",
                [("change_shares", UnitScale.Wan), ("after_shares", UnitScale.Wan)])
            + ScaleIpoIssue(conn, tx));

        // Lhb.volume 一并换（新浪老口径万股）：现在全表为空，只是防着库里还留着新浪行
        Run(conn, UnitBatch2Lhb, tx =>
            ScaleRows(conn, tx, "Lhb", [("amount", UnitScale.Wan), ("volume", UnitScale.Wan)]));
        Run(conn, UnitBatch2LhbSeat, tx =>
            ScaleRows(conn, tx, "LhbSeat", [("buy_ratio", UnitScale.Percent), ("sell_ratio", UnitScale.Percent)]));
        Run(conn, UnitBatch2BlockTrade, tx =>
            ScaleRows(conn, tx, "BlockTrade", [("premium_ratio", UnitScale.Percent)]));
        Run(conn, UnitBatch2EtfShare, tx =>
        {
            // 新库（DDL 直接建出 shares）没有 shares_wan，表里也是空的；老库先改名再换单位
            if (!ColumnExists(conn, tx, "EtfShare", "shares_wan")) return 0;
            Exec(conn, tx, "ALTER TABLE EtfShare RENAME COLUMN shares_wan TO shares;");
            return ScaleRows(conn, tx, "EtfShare", [("shares", UnitScale.Wan)]);
        });
        Run(conn, UnitBatch2Profile, tx =>
        {
            if (!ColumnExists(conn, tx, "CompanyProfile", "reg_capital_wan")) return 0;
            Exec(conn, tx, "ALTER TABLE CompanyProfile DROP COLUMN reg_capital_wan;");
            return 1;
        });
    }

    /// <summary>这条迁移跑过没有（测试和日志用）。</summary>
    public static bool IsApplied(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DataMigration WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <param name="migrate">做迁移、返回改了多少行；返回 null＝"这次不做、也不记账"（事务回滚，下次启动再判）。</param>
    private static void Run(SqliteConnection conn, string id, Func<SqliteTransaction, int?> migrate)
    {
        if (IsApplied(conn, id)) return;
        int rows;
        using (var tx = conn.BeginTransaction())
        {
            if (migrate(tx) is not { } n)
            {
                tx.Rollback();
                return;
            }
            rows = n;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO DataMigration (id, applied_at, rows) VALUES ($id, $at, $rows);";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("$rows", rows);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        // 大表改完把 WAL 落回主库，别让下一张表接着往上叠。有别的连接在读时 checkpoint 做不完整，不报错，无妨。
        if (rows > 10_000)
        {
            using var ck = conn.CreateCommand();
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            ck.ExecuteNonQuery();
        }
    }

    /// <summary>整张表每一行的这几列各乘一个系数（空值不动）。表还没建（新库）就是 0 行。</summary>
    private static int ScaleRows(SqliteConnection conn, SqliteTransaction tx, string table,
        IReadOnlyList<(string Column, decimal Factor)> columns)
    {
        var factors = columns.Select(c => c.Factor).ToArray();
        return ScaleRows(conn, tx, table, columns.Select(c => c.Column).ToList(), "NULL", "NULL", (_, _) => factors);
    }

    /// <summary>
    /// IpoIssue：沪深和北交所有三列单位不同，系数按每一行的 is_beijing 选（跟 provider 同一个判断）。
    /// </summary>
    private static int ScaleIpoIssue(SqliteConnection conn, SqliteTransaction tx)
    {
        var apis = IpoIssueColumns.UnitFactors.Keys.ToList();
        return ScaleRows(conn, tx, "IpoIssue", apis.Select(a => a.ToLowerInvariant()).ToList(), "code", "is_beijing",
            (code, isBeijing) =>
            {
                var beijing = IpoIssueColumns.IsBeijing(code!, isBeijing);
                return apis.Select(a => IpoIssueColumns.FactorOf(a, beijing)).ToArray();
            });
    }

    /// <param name="codeExpr">/<param name="beijingExpr">传给 <paramref name="factorsOf"/> 的两列（不需要就写 NULL）。</param>
    private static int ScaleRows(SqliteConnection conn, SqliteTransaction tx, string table, IReadOnlyList<string> columns,
        string codeExpr, string beijingExpr, Func<string?, double?, decimal[]> factorsOf)
    {
        if (!TableExists(conn, tx, table)) return 0;

        using var write = conn.CreateCommand();
        write.Transaction = tx;
        write.CommandText = $"UPDATE {table} SET {string.Join(", ", columns.Select((c, i) => $"{c} = $v{i}"))} WHERE rowid = $id;";
        var ps = columns.Select((_, i) => write.Parameters.Add($"$v{i}", SqliteType.Real)).ToArray();
        var pid = write.Parameters.Add("$id", SqliteType.Integer);

        // 按 rowid 分块：先把一块读完、关掉游标再写。不在读游标打开时改同一张表——值的存储长度会变
        // （REAL 存整数值时按整数编码），改动可能让页重排，SQLite 对"扫描中被改的表"不保证游标结果。
        using var read = conn.CreateCommand();
        read.Transaction = tx;
        read.CommandText = $"SELECT rowid, {codeExpr}, {beijingExpr}, {string.Join(", ", columns)} FROM {table} "
                         + "WHERE rowid > $after ORDER BY rowid LIMIT 20000;";
        var pAfter = read.Parameters.Add("$after", SqliteType.Integer);
        long after = long.MinValue;
        int changed = 0;
        var chunk = new List<(long RowId, double?[] Values, decimal[] Factors)>();
        while (true)
        {
            chunk.Clear();
            pAfter.Value = after;
            int seen = 0;
            using (var r = read.ExecuteReader())
                while (r.Read())
                {
                    seen++;
                    after = r.GetInt64(0);
                    var values = new double?[columns.Count];
                    bool any = false;
                    for (int i = 0; i < columns.Count; i++)
                    {
                        if (r.IsDBNull(3 + i)) continue;
                        values[i] = r.GetDouble(3 + i);
                        any = true;
                    }
                    if (!any) continue;
                    var factors = factorsOf(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetDouble(2));
                    if (factors.All(f => f == 1m)) continue;
                    chunk.Add((after, values, factors));
                }
            if (seen == 0) break;

            foreach (var (rowId, values, factors) in chunk)
            {
                for (int i = 0; i < columns.Count; i++)
                    ps[i].Value = values[i] is { } v ? UnitScale.Apply(v, factors[i]) : DBNull.Value;
                pid.Value = rowId;
                write.ExecuteNonQuery();
            }
            changed += chunk.Count;
        }
        return changed;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t;";
        cmd.Parameters.AddWithValue("$t", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool HasAnyRow(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {table});";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    private static bool ColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c;";
        cmd.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
