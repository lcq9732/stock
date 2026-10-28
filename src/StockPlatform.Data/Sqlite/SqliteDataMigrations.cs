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

    /// <summary>
    /// 清掉**配错了正文**的公告行（2026-10-22）：东财正文原来按"±3 天、取窗口里最新那篇"匹配，
    /// 连发几天同名公告的票全配到了同一篇（回购 19 行、中标订单 4 行），判据见 <see cref="AnnouncementMatchRule.FindMismatched"/>。
    /// 正文编号和抽出的数值清空、标题留着，变回空壳：回购那边每轮的回补段会按新规则重取；
    /// 超出东财"最近 100 条"的取不回来，就一直空着——空着比错着好。
    /// </summary>
    public const string AnnouncementMismatchCleanup = "announcement_mismatch_cleanup";

    /// <summary>
    /// 回购公告抽取规则升到第 2 版（2026-10-23，见 <c>PlanAnnouncementExtractor.Version</c>）时，
    /// 把**不用重抽**的老行先记成第 2 版，只留真要重抽的进队列：
    /// · 「方案」行——第 2 版只改了进展类（截至句、累计数），离线比对 60 份方案公告一条都没变；
    /// · 空壳行（没取到过正文）——没东西可重抽，归回补段管，补上时自然按新版抽。
    /// 剩下的约 2069 行由【回购公告进展】每轮尾巴上按 art_code 重取正文重抽。
    /// </summary>
    public const string PlanExtractV2Baseline = "plan_extract_v2_baseline";

    /// <summary>
    /// 预约披露日合并表的起点（2026-10-27，见 doc/financial-fetch-disclosure-race-design.md）：
    /// 把巨潮现有的行原样复制进 EarningsScheduleMerged。
    ///
    /// 消费端改成只读合并表之后，这一步是**上线当天不塌**的保证——否则在下一次
    /// 【拉取财报预约日】跑完之前合并表是空的，财务/股东/金融监管三处闸门会一起退回
    /// 兜底判据，界面的财报日列全空。
    /// </summary>
    public const string EarningsScheduleMergedBaseline = "earnings_schedule_merged_baseline";

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
        Run(conn, AnnouncementMismatchCleanup, tx =>
            ClearMismatched(conn, tx, "PlanAnnouncement", "announce_date",
                "art_code = '', as_of_date = NULL, cum_shares = NULL, cum_amount = NULL, price_low = NULL, "
                + "price_high = NULL, pct_of_capital = NULL, plan_cap_price = NULL, plan_amount_low = NULL, plan_amount_high = NULL")
            + ClearMismatched(conn, tx, "OrderWinAnnouncement", "publish_date",
                "art_code = '', content = NULL, total_amount_yuan = NULL"));
        Run(conn, PlanExtractV2Baseline, tx =>
        {
            if (!TableExists(conn, tx, "PlanAnnouncement")) return 0;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // 写死 2，不引用 PlanAnnouncementExtractor.Version：这是"第 2 版时核对过不用重抽"这件事本身，
            // 以后版本再升，这些行照样该进重抽队列。
            cmd.CommandText = """
                UPDATE PlanAnnouncement SET extract_ver = 2
                 WHERE IFNULL(extract_ver, 1) < 2
                   AND (stage = '方案' OR art_code IS NULL OR art_code = '');
                """;
            return cmd.ExecuteNonQuery();
        });
        Run(conn, EarningsScheduleMergedBaseline, tx =>
        {
            if (!TableExists(conn, tx, "EarningsSchedule")) return 0;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // 把巨潮现有的行原样灌进合并表当起点。**不做这一步的话**，上线后到下一次
            // 【拉取财报预约日】跑完之前，合并表是空的，而消费端已经改成只读它——
            // 财务抓取、股东抓取、金融监管指标会一起退回"谁都没披露"的兜底判据，
            // 界面的财报日列全空。那就不是"下限等于现状"，是直接砸出一个坑。
            cmd.CommandText = """
                INSERT INTO EarningsScheduleMerged
                    (code, report_period, appoint_date, change1, change2, change3, actual_date, fetched_at)
                SELECT code, report_period, appoint_date, change1, change2, change3, actual_date, fetched_at
                  FROM EarningsSchedule
                 WHERE true          -- ⚠ 不能删：INSERT…SELECT 后面直接跟 ON CONFLICT，SQLite 会把
                                     --   ON 当成 JOIN 的 ON 子句，报 near "DO": syntax error。
                                     --   官方给的消歧办法就是补一个 WHERE。
                ON CONFLICT(code, report_period) DO NOTHING;
                """;
            return cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// 按 <see cref="AnnouncementMatchRule.FindMismatched"/> 找出配错正文的行，执行 <paramref name="clearSet"/> 清空。
    /// 表还没建（新库）就是 0 行。
    /// </summary>
    private static int ClearMismatched(SqliteConnection conn, SqliteTransaction tx, string table, string dateColumn, string clearSet)
    {
        if (!TableExists(conn, tx, table)) return 0;

        var rowIds = new List<long>();
        var rows = new List<(string Code, DateOnly AnnounceDate, string ArtCode)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT rowid, code, {dateColumn}, art_code FROM {table} WHERE art_code IS NOT NULL AND art_code <> '';";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d)) continue;
                rowIds.Add(r.GetInt64(0));
                rows.Add((r.GetString(1), DateOnly.FromDateTime(d), r.GetString(3)));
            }
        }

        var wrong = AnnouncementMatchRule.FindMismatched(rows);
        using var write = conn.CreateCommand();
        write.Transaction = tx;
        write.CommandText = $"UPDATE {table} SET {clearSet} WHERE rowid = $id;";
        var pid = write.Parameters.Add("$id", SqliteType.Integer);
        foreach (var i in wrong)
        {
            pid.Value = rowIds[i];
            write.ExecuteNonQuery();
        }
        return wrong.Count;
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
