using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 龙虎榜营业部席位明细的本地存取（2026-09-03）。
///
/// 全量 264 万行，是本次接入的数据里最大的一块，所以写入路径刻意做成"按片多次"：
/// provider 每抓完一个月（约 1.5 万行）回调一次，这里一次事务写完。中断时已落库的部分是
/// 有效的，重跑从水位线接着走——通宵抓最怕的就是挂了要从头来。
///
/// 回调粒度是"月"而不是"每 N 行"，因为 LhbSeat.Seq 要整片就位后才能算（见那个字段的注释）。
/// </summary>
public class SqliteLhbSeatRepository : ILhbSeatRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public SqliteLhbSeatRepository(string dbFilePath)
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

    public int Upsert(IEnumerable<LhbSeat> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO LhbSeat
                (trade_date, code, name, is_buy, seat_code, seat_name, buy, sell, net,
                 explanation, rise_prob_3day, times_3day, trade_id, seq, close_price, change_rate, fetched_at)
            VALUES ($td, $code, $name, $isbuy, $seat, $seatname, $buy, $sell, $net,
                    $expl, $prob, $times, $tid, $seq, $close, $chg, $fetched);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$td", "$code", "$name", "$isbuy", "$seat", "$seatname", "$buy",
                                  "$sell", "$net", "$expl", "$prob", "$times", "$tid", "$seq", "$close",
                                  "$chg", "$fetched" })
        {
            var par = cmd.CreateParameter(); par.ParameterName = n; cmd.Parameters.Add(par); p[n] = par;
        }

        foreach (var s in list)
        {
            p["$td"].Value = s.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
            p["$code"].Value = s.Code;
            p["$name"].Value = s.Name;
            p["$isbuy"].Value = s.IsBuy ? 1 : 0;
            p["$seat"].Value = s.SeatCode;
            p["$seatname"].Value = s.SeatName;
            p["$buy"].Value = (object?)s.Buy ?? DBNull.Value;
            p["$sell"].Value = (object?)s.Sell ?? DBNull.Value;
            p["$net"].Value = (object?)s.Net ?? DBNull.Value;
            p["$expl"].Value = s.Explanation;
            p["$prob"].Value = (object?)s.RiseProbability3Day ?? DBNull.Value;
            p["$times"].Value = (object?)s.Times3Day ?? DBNull.Value;
            p["$tid"].Value = s.TradeId;
            p["$seq"].Value = s.Seq;
            p["$close"].Value = (object?)s.ClosePrice ?? DBNull.Value;
            p["$chg"].Value = (object?)s.ChangeRate ?? DBNull.Value;
            p["$fetched"].Value = s.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return list.Count;
    }

    /// <summary>
    /// 本地已有的最新交易日 —— 增量水位线。
    /// 跟业绩预告一样<b>从这一天本身重抓</b>而不是次日：龙虎榜是盘后发布的，上次抓的时候
    /// 当天可能还没发全。主键 UPSERT 保证重抓不产生重复行。
    /// </summary>
    public DateTime? GetLatestTradeDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(trade_date) FROM LhbSeat";
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return null;
        var s = v.ToString();
        return string.IsNullOrEmpty(s) ? null
            : DateTime.ParseExact(s.Substring(0, 10), DateFormat, CultureInfo.InvariantCulture);
    }

    public int Count()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM LhbSeat";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>某只股票某天的席位明细（买卖两边一起返回，按净额从大到小）。</summary>
    public List<LhbSeat> QueryByStock(string code, DateTime tradeDate)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trade_date, code, name, is_buy, seat_code, seat_name, buy, sell, net,
                   explanation, rise_prob_3day, times_3day, trade_id, seq, close_price, change_rate, fetched_at
            FROM LhbSeat WHERE code = $code AND trade_date = $td
            ORDER BY is_buy DESC, explanation, seq;
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$td", tradeDate.ToString(DateFormat, CultureInfo.InvariantCulture));
        return Read(cmd);
    }

    /// <summary>某个营业部的上榜记录（自建游资库/算席位胜率用），按日期倒序。</summary>
    public List<LhbSeat> QueryBySeat(string seatCode, int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trade_date, code, name, is_buy, seat_code, seat_name, buy, sell, net,
                   explanation, rise_prob_3day, times_3day, trade_id, seq, close_price, change_rate, fetched_at
            FROM LhbSeat WHERE seat_code = $seat
            ORDER BY trade_date DESC LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$seat", seatCode);
        cmd.Parameters.AddWithValue("$lim", limit);
        return Read(cmd);
    }

    private static List<LhbSeat> Read(SqliteCommand cmd)
    {
        var list = new List<LhbSeat>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LhbSeat
            {
                TradeDate = DateTime.ParseExact(r.GetString(0).Substring(0, 10), DateFormat, CultureInfo.InvariantCulture),
                Code = r.GetString(1),
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                IsBuy = r.GetInt32(3) == 1,
                SeatCode = r.IsDBNull(4) ? "" : r.GetString(4),
                SeatName = r.IsDBNull(5) ? "" : r.GetString(5),
                Buy = r.IsDBNull(6) ? null : r.GetDouble(6),
                Sell = r.IsDBNull(7) ? null : r.GetDouble(7),
                Net = r.IsDBNull(8) ? null : r.GetDouble(8),
                Explanation = r.IsDBNull(9) ? "" : r.GetString(9),
                RiseProbability3Day = r.IsDBNull(10) ? null : r.GetDouble(10),
                Times3Day = r.IsDBNull(11) ? null : r.GetInt32(11),
                TradeId = r.IsDBNull(12) ? "" : r.GetString(12),
                Seq = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                ClosePrice = r.IsDBNull(14) ? null : r.GetDouble(14),
                ChangeRate = r.IsDBNull(15) ? null : r.GetDouble(15),
                FetchedAt = r.IsDBNull(16) ? DateTime.MinValue
                    : DateTime.ParseExact(r.GetString(16), TimeFormat, CultureInfo.InvariantCulture),
            });
        }
        return list;
    }
}
