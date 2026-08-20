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
                INSERT OR IGNORE INTO TopShareholder (code, report_date, kind, rank, holder_name, shares, ratio, share_type, change_direction, fetched_at)
                VALUES ($c, $d, $k, $r, $hn, $s, $ra, $st, $cd, $at);
                """;
            var pc = cmd.CreateParameter(); pc.ParameterName = "$c"; cmd.Parameters.Add(pc);
            var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; cmd.Parameters.Add(pd);
            var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
            var pr = cmd.CreateParameter(); pr.ParameterName = "$r"; cmd.Parameters.Add(pr);
            var phn = cmd.CreateParameter(); phn.ParameterName = "$hn"; cmd.Parameters.Add(phn);
            var ps = cmd.CreateParameter(); ps.ParameterName = "$s"; cmd.Parameters.Add(ps);
            var pra = cmd.CreateParameter(); pra.ParameterName = "$ra"; cmd.Parameters.Add(pra);
            var pst = cmd.CreateParameter(); pst.ParameterName = "$st"; cmd.Parameters.Add(pst);
            var pcd = cmd.CreateParameter(); pcd.ParameterName = "$cd"; cmd.Parameters.Add(pcd);
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
                pcd.Value = (object?)r.ChangeDirection ?? DBNull.Value;
                pat.Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// 入库前的自洽性检查：**排名夹在两个正数中间、自己却是0**，说明这一格没解析出来。
    /// 前十大股东是按持股数降序排的，所以第 N 名的持股必然介于第 N-1 名和第 N+1 名之间；
    /// 出现 0 就一定是解析问题而不是真实数据（真的持股为0根本不会上榜）。
    ///
    /// 这个检查是 2026-08-13 那次事故的产物：新浪在持股数后面挂了个环比箭头（<c>40610695↓</c>），
    /// 旧的 ParseD 只剥逗号和百分号，箭头导致 TryParse 失败、静默返回0，库里 14953 行中招却没人发现，
    /// 直到用户看结果表时觉得"排进前十却是0股"不合理才暴露。解析已修，这里再加一道防线：
    /// 数据源将来换别的装饰符号时，能在入库当时就喊出来，而不是等几个月后被肉眼抓到。
    ///
    /// 返回可疑行的描述（空列表=正常），由调用方决定是记日志还是拦下来——**这里不抛异常**：
    /// 单只股票的局部异常不该中断整批抓取。
    /// </summary>
    public static List<string> FindInconsistentZeroShares(IEnumerable<TopShareholderRow> rows)
    {
        var problems = new List<string>();
        foreach (var g in rows.GroupBy(r => (r.Code, r.ReportDate, r.Kind)))
        {
            var ordered = g.OrderBy(r => r.Rank).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Shares > 0) continue;
                // 只要前面有正数、后面也有正数，这一行的0就是自相矛盾的
                bool posBefore = ordered.Take(i).Any(r => r.Shares > 0);
                bool posAfter = ordered.Skip(i + 1).Any(r => r.Shares > 0);
                if (posBefore && posAfter)
                    problems.Add($"{g.Key.Code} {g.Key.ReportDate:yyyy-MM-dd} {g.Key.Kind} " +
                                 $"第{ordered[i].Rank}名「{ordered[i].HolderName}」持股解析为0，" +
                                 $"但前后名次都有正值——疑似数据源格式变化导致解析失败");
            }
        }
        return problems;
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
