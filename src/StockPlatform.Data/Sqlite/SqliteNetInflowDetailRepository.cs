using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 分档资金流的本地存取（2026-09-03）。
///
/// 抓取是"逐只股票、每只约 120 行"，所以写入按股票为单位。<see cref="HasFreshData"/> 是
/// 断点续传的关键：接口给的是滚动 120 天窗口、每次都返回同样那批日期，没有增量入口，
/// 只能靠"这只票今天已经抓过了"来跳过——否则中断重跑就得把 5500 只从头再来一遍。
/// </summary>
public class SqliteNetInflowDetailRepository : INetInflowDetailRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteNetInflowDetailRepository(string dbFilePath)
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

    public int Upsert(IEnumerable<NetInflowDetail> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO NetInflowDetail
                (code, trade_date, main_net, super_net, big_net, mid_net, small_net,
                 main_ratio, super_ratio, big_ratio, mid_ratio, small_ratio,
                 close_price, change_rate, fetched_at)
            VALUES ($code, $td, $mn, $sun, $bn, $mdn, $smn,
                    $mr, $sur, $br, $mdr, $smr, $close, $chg, $fetched);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$code", "$td", "$mn", "$sun", "$bn", "$mdn", "$smn",
                                  "$mr", "$sur", "$br", "$mdr", "$smr", "$close", "$chg", "$fetched" })
        {
            var par = cmd.CreateParameter(); par.ParameterName = n; cmd.Parameters.Add(par); p[n] = par;
        }

        foreach (var d in list)
        {
            p["$code"].Value = d.Code;
            p["$td"].Value = d.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$mn"].Value = (object?)d.MainNet ?? DBNull.Value;
            p["$sun"].Value = (object?)d.SuperNet ?? DBNull.Value;
            p["$bn"].Value = (object?)d.BigNet ?? DBNull.Value;
            p["$mdn"].Value = (object?)d.MidNet ?? DBNull.Value;
            p["$smn"].Value = (object?)d.SmallNet ?? DBNull.Value;
            p["$mr"].Value = (object?)d.MainRatio ?? DBNull.Value;
            p["$sur"].Value = (object?)d.SuperRatio ?? DBNull.Value;
            p["$br"].Value = (object?)d.BigRatio ?? DBNull.Value;
            p["$mdr"].Value = (object?)d.MidRatio ?? DBNull.Value;
            p["$smr"].Value = (object?)d.SmallRatio ?? DBNull.Value;
            p["$close"].Value = (object?)d.ClosePrice ?? DBNull.Value;
            p["$chg"].Value = (object?)d.ChangeRate ?? DBNull.Value;
            p["$fetched"].Value = d.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    /// <summary>
    /// 这只票是否已经在 <paramref name="since"/> 之后抓过 —— 断点续传靠它跳过已完成的。
    /// 接口是滚动窗口、没有增量入口，所以只能按"抓取时刻"而不是"数据日期"来判断。
    /// </summary>
    public bool HasFreshData(string code, DateTime since)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(fetched_at) FROM NetInflowDetail WHERE code = $code";
        cmd.Parameters.AddWithValue("$code", code);
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return false;
        return DateTime.TryParseExact(v.ToString(), TimeFormat, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out var t) && t >= since;
    }

    /// <summary>
    /// 每只票上次抓取的时刻，一次查全（2026-09-04）。给"最久没抓的先抓"排序用。
    ///
    /// 为什么非要它：原来的排队是"今天没抓过的按代码顺序抓"，而每天零点一到，
    /// 昨天抓过的又全变成"今天没抓过"——于是每天都从 000001 重新开始，
    /// 代码靠后的票**永远轮不到**。实测跑了两天，库里只有 000001~000509 这 89 只。
    ///
    /// 接口是 120 天滚动窗口，意思是**只要每只票 120 天内被轮到一次，历史就不会缺**。
    /// 全市场 5900 只、每天能抓百来只的话 60 天转完一圈，正好在窗口内——
    /// 前提是排队得轮转，不能每天从头来。
    /// </summary>
    public Dictionary<string, DateTime> GetLastFetchedAt()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, MAX(fetched_at) FROM NetInflowDetail GROUP BY code";
        var map = new Dictionary<string, DateTime>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(1)) continue;
            if (DateTime.TryParseExact(r.GetString(1), TimeFormat, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var t))
                map[r.GetString(0)] = t;
        }
        return map;
    }

    /// <summary>
    /// 每只票在库里有多少行（2026-09-06）。给"哪些票的 120 天历史还没补齐"排队用。
    ///
    /// 一条 GROUP BY 扫 62 万行，几十毫秒——比按只查 5900 次便宜得多。
    /// </summary>
    public Dictionary<string, int> GetRowCountByCode()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code, COUNT(*) FROM NetInflowDetail GROUP BY code";
        var map = new Dictionary<string, int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    /// <summary>
    /// 某一段交易日窗口内，每只票有多少行（2026-09-11）。判据必须用它而不是全表计数，
    /// 理由见接口上的注释。闭区间。
    /// </summary>
    public Dictionary<string, int> GetRowCountByCode(DateTime from, DateTime to)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT code, COUNT(*) FROM NetInflowDetail "
          + "WHERE trade_date >= $from AND trade_date <= $to GROUP BY code";
        cmd.Parameters.AddWithValue("$from", from.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", to.ToString(DateFormat, CultureInfo.InvariantCulture));
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetInt32(1);
        return map;
    }

    public int Count()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM NetInflowDetail";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public int CountCodes()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT code) FROM NetInflowDetail";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public List<NetInflowDetail> Query(string code, int limit = 120)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, trade_date, main_net, super_net, big_net, mid_net, small_net,
                   main_ratio, super_ratio, big_ratio, mid_ratio, small_ratio,
                   close_price, change_rate, fetched_at
            FROM NetInflowDetail WHERE code = $code
            ORDER BY trade_date DESC LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$lim", limit);

        var list = new List<NetInflowDetail>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new NetInflowDetail
            {
                Code = r.GetString(0),
                TradeDate = DateTime.ParseExact(r.GetString(1), DateFormat, CultureInfo.InvariantCulture),
                MainNet = r.IsDBNull(2) ? null : r.GetDouble(2),
                SuperNet = r.IsDBNull(3) ? null : r.GetDouble(3),
                BigNet = r.IsDBNull(4) ? null : r.GetDouble(4),
                MidNet = r.IsDBNull(5) ? null : r.GetDouble(5),
                SmallNet = r.IsDBNull(6) ? null : r.GetDouble(6),
                MainRatio = r.IsDBNull(7) ? null : r.GetDouble(7),
                SuperRatio = r.IsDBNull(8) ? null : r.GetDouble(8),
                BigRatio = r.IsDBNull(9) ? null : r.GetDouble(9),
                MidRatio = r.IsDBNull(10) ? null : r.GetDouble(10),
                SmallRatio = r.IsDBNull(11) ? null : r.GetDouble(11),
                ClosePrice = r.IsDBNull(12) ? null : r.GetDouble(12),
                ChangeRate = r.IsDBNull(13) ? null : r.GetDouble(13),
                FetchedAt = r.IsDBNull(14) ? DateTime.MinValue
                    : DateTime.ParseExact(r.GetString(14), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }
}
