using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>一批迁移的结果。</summary>
/// <param name="Rows">这一批改了多少行。</param>
/// <param name="Cursor">做到哪个 rowid 了（含）。</param>
/// <param name="MaxRowId">Bar 表当前最大 rowid——进度的分母。</param>
/// <param name="Done">全表做完了（已记进 <c>DataMigration</c>）。</param>
public sealed record BarVolumeMigrationChunk(long Rows, long Cursor, long MaxRowId, bool Done);

/// <summary>
/// 【成交量改存股】的存量迁移（2026-10-20，见 doc/unit-normalization-inventory.md）：
/// <c>Bar.volume</c> 全表 ×100，手 → 股（ETF 份）。个股、ETF、指数、板块指数、四个口径一起改。
///
/// ════ 为什么不跟别的迁移一样放在启动时（<see cref="SqliteDataMigrations"/>）════
/// 8,325 万行、25GB 的库，一口气做会把 Fetcher 和 Analyzer 的启动卡十几到几十分钟。
/// 所以做成一个任务（<c>BarVolumeSharesTask</c>），分批、有进度、能停能续。
///
/// ════ 为什么按 rowid 顺序、而不是一只票一只票地改 ════
/// 每天的抓取是全市场一起插入的，同一页里混着几十只票。按票改等于每一页被重写几十遍；
/// 按 rowid 顺序整表扫一遍，每页只写一次。代价是做到一半时同一只票新旧两种单位混着——
/// 所以迁移没做完之前调度器不放行任何别的任务（<c>BarVolumeMigrationGate</c>），Analyzer 显示提醒。
///
/// ════ 能停能续、不会乘两遍 ════
/// 每一批是一个事务：改值 + 把"做到哪个 rowid"写进 <c>DataMigrationCursor</c>，一起提交。
/// 中途被停、被杀，下次从游标接着做；全表做完写 <c>DataMigration</c>（id = <see cref="Id"/>），此后不再动。
///
/// ════ 换算：整数在 SQL 里乘，带小数的在 C# 里乘 ════
/// 绝大多数行是整数手，<c>volume × 100</c> 在 double 里是精确的，一条 UPDATE 做掉（8 千万行逐行往返太慢）。
/// 带小数的（科创板历史上存的 317113.34 手、板块指数的加总）double 乘 100 会得 31711333.999999996，
/// 这些行逐行走 <c>UnitScale.Apply</c>（decimal），跟新抓进来的值逐位相同。
/// ⚠ 不能用 SQLite 的 <c>ROUND(volume*100, 6)</c> 兜底：它内部"转字符串再转回来"有精度损失，
/// 实测把精确整数 41779124800 变成了 41779124799.999992。
/// 顺序要紧：先乘整数行、再挑非整数行——反过来的话，小数行乘完可能变成整数，会被再乘一遍。
/// 只改 volume 一列，**不碰 fetched_at**：改的是单位不是值，碰了会让【重算回测序列】以为
/// day_raw 被重抓过、把全库 day_adj 重算一遍。
/// </summary>
public sealed class SqliteBarVolumeMigration(string dbPath)
{
    /// <summary>做完后记在 <c>DataMigration</c> 里的 id。</summary>
    public const string Id = "bar_volume_shares";

