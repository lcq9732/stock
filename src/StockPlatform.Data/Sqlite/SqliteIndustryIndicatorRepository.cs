using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 行业景气指标的本地存取（2026-09-07）。语义分界见 <see cref="IIndustryIndicatorRepository"/>：
/// 目录是快照、序列是累积。
/// </summary>
public class SqliteIndustryIndicatorRepository : IIndustryIndicatorRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public SqliteIndustryIndicatorRepository(string dbFilePath)
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

    public int UpsertIndicators(IEnumerable<IndustryIndicator> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;      // 空集合是空操作，别把字典清了

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO IndustryIndicator
                (indicator_id, name, orig_name, unit, frequency, granularity, chart_type, source, fetched_at)
            VALUES ($id, $n, $on, $u, $f, $g, $c, $s, $at)
            ON CONFLICT(indicator_id) DO UPDATE SET
                name = excluded.name, orig_name = excluded.orig_name, unit = excluded.unit,
                frequency = excluded.frequency, granularity = excluded.granularity,
                chart_type = excluded.chart_type, source = excluded.source,
                fetched_at = excluded.fetched_at;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$id", "$n", "$on", "$u", "$f", "$g", "$c", "$s", "$at" })
        { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

        foreach (var x in list)
        {
            p["$id"].Value = x.IndicatorId;
            p["$n"].Value = x.Name;
            p["$on"].Value = x.OrigName;
            p["$u"].Value = x.Unit;
            p["$f"].Value = x.Frequency;
            p["$g"].Value = x.Granularity;
            p["$c"].Value = x.ChartType;
            p["$s"].Value = x.Source;
            p["$at"].Value = x.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public int ReplaceLinks(IEnumerable<StockIndicatorLink> links)
    {
        var list = links.ToList();
        if (list.Count == 0) return 0;      // ⚠ 空集合绝不清空，跟板块快照同一条铁律

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 快照替换：本批里没有的旧关联要清掉（指标下架、股票不再关联）。
        // 先删后写放同一个事务里，中途出错整张表回到原样。
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM StockIndustryIndicator;";
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO StockIndustryIndicator (code, indicator_id, indicator_order, fetched_at)
                VALUES ($c, $i, $o, $at)
                ON CONFLICT(code, indicator_id) DO UPDATE SET
                    indicator_order = excluded.indicator_order, fetched_at = excluded.fetched_at;
                """;
            var p = new Dictionary<string, SqliteParameter>();
            foreach (var n in new[] { "$c", "$i", "$o", "$at" })
            { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

            var now = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture);
            foreach (var x in list)
            {
                p["$c"].Value = x.Code;
                p["$i"].Value = x.IndicatorId;
                p["$o"].Value = (object?)x.Order ?? DBNull.Value;
                p["$at"].Value = now;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return list.Count;
    }

    public int UpsertPoints(IEnumerable<IndicatorPoint> points)
    {
        var list = points.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 累积语义：只写不删。某个指标这轮没抓到，库里上一轮的序列照样有效。
        cmd.CommandText = """
            INSERT INTO IndustryIndicatorValue (indicator_id, trade_date, value, yoy_pct)
            VALUES ($i, $d, $v, $y)
            ON CONFLICT(indicator_id, trade_date) DO UPDATE SET
                value = excluded.value, yoy_pct = excluded.yoy_pct;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$i", "$d", "$v", "$y" })
        { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

        foreach (var x in list)
        {
            p["$i"].Value = x.IndicatorId;
            p["$d"].Value = x.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$v"].Value = x.Value;
            p["$y"].Value = (object?)x.YoyPct ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public List<IndustryIndicator> GetIndicators()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT indicator_id, name, orig_name, unit, frequency, granularity, chart_type, source
            FROM IndustryIndicator ORDER BY indicator_id;
            """;
        var list = new List<IndustryIndicator>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new IndustryIndicator
            {
                IndicatorId = r.GetString(0),
                Name = r.IsDBNull(1) ? "" : r.GetString(1),
                OrigName = r.IsDBNull(2) ? "" : r.GetString(2),
                Unit = r.IsDBNull(3) ? "" : r.GetString(3),
                Frequency = r.IsDBNull(4) ? "" : r.GetString(4),
                Granularity = r.IsDBNull(5) ? "" : r.GetString(5),
                ChartType = r.IsDBNull(6) ? "" : r.GetString(6),
                Source = r.IsDBNull(7) ? "" : r.GetString(7),
            });
        return list;
    }

    public Dictionary<string, DateTime> GetLatestDates()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT indicator_id, MAX(trade_date) FROM IndustryIndicatorValue GROUP BY indicator_id;";
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(1) && DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture,
                                                    DateTimeStyles.None, out var d))
                map[r.GetString(0)] = d;
        return map;
    }

    public Dictionary<string, string> GetRepresentativeStocks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // MIN(code) 就是"按 code 排序取第一只"。选谁都行（同一指标各股票的值完全一样），
        // 但必须稳定——每轮换一只的话，日志对不上、出了问题没法复现。
        cmd.CommandText = """
            SELECT indicator_id, MIN(code) FROM StockIndustryIndicator GROUP BY indicator_id;
            """;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(1)) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public (int Indicators, int Points, int Links) GetCounts()
    {
        using var conn = Open();
        int N(string sql)
        {
            using var c = conn.CreateCommand();
            c.CommandText = sql;
            return Convert.ToInt32(c.ExecuteScalar() ?? 0);
        }
        return (N("SELECT COUNT(*) FROM IndustryIndicator"),
                N("SELECT COUNT(*) FROM IndustryIndicatorValue"),
                N("SELECT COUNT(*) FROM StockIndustryIndicator"));
    }
}
