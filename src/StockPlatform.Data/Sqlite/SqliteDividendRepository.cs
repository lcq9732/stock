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
            SELECT code, announce_date, bonus_shares, transfer_shares, dividend_yuan, progress, record_date, ex_date, fetched_at
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
            });
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
}
