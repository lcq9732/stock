using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

public class SqliteBoardRepository : IBoardRepository
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteBoardRepository(string dbFilePath)
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

    public void ReplaceAll(IEnumerable<Board> boards)
    {
        var list = boards.ToList();
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Board; DELETE FROM BoardMember;";
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO Board (board_code, board_type, name, member_count, change_pct, amount, leader_code, leader_name, as_of)
                VALUES ($code, $type, $name, $count, $pct, $amount, $lcode, $lname, $asof);
                """;
            var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
            var pType = cmd.CreateParameter(); pType.ParameterName = "$type"; cmd.Parameters.Add(pType);
            var pName = cmd.CreateParameter(); pName.ParameterName = "$name"; cmd.Parameters.Add(pName);
            var pCount = cmd.CreateParameter(); pCount.ParameterName = "$count"; cmd.Parameters.Add(pCount);
            var pPct = cmd.CreateParameter(); pPct.ParameterName = "$pct"; cmd.Parameters.Add(pPct);
            var pAmount = cmd.CreateParameter(); pAmount.ParameterName = "$amount"; cmd.Parameters.Add(pAmount);
            var pLCode = cmd.CreateParameter(); pLCode.ParameterName = "$lcode"; cmd.Parameters.Add(pLCode);
            var pLName = cmd.CreateParameter(); pLName.ParameterName = "$lname"; cmd.Parameters.Add(pLName);
            var pAsOf = cmd.CreateParameter(); pAsOf.ParameterName = "$asof"; cmd.Parameters.Add(pAsOf);

            foreach (var b in list)
            {
                pCode.Value = b.BoardCode;
                pType.Value = (int)b.Type;
                pName.Value = b.Name;
                pCount.Value = b.MemberCount;
                pPct.Value = b.ChangePct;
                pAmount.Value = b.Amount;
                pLCode.Value = b.LeaderCode;
                pLName.Value = b.LeaderName;
                pAsOf.Value = b.AsOf.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        using (var mcmd = conn.CreateCommand())
        {
            mcmd.Transaction = tx;
            mcmd.CommandText = "INSERT OR IGNORE INTO BoardMember (board_code, stock_code) VALUES ($bcode, $scode);";
            var pB = mcmd.CreateParameter(); pB.ParameterName = "$bcode"; mcmd.Parameters.Add(pB);
            var pS = mcmd.CreateParameter(); pS.ParameterName = "$scode"; mcmd.Parameters.Add(pS);
            foreach (var b in list)
                foreach (var s in b.MemberCodes)
                {
                    pB.Value = b.BoardCode;
                    pS.Value = s;
                    mcmd.ExecuteNonQuery();
                }
        }

        tx.Commit();
    }

    public List<Board> QueryBoards(BoardType? type = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT board_code, board_type, name, member_count, change_pct, amount, leader_code, leader_name, as_of
            FROM Board
            WHERE ($type IS NULL OR board_type = $type)
            ORDER BY change_pct DESC;
            """;
        cmd.Parameters.AddWithValue("$type", type.HasValue ? (int)type.Value : (object)DBNull.Value);

        var result = new List<Board>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Board
            {
                BoardCode = reader.GetString(0),
                Type = (BoardType)reader.GetInt32(1),
                Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                MemberCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                ChangePct = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                Amount = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                LeaderCode = reader.IsDBNull(6) ? "" : reader.GetString(6),
                LeaderName = reader.IsDBNull(7) ? "" : reader.GetString(7),
                AsOf = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(8), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    public List<string> QueryMembers(string boardCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT stock_code FROM BoardMember WHERE board_code = $bcode;";
        cmd.Parameters.AddWithValue("$bcode", boardCode);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public Dictionary<string, List<string>> GetConceptBoardsByStock()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bm.stock_code, b.name
            FROM BoardMember bm
            JOIN Board b ON bm.board_code = b.board_code
            WHERE b.board_type = 0;
            """;
        var map = new Dictionary<string, List<string>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (name.Length == 0) continue;
            if (!map.TryGetValue(code, out var list)) { list = new List<string>(); map[code] = list; }
            list.Add(name);
        }
        return map;
    }

    public DateTime? GetLatestAsOf()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(as_of) FROM Board;";
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, TimeFormat, CultureInfo.InvariantCulture);
    }
}
