using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>股东数据本地存取（ShareholderCount + TopShareholder）——每次抓取返回该股全部历史，按 code
/// "删旧写新"整体覆盖，仿 <see cref="SqliteBoardRepository.ReplaceAll"/> 的删+写事务。</summary>
public class SqliteShareholderRepository : IShareholderRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteShareholderRepository(string dbFilePath)
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

    public void ReplaceByCode(string code, ShareholderData data)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM ShareholderCount WHERE code = $c; DELETE FROM TopShareholder WHERE code = $c;";
            del.Parameters.AddWithValue("$c", code);
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO ShareholderCount (code, report_date, holder_num, avg_shares, fetched_at)
                VALUES ($c, $d, $n, $avg, $at);
                """;
            var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
            var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
            var pn = cmd.CreateParameter(); pn.ParameterName = "$n"; cmd.Parameters.Add(pn);
            var pavg = cmd.CreateParameter(); pavg.ParameterName = "$avg"; cmd.Parameters.Add(pavg);
            var pat = cmd.CreateParameter(); pat.ParameterName = "$at"; cmd.Parameters.Add(pat);
            foreach (var r in data.Counts)
            {
                pc.Value = r.Code;
                pd.Value = r.ReportDate.ToString(DateFormat, CultureInfo.InvariantCulture);
                pn.Value = r.HolderNum;
                pavg.Value = r.AvgShares;
                pat.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO TopShareholder (code, report_date, kind, rank, holder_name, shares, ratio, share_type, fetched_at)
                VALUES ($c, $d, $k, $r, $hn, $s, $ra, $st, $at);
                """;
            var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
            var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
            var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
            var pr = cmd.CreateParameter(); pr.ParameterName = "$r"; cmd.Parameters.Add(pr);
            var phn = cmd.CreateParameter(); phn.ParameterName = "$hn"; cmd.Parameters.Add(phn);
            var ps = cmd.CreateParameter(); ps.ParameterName = "$s"; cmd.Parameters.Add(ps);
            var pra = cmd.CreateParameter(); pra.ParameterName = "$ra"; cmd.Parameters.Add(pra);
            var pst = cmd.CreateParameter(); pst.ParameterName = "$st"; cmd.Parameters.Add(pst);
            var pat = cmd.CreateParameter(); pat.ParameterName = "$at"; cmd.Parameters.Add(pat);
            foreach (var r in data.TopHolders)
            {
                pc.Value = r.Code;
                pd.Value = r.ReportDate.ToString(DateFormat, CultureInfo.InvariantCulture);
                pk.Value = r.Kind;
                pr.Value = r.Rank;
                phn.Value = (object?)r.HolderName ?? DBNull.Value;
                ps.Value = r.Shares;
                pra.Value = r.Ratio;
                pst.Value = (object?)r.ShareType ?? DBNull.Value;
                pat.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public List<ShareholderCountRow> GetCountSeries(string code)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, report_date, holder_num, avg_shares, fetched_at FROM ShareholderCount WHERE code = $c ORDER BY report_date;";
        cmd.Parameters.AddWithValue("$c", code);
        var result = new List<ShareholderCountRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ShareholderCountRow
            {
                Code = reader.GetString(0),
                ReportDate = DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture),
                HolderNum = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                AvgShares = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                FetchedAt = reader.IsDBNull(4) ? DateTime.MinValue : DateTime.ParseExact(reader.GetString(4), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return result;
    }

    public int GetCodeCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT code) FROM ShareholderCount;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