    /// <summary>
    /// 每批跨多少个 rowid。50 万行一个事务，WAL 只涨几十 MB；一批几秒，进度和停止都够细。
    /// </summary>
    public const long DefaultChunkRowIds = 500_000;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var busy = c.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout = 30000;";
        busy.ExecuteNonQuery();
        return c;
    }

    /// <summary>做完没有。</summary>
    public bool IsDone()
    {
        using var c = Open();
        return IsDone(c);
    }

    public static bool IsDone(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='DataMigration';";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return false;
        cmd.CommandText = "SELECT COUNT(*) FROM DataMigration WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", Id);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>当前进度：(做到的 rowid, 最大 rowid)。没开始过游标是 0。</summary>
    public (long Cursor, long MaxRowId) Progress()
    {
        using var c = Open();
        return (ReadCursor(c, null).Cursor, MaxRowId(c, null));
    }

    /// <summary>
    /// 做一批：rowid 落在 (游标, 游标 + <paramref name="chunkRowIds"/>] 的行 volume ×100，游标前移，一个事务。
    /// 已经做完的再调直接返回 Done、什么都不改。
    /// </summary>
    public BarVolumeMigrationChunk RunChunk(long chunkRowIds = DefaultChunkRowIds)
    {
        using var c = Open();
        if (IsDone(c)) return new BarVolumeMigrationChunk(0, 0, 0, true);

        long changed, hi, max;
        bool done;
        using (var tx = c.BeginTransaction())
        {
            var (cursor, total) = ReadCursor(c, tx);
            max = MaxRowId(c, tx);
            hi = Math.Min(cursor + chunkRowIds, max);

            changed = hi > cursor ? ScaleRange(c, tx, cursor, hi) : 0;
            total += changed;

            using (var cur = c.CreateCommand())
            {
                cur.Transaction = tx;
                cur.CommandText = """
                    INSERT INTO DataMigrationCursor (id, last_rowid, rows) VALUES ($id, $at, $rows)
                    ON CONFLICT(id) DO UPDATE SET last_rowid = excluded.last_rowid, rows = excluded.rows;
                    """;
                cur.Parameters.AddWithValue("$id", Id);
                cur.Parameters.AddWithValue("$at", hi);
                cur.Parameters.AddWithValue("$rows", total);
                cur.ExecuteNonQuery();
            }

            done = hi >= max;
            if (done)
            {
                using var mark = c.CreateCommand();
                mark.Transaction = tx;
                mark.CommandText = "INSERT INTO DataMigration (id, applied_at, rows) VALUES ($id, $at, $rows);";
                mark.Parameters.AddWithValue("$id", Id);
                mark.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                mark.Parameters.AddWithValue("$rows", total);
                mark.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // 每批把 WAL 落回主库，不让它一批批叠上去。有别的连接在读时做不完整，不报错，无妨。
        using (var ck = c.CreateCommand())
        {
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            ck.ExecuteNonQuery();
        }
        return new BarVolumeMigrationChunk(changed, hi, max, done);
    }

    /// <summary>rowid 落在 (lo, hi] 的行 volume ×100，返回改了几行。见类注释「换算」。</summary>
    private static long ScaleRange(SqliteConnection c, SqliteTransaction tx, long lo, long hi)
    {
        long changed;
        // ① 整数值：double 乘 100 精确
        using (var up = c.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = """
                UPDATE Bar SET volume = volume * 100
                WHERE rowid > $lo AND rowid <= $hi AND volume <> 0 AND volume = CAST(volume AS INTEGER);
                """;
            up.Parameters.AddWithValue("$lo", lo);
            up.Parameters.AddWithValue("$hi", hi);
            changed = up.ExecuteNonQuery();
        }

        // ② 带小数的：先读出来（关掉游标再写），逐行按 decimal 乘
        var frac = new List<(long RowId, double Volume)>();
        using (var sel = c.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT rowid, volume FROM Bar
                WHERE rowid > $lo AND rowid <= $hi AND volume <> CAST(volume AS INTEGER);
                """;
            sel.Parameters.AddWithValue("$lo", lo);
            sel.Parameters.AddWithValue("$hi", hi);
            using var r = sel.ExecuteReader();
            while (r.Read()) frac.Add((r.GetInt64(0), r.GetDouble(1)));
        }
        if (frac.Count > 0)
        {
            using var up = c.CreateCommand();
            up.Transaction = tx;
            up.CommandText = "UPDATE Bar SET volume = $v WHERE rowid = $id;";
            var pv = up.Parameters.Add("$v", SqliteType.Real);
            var pid = up.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (rowId, v) in frac)
            {
                pv.Value = UnitScale.Apply(v, 100m);
                pid.Value = rowId;
                up.ExecuteNonQuery();
            }
        }
        return changed + frac.Count;
    }

    private static (long Cursor, long Rows) ReadCursor(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_rowid, rows FROM DataMigrationCursor WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", Id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
    }

    private static long MaxRowId(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(rowid), 0) FROM Bar;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
