using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 【板块指数合成】的合成状态（2026-09-21）——只读写，判据在
/// <see cref="BoardIndexIncrementalRule"/>（Logic 层纯函数）。
///
/// ⚠ 这张表**丢了也不会错**：读不到状态就当没合成过、整段重算，只是慢一轮。
/// 它是性能优化的记账，不是数据本身。
/// </summary>
public sealed class SqliteBoardIndexStateRepository(string dbFilePath)
{
    private const string DateFormat = "yyyy-MM-dd";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbFilePath}");
        conn.Open();
        SqliteSchema.EnsureSchema(conn);
        return conn;
    }

    /// <summary>全部板块的合成状态，按板块代码索引。</summary>
    public Dictionary<string, BoardIndexState> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT board_code, member_hash, last_bar_date, member_earliest FROM BoardIndexState;";
        var result = new Dictionary<string, BoardIndexState>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;   // 半截的行按"没记过"处理
            if (!DateTime.TryParse(reader.GetString(2), out var last)) continue;
            DateTime? earliest = reader.IsDBNull(3) || !DateTime.TryParse(reader.GetString(3), out var e)
                ? null : e;
            result[reader.GetString(0)] = new BoardIndexState(reader.GetString(1), last, earliest);
        }
        return result;
    }

    /// <summary>记下一个板块这一轮合成到哪儿了。</summary>
    public void Save(string boardCode, BoardIndexState state, DateTime synthesizedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO BoardIndexState(board_code, member_hash, last_bar_date, member_earliest, synthesized_at)
            VALUES($code, $hash, $last, $earliest, $at)
            ON CONFLICT(board_code) DO UPDATE SET
                member_hash = excluded.member_hash,
                last_bar_date = excluded.last_bar_date,
                member_earliest = excluded.member_earliest,
                synthesized_at = excluded.synthesized_at;
            """;
        cmd.Parameters.AddWithValue("$code", boardCode);
        cmd.Parameters.AddWithValue("$hash", state.MemberHash);
        cmd.Parameters.AddWithValue("$last", state.LastBarDate.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$earliest",
            (object?)state.MemberEarliest?.ToString(DateFormat) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", synthesizedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>某个板块的状态作废（合成失败时用——别让下一轮以为它是好的）。</summary>
    public void Invalidate(string boardCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BoardIndexState WHERE board_code = $code;";
        cmd.Parameters.AddWithValue("$code", boardCode);
        cmd.ExecuteNonQuery();
    }
}
