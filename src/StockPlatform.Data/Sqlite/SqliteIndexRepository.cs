using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>指数成分名单 / 成分权重 / ETF→指数映射 的本地存取（IndexCons / IndexWeight / EtfIndexMap）。
/// 成分与权重按指数"删旧写新"（成分会调整，不能 INSERT OR IGNORE 累积）；ETF 映射整体覆盖。仿
/// <see cref="SqliteBoardRepository"/> 的连接/事务/参数复用写法。</summary>
public class SqliteIndexRepository : IIndexConsRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteIndexRepository(string dbFilePath)
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

    public void ReplaceCons(string indexCode, IEnumerable<(string Code, DateTime? InDate)> members, DateTime fetchedAt)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM IndexCons WHERE index_code = $idx;";
            del.Parameters.AddWithValue("$idx", indexCode);
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO IndexCons (index_code, stock_code, in_date, fetched_at) VALUES ($idx, $code, $in, $at);";
            var pIdx = cmd.CreateParameter(); pIdx.ParameterName = "$idx"; cmd.Parameters.Add(pIdx);
            var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
            var pIn = cmd.CreateParameter(); pIn.ParameterName = "$in"; cmd.Parameters.Add(pIn);
            var pAt = cmd.CreateParameter(); pAt.ParameterName = "$at"; cmd.Parameters.Add(pAt);
            pIdx.Value = indexCode;
            pAt.Value = fetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            foreach (var (code, inDate) in members)
            {
                pCode.Value = code;
                pIn.Value = inDate.HasValue ? inDate.Value.ToString(DateFormat, CultureInfo.InvariantCulture) : (object)DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>版本化写入：**不删旧**，按 as_of_date 保留各期历史（同一 index+stock+as_of 重复则忽略），
    /// 供回测按时点取当期权重。中证每次给最新一期，多次拉不同基准日就累积多版。</summary>
    public void ReplaceWeights(string indexCode, IEnumerable<IndexWeightRow> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO IndexWeight (index_code, stock_code, weight, as_of_date, fetched_at)
            VALUES ($idx, $code, $w, $asof, $at);
            """;
        var pIdx = cmd.CreateParameter(); pIdx.ParameterName = "$idx"; cmd.Parameters.Add(pIdx);
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pW = cmd.CreateParameter(); pW.ParameterName = "$w"; cmd.Parameters.Add(pW);
        var pAsOf = cmd.CreateParameter(); pAsOf.ParameterName = "$asof"; cmd.Parameters.Add(pAsOf);
        var pAt = cmd.CreateParameter(); pAt.ParameterName = "$at"; cmd.Parameters.Add(pAt);
        foreach (var r in rows)
        {
            pIdx.Value = r.IndexCode;
            pCode.Value = r.StockCode;
            pW.Value = r.Weight;
            // as_of_date 是主键列(NOT NULL)——极少数拿不到基准日时用空串占位，保证去重一致。
            pAsOf.Value = r.AsOfDate == default ? "" : r.AsOfDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            pAt.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ReplaceEtfIndexMap(IEnumerable<(string EtfCode, string? IndexCode, string MatchType)> rows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM EtfIndexMap;";
            del.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO EtfIndexMap (etf_code, index_code, match_type) VALUES ($etf, $idx, $mt);";
            var pEtf = cmd.CreateParameter(); pEtf.ParameterName = "$etf"; cmd.Parameters.Add(pEtf);
            var pIdx = cmd.CreateParameter(); pIdx.ParameterName = "$idx"; cmd.Parameters.Add(pIdx);
            var pMt = cmd.CreateParameter(); pMt.ParameterName = "$mt"; cmd.Parameters.Add(pMt);
            foreach (var (etf, idx, mt) in rows)
            {
                pEtf.Value = etf;
                pIdx.Value = (object?)idx ?? DBNull.Value;
                pMt.Value = mt;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public List<string> GetConsByIndex(string indexCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT stock_code FROM IndexCons WHERE index_code = $code ORDER BY stock_code;";
        cmd.Parameters.AddWithValue("$code", indexCode);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public List<string> GetIndexesByStock(string stockCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT index_code FROM IndexCons WHERE stock_code = $code;";
        cmd.Parameters.AddWithValue("$code", stockCode);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public List<string> GetEtfsByStock(string stockCode)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT m.etf_code
            FROM IndexCons c
            JOIN EtfIndexMap m ON c.index_code = m.index_code
            WHERE c.stock_code = $code;
            """;
        cmd.Parameters.AddWithValue("$code", stockCode);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public int GetConsIndexCount()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT index_code) FROM IndexCons;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public DateTime? GetLatestConsFetch()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(fetched_at) FROM IndexCons;";
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, TimeFormat, CultureInfo.InvariantCulture);
    }
}
