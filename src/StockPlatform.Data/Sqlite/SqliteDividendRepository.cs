using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>分红送配本地存取（Dividend 表）——每次抓取返回该股全部历史，按 code "删旧写新"整体覆盖，
/// 仿 <see cref="SqliteShareholderRepository.ReplaceByCode"/> 的删+写事务。</summary>
public class SqliteDividendRepository : IDividendRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteDividendRepository(string dbFilePath)
    {
        _connectionString = $"Data Source={dbFilePath}";
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void EnsureSchema()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
    }

    public void ReplaceByCode(string code, List<DividendRow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Dividend WHERE code = $c;";
            del.Parameters.AddWithValue("$c", code);
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO Dividend
                    (code, announce_date, bonus_shares, transfer_shares, dividend_yuan, progress, record_date, ex_date, fetched_at)
                VALUES ($c, $ad, $bs, $ts, $dy, $pg, $rd, $ex, $at);
                """;
            var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
            var pad = cmd.CreateParameter(); pad.ParameterName = "$ad"; cmd.Parameters.Add(pad);
            var pbs = cmd.CreateParameter(); pbs.ParameterName = "$bs"; cmd.Parameters.Add(pbs);
            var pts = cmd.CreateParameter(); pts.ParameterName = "$ts"; cmd.Parameters.Add(pts);
            var pdy = cmd.CreateParameter(); pdy.ParameterName = "$dy"; cmd.Parameters.Add(pdy);
            var ppg = cmd.CreateParameter(); ppg.ParameterName = "$pg"; cmd.Parameters.Add(ppg);
            var prd = cmd.CreateParameter(); prd.ParameterName = "$rd"; cmd.Parameters.Add(prd);
            var pex = cmd.CreateParameter(); pex.ParameterName = "$ex"; cmd.Parameters.Add(pex);
            var pat = cmd.CreateParameter(); pat.ParameterName = "$at"; cmd.Parameters.Add(pat);
            foreach (var r in rows)
            {
                pc.Value = r.Code;
                pad.Value = r.AnnounceDate.ToString(DateFormat, CultureInfo.InvariantCulture);
                pbs.Value = r.BonusShares;
                pts.Value = r.TransferShares;
                pdy.Value = r.DividendYuan;
                ppg.Value = (object?)r.Progress ?? DBNull.Value;
                prd.Value = r.RecordDate.HasValue ? r.RecordDate.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value;
                pex.Value = r.ExDate.HasValue ? r.ExDate.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value;
                pat.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public List<DividendRow> GetByCode(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, announce_date, bonus_shares, transfer_shares, dividend_yuan, progress, record_date, ex_date, fetched_at, source
            FROM Dividend WHERE code = $c ORDER BY announce_date;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        var result = new List<DividendRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DividendRow
            {
                Code = reader.GetString(0),
                AnnounceDate = DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture),
                BonusShares = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                TransferShares = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                DividendYuan = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                Progress = reader.IsDBNull(5) ? null : reader.GetString(5),
                RecordDate = reader.IsDBNull(6) ? null : DateTime.ParseExact(reader.GetString(6), DateFormat, CultureInfo.InvariantCulture),
                ExDate = reader.IsDBNull(7) ? null : DateTime.ParseExact(reader.GetString(7), DateFormat, CultureInfo.InvariantCulture),
                FetchedAt = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(8), TimeFormat, CultureInfo.InvariantCulture),
                Source = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return result;
    }

    /// <summary>
    /// "最近12个月已实施的现金派息"的筛选条件。全市场版和单只版共用一份文本——两处各写各的话，
    /// 哪天调了口径只改一处，同一只票的股息率在列表页和详情页就会对不上，而且谁都不会报错。
    /// </summary>
    private const string TrailingCashWhere =
        "progress = '实施' AND dividend_yuan > 0 AND ex_date IS NOT NULL AND ex_date >= $since";

    public Dictionary<string, double> GetTrailingCashDividendPerShare(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 表里 dividend_yuan 是"每10股派X元"（数据源口径，见 DividendRow），/10 换成每股。
        cmd.CommandText = $"""
            SELECT code, SUM(dividend_yuan) / 10.0
            FROM Dividend
            WHERE {TrailingCashWhere}
            GROUP BY code;
            """;
        cmd.Parameters.AddWithValue("$since", since.ToString(DateFormat, CultureInfo.InvariantCulture));
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            result[reader.GetString(0)] = reader.GetDouble(1);
        }
        return result;
    }

    public double GetTrailingCashDividendPerShare(string code, DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 多加一个 code = $code 就走主键前缀，不再扫全表（批量版那条是全表 GROUP BY）。
        cmd.CommandText = $"""
            SELECT SUM(dividend_yuan) / 10.0
            FROM Dividend
            WHERE code = $code AND {TrailingCashWhere};
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$since", since.ToString(DateFormat, CultureInfo.InvariantCulture));
        // 一行都没匹配上时 SUM 返回 NULL（不是 0），所以这里必须判 DBNull
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    public Dictionary<string, List<(int Year, double PerShare)>> GetAnnualCashDividendPerShare(DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 按除权除息日所属年分组（钱到账那年），同年多次派息合并；/10 换成每股，同
        // GetTrailingCashDividendPerShare。ex_date 是 'yyyy-MM-dd' 文本，取前4位当年份比
        // strftime 快且不依赖 SQLite 的日期函数。
        cmd.CommandText = """
            SELECT code, CAST(substr(ex_date, 1, 4) AS INTEGER) AS y, SUM(dividend_yuan) / 10.0
            FROM Dividend
            WHERE progress = '实施' AND dividend_yuan > 0 AND ex_date IS NOT NULL AND ex_date >= $since
            GROUP BY code, y
            ORDER BY code, y;
            """;
        cmd.Parameters.AddWithValue("$since", since.ToString(DateFormat, CultureInfo.InvariantCulture));
        var result = new Dictionary<string, List<(int Year, double PerShare)>>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
            var code = reader.GetString(0);
            if (!result.TryGetValue(code, out var list))
            {
                list = new List<(int Year, double PerShare)>();
                result[code] = list;
            }
            list.Add((reader.GetInt32(1), reader.GetDouble(2)));
        }
        return result;
    }

    public int GetCodeCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT code) FROM Dividend;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public Dictionary<string, DividendFetchState> GetFetchStates()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, last_ok_at, dividend_rows, rights_rows, last_fail_at, fail_reason
            FROM DividendFetchState;
            """;
        var result = new Dictionary<string, DividendFetchState>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var code = r.GetString(0);
            result[code] = new DividendFetchState
            {
                Code = code,
                LastOkAt = ReadTime(r, 1),
                DividendRows = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                RightsRows = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                LastFailAt = ReadTime(r, 4),
                FailReason = r.IsDBNull(5) ? null : r.GetString(5),
            };
        }
        return result;
    }

    public Dictionary<string, HashSet<DateTime>> GetImplementedExDates()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, ex_date FROM Dividend
            WHERE progress = '实施' AND ex_date IS NOT NULL AND ex_date <> '';
            """;
        var result = new Dictionary<string, HashSet<DateTime>>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var d)) continue;
            if (!result.TryGetValue(r.GetString(0), out var set))
                result[r.GetString(0)] = set = new HashSet<DateTime>();
            set.Add(d.Date);
        }
        return result;
    }

    public int InsertMissing(IReadOnlyList<DividendRow> rows)
    {
        if (rows.Count == 0) return 0;
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();
        int n = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // OR IGNORE：主键撞上就原样不动。判重在调用方按除权日做过了（见
            // GetImplementedExDates 的注释），这里只是最后一道不覆盖的保险。
            cmd.CommandText = """
                INSERT OR IGNORE INTO Dividend
                    (code, announce_date, bonus_shares, transfer_shares, dividend_yuan,
                     progress, record_date, ex_date, fetched_at, source)
                VALUES ($c, $ad, $bs, $ts, $dy, $pg, $rd, $ex, $at, $src);
                """;
            var ps = new[] { "$c","$ad","$bs","$ts","$dy","$pg","$rd","$ex","$at","$src" }
                .Select(n2 => { var p = cmd.CreateParameter(); p.ParameterName = n2; cmd.Parameters.Add(p); return p; })
                .ToArray();
            foreach (var r in rows)
            {
                ps[0].Value = r.Code;
                ps[1].Value = r.AnnounceDate.ToString(DateFormat, CultureInfo.InvariantCulture);
                ps[2].Value = r.BonusShares;
                ps[3].Value = r.TransferShares;
                ps[4].Value = r.DividendYuan;
                ps[5].Value = (object?)r.Progress ?? DBNull.Value;
                ps[6].Value = r.RecordDate.HasValue ? r.RecordDate.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value;
                ps[7].Value = r.ExDate.HasValue ? r.ExDate.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value;
                ps[8].Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                ps[9].Value = (object?)r.Source ?? DBNull.Value;
                int wrote = cmd.ExecuteNonQuery();

                // 撞主键了（wrote==0）：判重是按**除权日**过的，所以这条在库里确实还没有，
                // 是它的公告日恰好跟另一条撞上了——东财会给出"两条不同除权日、预案公告日相同"
                // 的记录。直接 IGNORE 掉的话，这条缺口每轮都会被判成缺、每轮被吞，
                // 日志还每轮报一次假的成功数，**永远收敛不了**（2026-09-18 实机跑出来的，139 条）。
                // 换成用除权日当公告日再试一次：除权日在库里本来就不存在（判重刚验过），
                // 撞第二次的概率极低，真撞了就在返回值里体现出来，由调用方报差额。
                if (wrote == 0 && r.ExDate is { } ex && r.AnnounceDate.Date != ex.Date)
                {
                    ps[1].Value = ex.ToString(DateFormat, CultureInfo.InvariantCulture);
                    wrote = cmd.ExecuteNonQuery();
                }
                n += wrote;
            }
        }
        tx.Commit();
        return n;
    }

    public int SeedFetchStatesFromDividends()
    {
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);

        // 只在整张表还空着的时候播种。非空＝新版已经跑过，那时的状态是真的抓取记录，
        // 绝不能被这份近似值盖掉。
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT EXISTS(SELECT 1 FROM DividendFetchState);";
            if (Convert.ToInt32(probe.ExecuteScalar()) != 0) return 0;
        }

        using var tx = conn.BeginTransaction();
        int seeded;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // fetched_at 早于 1990 的丢掉——库里有 323 只老 code 记的是 DateTime.MinValue
            // （'0001-01-01…'，早期数据没记时刻）。那不是"抓过"，播成水位线会让它们永远不抓。
            cmd.CommandText = """
                INSERT OR IGNORE INTO DividendFetchState
                    (code, last_ok_at, dividend_rows, rights_rows)
                SELECT d.code,
                       MAX(d.fetched_at),
                       COUNT(*),
                       COALESCE((SELECT COUNT(*) FROM RightsIssue r WHERE r.code = d.code), 0)
                FROM Dividend d
                WHERE d.fetched_at IS NOT NULL AND d.fetched_at >= '1990'
                GROUP BY d.code;
                """;
            seeded = cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return seeded;
    }

    public void SaveFetchStates(IReadOnlyList<DividendFetchState> states)
    {
        if (states.Count == 0) return;
        using var conn = Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // 整条覆盖：调用方持有的就是完整状态（失败那只带着原来的 last_ok_at），
            // 这里再做字段级合并只会多一处判据、两处不一致。
            cmd.CommandText = """
                INSERT INTO DividendFetchState
                    (code, last_ok_at, dividend_rows, rights_rows, last_fail_at, fail_reason)
                VALUES ($c, $ok, $dr, $rr, $fa, $fr)
                ON CONFLICT(code) DO UPDATE SET
                    last_ok_at = excluded.last_ok_at,
                    dividend_rows = excluded.dividend_rows,
                    rights_rows = excluded.rights_rows,
                    last_fail_at = excluded.last_fail_at,
                    fail_reason = excluded.fail_reason;
                """;
            var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
            var pok = cmd.CreateParameter(); pok.ParameterName = "$ok"; cmd.Parameters.Add(pok);
            var pdr = cmd.CreateParameter(); pdr.ParameterName = "$dr"; cmd.Parameters.Add(pdr);
            var prr = cmd.CreateParameter(); prr.ParameterName = "$rr"; cmd.Parameters.Add(prr);
            var pfa = cmd.CreateParameter(); pfa.ParameterName = "$fa"; cmd.Parameters.Add(pfa);
            var pfr = cmd.CreateParameter(); pfr.ParameterName = "$fr"; cmd.Parameters.Add(pfr);
            foreach (var st in states)
            {
                pc.Value = st.Code;
                pok.Value = WriteTime(st.LastOkAt);
                pdr.Value = st.DividendRows;
                prr.Value = st.RightsRows;
                pfa.Value = WriteTime(st.LastFailAt);
                pfr.Value = (object?)st.FailReason ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    private static object WriteTime(DateTime? t) =>
        t.HasValue ? t.Value.ToString(TimeFormat, CultureInfo.InvariantCulture) : DBNull.Value;

    private static DateTime? ReadTime(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var s = r.GetString(i);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
