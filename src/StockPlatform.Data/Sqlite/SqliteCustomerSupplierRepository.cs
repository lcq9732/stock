using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 前五大客户/供应商的本地存取（2026-09-07）。累积语义，**全类没有一句 DELETE**——
/// 数据按报告期一期一期出，老报告期的行永远有效。
/// </summary>
public class SqliteCustomerSupplierRepository : ICustomerSupplierRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteCustomerSupplierRepository(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath}";

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

    public int Upsert(IEnumerable<CustomerSupplier> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO StockCustomerSupplier
                (code, report_date, is_supplier, rank, partner_name, amount, pct,
                 total_amount, report_name, fetched_at)
            VALUES ($c, $d, $s, $r, $p, $a, $pct, $t, $rn, $at)
            ON CONFLICT(code, report_date, is_supplier, rank) DO UPDATE SET
                partner_name = excluded.partner_name,
                amount       = excluded.amount,
                pct          = excluded.pct,
                total_amount = excluded.total_amount,
                report_name  = excluded.report_name,
                fetched_at   = excluded.fetched_at;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$c", "$d", "$s", "$r", "$p", "$a", "$pct", "$t", "$rn", "$at" })
        { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

        foreach (var x in list)
        {
            p["$c"].Value = x.Code;
            p["$d"].Value = x.ReportDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$s"].Value = x.IsSupplier ? 1 : 0;
            p["$r"].Value = x.Rank;
            p["$p"].Value = x.PartnerName;
            p["$a"].Value = x.Amount;
            p["$pct"].Value = x.Pct;
            p["$t"].Value = x.TotalAmount;
            p["$rn"].Value = x.ReportName;
            p["$at"].Value = x.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public void SaveYearState(int year, int reported, int saved, int skipped)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CustSuppYearState (year, reported, saved, skipped, updated_at)
            VALUES ($y, $r, $s, $k, $at)
            ON CONFLICT(year) DO UPDATE SET
                reported = excluded.reported, saved = excluded.saved,
                skipped = excluded.skipped, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$y", year);
        cmd.Parameters.AddWithValue("$r", reported);
        cmd.Parameters.AddWithValue("$s", saved);
        cmd.Parameters.AddWithValue("$k", skipped);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public Dictionary<int, (int Reported, int Saved, int Skipped)> GetYearStates()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // COALESCE：老库迁移上来的行 skipped 是 NULL（那时还没这一列）
        cmd.CommandText = "SELECT year, reported, saved, COALESCE(skipped, 0) FROM CustSuppYearState;";
        var map = new Dictionary<int, (int, int, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt32(0)] = (r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
        return map;
    }

    public List<string> GetPartnerNamesToMatch(bool all = false)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 默认只取还没匹配过的：全量重扫 76 万行没必要，规则改了才用 all=true。
        cmd.CommandText = all
            ? "SELECT DISTINCT partner_name FROM StockCustomerSupplier WHERE partner_name IS NOT NULL AND partner_name <> '';"
            : "SELECT DISTINCT partner_name FROM StockCustomerSupplier WHERE partner_code IS NULL AND partner_name IS NOT NULL AND partner_name <> '';";
        var list = new List<string>(50000);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>一个事务里最多写多少条 UPDATE。见下面 WAL 那段注释。</summary>
    private const int ApplyBatchSize = 2000;

    public int ApplyMatches(IReadOnlyDictionary<string, (string Code, string MatchType)> matches,
                            IReadOnlyCollection<string>? evaluated = null)
    {
        // ⚠ 空字典是空操作：档案没拉到时保留上次的匹配。
        //   但 evaluated 给了就不能这么早退——那是"全量重匹算完了，只是一个都没命中"，
        //   跟"档案没拉到"是两回事。后者才该保留旧值。
        if (matches.Count == 0 && evaluated == null) return 0;

        using var conn = Open();

        // 写：只改**真的变了**的行。加上 IS NOT 这两个条件之后，全量重匹 11 万个名字时
        // 绝大多数名字的结果跟上次一样，一行都不会被重写——WAL 只记真正的改动。
        // 这个库有过单事务写 468 万行把 WAL 撑到 162GB 的前科（板块指数合成），不能不防。
        const string SqlWrite = """
            UPDATE StockCustomerSupplier SET partner_code = $c, match_type = $t
            WHERE partner_name = $n AND (partner_code IS NOT $c OR match_type IS NOT $t);
            """;
        // 清：评估过但没命中的，撤回上一次的结论。判据改版后"不再认识"也是一种结论。
        const string SqlClear = """
            UPDATE StockCustomerSupplier SET partner_code = NULL, match_type = NULL
            WHERE partner_name = $n AND partner_code IS NOT NULL;
            """;

        int rows = 0, inBatch = 0;
        var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = SqlWrite;
                var pn = cmd.CreateParameter(); pn.ParameterName = "$n"; cmd.Parameters.Add(pn);
                var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
                var pt = cmd.CreateParameter(); pt.ParameterName = "$t"; cmd.Parameters.Add(pt);

                // 按名字整批更新：同一个对手名在多家公司、多个年份的行里重复出现，一条 UPDATE 全覆盖
                foreach (var (name, (code, type)) in matches)
                {
                    pn.Value = name; pc.Value = code; pt.Value = type;
                    rows += cmd.ExecuteNonQuery();
                    if (++inBatch < ApplyBatchSize) continue;
                    tx.Commit(); tx.Dispose(); tx = conn.BeginTransaction();
                    cmd.Transaction = tx; inBatch = 0;
                }
            }

            if (evaluated != null)
            {
                using var clr = conn.CreateCommand();
                clr.Transaction = tx;
                clr.CommandText = SqlClear;
                var qn = clr.CreateParameter(); qn.ParameterName = "$n"; clr.Parameters.Add(qn);

                foreach (var name in evaluated)
                {
                    if (matches.ContainsKey(name)) continue;
                    qn.Value = name;
                    rows += clr.ExecuteNonQuery();
                    if (++inBatch < ApplyBatchSize) continue;
                    tx.Commit(); tx.Dispose(); tx = conn.BeginTransaction();
                    clr.Transaction = tx; inBatch = 0;
                }
            }

            tx.Commit();
        }
        finally { tx.Dispose(); }
        return rows;
    }

    public List<string> GetMostReferencedPartners(int top)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 只数可信档：normalized 可能只是写法像，parent_group 指的是非上市母集团——
        // 拿它们排序会把下载清单带偏。
        cmd.CommandText = """
            SELECT partner_code FROM StockCustomerSupplier
            WHERE rank <= 5 AND partner_code IS NOT NULL
              AND match_type IN ('exact', 'short', 'qualified')
            GROUP BY partner_code ORDER BY COUNT(*) DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", top);
        var list = new List<string>(top);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public int GetMatcherVersion()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT matcher_version FROM CustSuppMatchState WHERE id = 1;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void SetMatcherVersion(int version)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CustSuppMatchState (id, matcher_version, matched_at) VALUES (1, $v, $t)
            ON CONFLICT(id) DO UPDATE SET matcher_version = $v, matched_at = $t;
            """;
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public (int Matched, int Unmatched, int Anonymous) GetMatchStats()
    {
        using var conn = Open();
        int N(string where)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM StockCustomerSupplier WHERE " + where;
            return Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
        // 匿名的判据在 PartnerNameMatcher 里，SQL 这边只做个粗略口径：
        // 「第N名」「其余客户/供应商」「客户N」这几种占了绝大多数
        const string anon = "(partner_name LIKE '第%名' OR partner_name LIKE '其余%' "
                          + "OR partner_name LIKE '客户_' OR partner_name LIKE '供应商_')";
        return (N("partner_code IS NOT NULL"),
                N($"partner_code IS NULL AND NOT {anon}"),
                N($"partner_code IS NULL AND {anon}"));
    }

    public int CountByYear(int year)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM StockCustomerSupplier WHERE substr(report_date,1,4) = $y;";
        cmd.Parameters.AddWithValue("$y", year.ToString(CultureInfo.InvariantCulture));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public (int Rows, int Stocks, DateTime? First, DateTime? Last) GetStats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT code), MIN(report_date), MAX(report_date)
            FROM StockCustomerSupplier;
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0, null, null);
        DateTime? D(int i) => r.IsDBNull(i) || !DateTime.TryParse(r.GetString(i),
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? null : d;
        return (r.GetInt32(0), r.GetInt32(1), D(2), D(3));
    }
}
