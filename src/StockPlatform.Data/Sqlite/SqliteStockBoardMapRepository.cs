using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 个股行业/题材归属的本地存取（2026-09-03）。
///
/// 这两张表是<b>当下快照</b>（接口没有时间维度），所以是整体替换语义而不是增量累积。
/// 但跟板块表一样有条铁律：<b>空集合是空操作</b>——抓取失败时保留库里上一次的，
/// 绝不能因为这轮拿回来个空的就把行业分类清空（那会让所有依赖行业的分析当场失效）。
/// </summary>
public class SqliteStockBoardMapRepository : IStockBoardMapRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteStockBoardMapRepository(string dbFilePath)
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

    /// <summary>
    /// 清空两张表，供一次全量重取之前调用。
    /// <b>只有在确认新数据拿得到之后才该调它</b>——调用方（编排层）是在第一批数据到手时
    /// 才清的，不是一进来就清，否则接口一挂就把行业分类清空了。
    /// </summary>
    public void ClearAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM StockIndustryEm; DELETE FROM StockThemeEm;";
        cmd.ExecuteNonQuery();
    }

    public int UpsertIndustries(IEnumerable<StockIndustryEm> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO StockIndustryEm (code, board_code, board_name, board_level, fetched_at)
            VALUES ($c, $b, $n, $l, $f);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$c", "$b", "$n", "$l", "$f" })
        { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

        foreach (var x in list)
        {
            p["$c"].Value = x.Code; p["$b"].Value = x.BoardCode; p["$n"].Value = x.BoardName;
            p["$l"].Value = (object?)x.BoardLevel ?? DBNull.Value;
            p["$f"].Value = x.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public int UpsertThemes(IEnumerable<StockThemeEm> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO StockThemeEm
                (code, board_code, board_name, is_precise, board_rank, reason, fetched_at)
            VALUES ($c, $b, $n, $p, $r, $reason, $f);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$c", "$b", "$n", "$p", "$r", "$reason", "$f" })
        { var q = cmd.CreateParameter(); q.ParameterName = n; cmd.Parameters.Add(q); p[n] = q; }

        foreach (var x in list)
        {
            p["$c"].Value = x.Code; p["$b"].Value = x.BoardCode; p["$n"].Value = x.BoardName;
            p["$p"].Value = x.IsPrecise ? 1 : 0;
            p["$r"].Value = (object?)x.BoardRank ?? DBNull.Value;
            p["$reason"].Value = x.Reason;
            p["$f"].Value = x.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    public int CountIndustries() => Scalar("SELECT COUNT(*) FROM StockIndustryEm");
    public int CountThemes() => Scalar("SELECT COUNT(*) FROM StockThemeEm");
    public int CountIndustryStocks() => Scalar("SELECT COUNT(DISTINCT code) FROM StockIndustryEm");

    /// <summary>
    /// 每只股票的**最细**行业（board_level 最大的那一条）。行业中性化、因子分组用这个。
    /// 东财覆盖不到的股票不在返回里——调用方应退回证监会分类（<c>StockIndustry</c>）兜底。
    /// </summary>
    public Dictionary<string, (string BoardCode, string BoardName)> GetFinestIndustryByStock()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, board_code, board_name FROM StockIndustryEm e
            WHERE board_level = (SELECT MAX(board_level) FROM StockIndustryEm x WHERE x.code = e.code)
            GROUP BY code;
            """;
        var map = new Dictionary<string, (string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2));
        return map;
    }

    public List<(string Code, string BoardCode)> GetAllIndustryLinks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, board_code FROM StockIndustryEm;";
        var list = new List<(string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public Dictionary<string, (string? Parent, int Level)> GetBoardParents()
    {
        var map = new Dictionary<string, (string?, int)>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();

        // ① 每个板块自己的层级。一级板块只在这一步出现——它从不作为"子"。
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT board_code, MAX(board_level) FROM StockIndustryEm
                WHERE board_level IS NOT NULL GROUP BY board_code;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = (null, r.GetInt32(1));
        }

        // ② 父子链：同一只股票、层级差 1 的两行就是一条边。按 (子,父) 数票数，
        //    取票最多的那个当父——数据干净时每个子只有一个候选（实测 0 个多父冲突），
        //    真出现分歧时也有个确定的结果，而不是"看哪行先读到"。
        var votes = new Dictionary<string, (string Parent, int Votes)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.board_code, p.board_code, COUNT(*)
                FROM StockIndustryEm c
                JOIN StockIndustryEm p ON p.code = c.code AND p.board_level = c.board_level - 1
                GROUP BY c.board_code, p.board_code;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var (child, parent, n) = (r.GetString(0), r.GetString(1), r.GetInt32(2));
                if (!votes.TryGetValue(child, out var cur) || n > cur.Votes)
                    votes[child] = (parent, n);
            }
        }

        foreach (var (child, v) in votes)
            if (map.TryGetValue(child, out var e)) map[child] = (v.Parent, e.Item2);

        return map;
    }

    private int Scalar(string sql)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
