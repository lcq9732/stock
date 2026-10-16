using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 新股发行表 IpoIssue 的本地存取（表结构按 <see cref="IpoIssueColumns"/> 生成，见 <see cref="SqliteSchema"/>）。
/// 写入语句同样按列目录拼——151 列手写一遍，漏一列不会报错、只会静默少存。
/// </summary>
public sealed class SqliteIpoIssueRepository(string dbFilePath) : IIpoIssueRepository
{
    private readonly string _connectionString = $"Data Source={dbFilePath}";

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

    public void Upsert(IReadOnlyList<IpoIssueRow> rows)
    {
        if (rows.Count == 0) return;
        var cols = IpoIssueColumns.All.Where(c => c.Api != IpoIssueColumns.CodeColumn).ToList();

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO IpoIssue (code, {string.Join(", ", cols.Select(c => c.Db))}, fetched_at) "
            + $"VALUES ($code, {string.Join(", ", cols.Select((_, i) => $"$p{i}"))}, $at) "
            + "ON CONFLICT(code) DO UPDATE SET "
            + string.Join(", ", cols.Select(c => $"{c.Db} = excluded.{c.Db}"))
            + ", fetched_at = excluded.fetched_at;";

        var pCode = cmd.Parameters.Add("$code", SqliteType.Text);
        var ps = cols.Select((c, i) => cmd.Parameters.Add($"$p{i}",
            c.Kind == IpoColumnKind.Number ? SqliteType.Real : SqliteType.Text)).ToList();
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        foreach (var r in rows)
        {
            pCode.Value = r.Code;
            for (int i = 0; i < cols.Count; i++) ps[i].Value = r[cols[i].Api] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public int UpdateListDates(IReadOnlyList<IpoIssueRow> rows)
    {
        var dated = rows.Where(r => r.ListingDate is not null).ToList();
        if (dated.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // 只改名册里已有的个股（含退市）：不新建名册行——名册归【股票名册】管；
        // 值没变的不写，返回的就是"这一轮真正改了几只"。
        cmd.CommandText = """
            UPDATE StockMeta SET list_date = $d
            WHERE code = $c AND type IN ('stock', 'delisted') AND (list_date IS NULL OR list_date <> $d);
            """;
        var pc = cmd.Parameters.Add("$c", SqliteType.Text);
        var pd = cmd.Parameters.Add("$d", SqliteType.Text);
        int n = 0;
        foreach (var r in dated)
        {
            pc.Value = r.Code;
            pd.Value = r.ListingDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>读一只票的全部列（东财原名 → 值）。给测试和以后的查询用；没有返回 null。</summary>
    public Dictionary<string, object?>? Get(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM IpoIssue WHERE code = $c;";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var byDb = Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => r.IsDBNull(i) ? null : r.GetValue(i));
        return IpoIssueColumns.All.ToDictionary(c => c.Api, c => byDb.GetValueOrDefault(c.Db), StringComparer.Ordinal);
    }
}
