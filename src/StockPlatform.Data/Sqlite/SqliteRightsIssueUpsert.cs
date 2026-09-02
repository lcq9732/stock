using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 配股表的读写（2026-09-01 新增）。
///
/// 写成**静态类 + 传库路径**而不是 IRepository + 依赖注入，是跟 <see cref="SqliteStockMetaUpsert"/>
/// 一致的做法：配股跟分红是同一次抓取里顺带拿到的（同一张网页的两张表），没有独立的抓取入口，
/// 也就不需要在 App 启动时注册一个 provider/repository 对，多一层注入只是多一处要改的地方。
/// </summary>
public static class SqliteRightsIssueUpsert
{
    /// <summary>整只股票覆盖写——同 SqliteDividendRepository.ReplaceByCode 的口径：
    /// 源页面每次返回该股全部历史方案，删旧写新最简单，也能顺带清掉源那边撤销过的记录。</summary>
    public static int ReplaceByCode(string dbPath, string code, IReadOnlyList<RightsIssueRow> rows)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM RightsIssue WHERE code = $c;";
            del.Parameters.AddWithValue("$c", code);
            del.ExecuteNonQuery();
        }

        int n = 0;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO RightsIssue
                    (code, announce_date, shares_per_10, price, ex_date, record_date, fetched_at)
                VALUES ($c, $a, $s, $p, $e, $r, $f);
                """;
            foreach (var row in rows)
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("$c", row.Code);
                ins.Parameters.AddWithValue("$a", row.AnnounceDate.ToString("yyyy-MM-dd"));
                ins.Parameters.AddWithValue("$s", row.SharesPer10);
                ins.Parameters.AddWithValue("$p", row.Price);
                ins.Parameters.AddWithValue("$e", (object?)row.ExDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
                ins.Parameters.AddWithValue("$r", (object?)row.RecordDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
                ins.Parameters.AddWithValue("$f", row.FetchedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                n += ins.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return n;
    }

    /// <summary>取一只股票**已实施**（有除权日）的配股，按除权日升序——复权计算只关心这些。</summary>
    public static List<RightsIssueRow> GetByCode(SqliteConnection conn, string code)
    {
        var list = new List<RightsIssueRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT announce_date, shares_per_10, price, ex_date, record_date
            FROM RightsIssue WHERE code = $c AND ex_date IS NOT NULL
            ORDER BY ex_date;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RightsIssueRow
            {
                Code = code,
                AnnounceDate = DateTime.TryParse(r.GetString(0), out var a) ? a : default,
                SharesPer10 = r.IsDBNull(1) ? 0 : r.GetDouble(1),
                Price = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                ExDate = r.IsDBNull(3) ? null : DateTime.TryParse(r.GetString(3), out var e) ? e : null,
                RecordDate = r.IsDBNull(4) ? null : DateTime.TryParse(r.GetString(4), out var rd) ? rd : null,
            });
        }
        return list;
    }

    /// <summary>一次把全库配股读进内存（code → 该股的配股列表）——重算 day_adj 时要逐只取，
    /// 5500 次单独查询不如一次扫完：全市场配股记录总共也就几千条。</summary>
    public static Dictionary<string, List<RightsIssueRow>> LoadAll(SqliteConnection conn)
    {
        var map = new Dictionary<string, List<RightsIssueRow>>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, announce_date, shares_per_10, price, ex_date, record_date
            FROM RightsIssue WHERE ex_date IS NOT NULL ORDER BY code, ex_date;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var code = r.GetString(0);
            if (!map.TryGetValue(code, out var list)) map[code] = list = [];
            list.Add(new RightsIssueRow
            {
                Code = code,
                AnnounceDate = DateTime.TryParse(r.GetString(1), out var a) ? a : default,
                SharesPer10 = r.IsDBNull(2) ? 0 : r.GetDouble(2),
                Price = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                ExDate = r.IsDBNull(4) ? null : DateTime.TryParse(r.GetString(4), out var e) ? e : null,
                RecordDate = r.IsDBNull(5) ? null : DateTime.TryParse(r.GetString(5), out var rd) ? rd : null,
            });
        }
        return map;
    }
}
