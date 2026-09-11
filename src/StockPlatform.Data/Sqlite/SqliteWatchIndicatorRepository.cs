using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// <c>StockWatchIndicator</c> 的 SQLite 实现。语义见 <see cref="IWatchIndicatorRepository"/>：
/// 替换只动 <c>origin='rule'</c> 那一半。
/// </summary>
public class SqliteWatchIndicatorRepository : IWatchIndicatorRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteWatchIndicatorRepository(string dbFilePath)
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

    public int ReplaceRuleLinks(IEnumerable<WatchIndicatorLink> links)
    {
        var list = links.ToList();
        if (list.Count == 0) return 0;      // 空集合是空操作：规则读失败时保留上一轮的

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // ⚠ 这个 WHERE 是整张表设计的关键。写成 DELETE FROM StockWatchIndicator 会把人手挂的
        // （origin='manual'）一起删掉，而且不报错、界面上看不出来。
        // 先删后写放同一个事务，中途出错整表回到原样。
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM StockWatchIndicator WHERE origin = $o;";
            del.Parameters.AddWithValue("$o", WatchIndicatorOrigin.Rule);
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // 主键是 (code, indicator_id)，跨 origin 唯一：同一条映射不该既是规则的又是手挂的。
            // 人手挂了之后规则又算出同一条时，DO NOTHING 让**手挂的那条留着**——
            // 它的 reason 是人写的，比规则生成的那句有信息量。
            cmd.CommandText = """
                INSERT INTO StockWatchIndicator
                    (code, indicator_id, weight, origin, reason, created_at)
                VALUES ($c, $i, $w, $o, $r, $at)
                ON CONFLICT(code, indicator_id) DO NOTHING;
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var n in new[] { "$c", "$i", "$w", "$o", "$r", "$at" })
            { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

            foreach (var x in list)
            {
                p["$c"].Value = x.Code;
                p["$i"].Value = x.IndicatorId;
                p["$w"].Value = x.Weight;
                p["$o"].Value = x.Origin;
                p["$r"].Value = x.Reason;
                p["$at"].Value = x.CreatedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return list.Count;
    }

    public List<WatchIndicatorLink> GetLinks(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, indicator_id, weight, origin, reason, created_at
            FROM StockWatchIndicator WHERE code = $c ORDER BY weight, indicator_id;
            """;
        cmd.Parameters.AddWithValue("$c", code);

        var list = new List<WatchIndicatorLink>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new WatchIndicatorLink(
                Code: r.GetString(0),
                IndicatorId: r.GetString(1),
                Weight: r.IsDBNull(2) ? 0 : r.GetInt32(2),
                Origin: r.IsDBNull(3) ? WatchIndicatorOrigin.Rule : r.GetString(3),
                Reason: r.IsDBNull(4) ? "" : r.GetString(4))
            {
                CreatedAt = r.IsDBNull(5) ? default
                    : DateTime.TryParse(r.GetString(5), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var t) ? t : default,
            });
        }
        return list;
    }

    public (int RuleLinks, int ManualLinks, int Stocks) GetCounts()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM StockWatchIndicator WHERE origin = 'rule'),
              (SELECT COUNT(*) FROM StockWatchIndicator WHERE origin = 'manual'),
              (SELECT COUNT(DISTINCT code) FROM StockWatchIndicator);
            """;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : (0, 0, 0);
    }
}
