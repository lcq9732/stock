using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 龙虎榜营业部席位明细的本地存取（2026-09-03）。
///
/// 全量 178 万行，写入粒度是**一个交易日**：一天买卖两侧都收齐了，整天删掉重写
/// （<see cref="ReplaceForDay"/>）。中断时已落库的天是完整的，重跑从水位线接着走。
///
/// ⚠ 2026-09-17 把原来的 <c>Upsert</c>（<c>INSERT OR REPLACE</c>，靠主键去重）**删掉了**，
/// 没有保留。留着就是留个陷阱——谁用了它，副本就回来了：主键末列 <c>seq</c> 是**位次**，
/// 一次抓取多收一行、整组编号就多一位，上次落库的高位行没人覆盖得掉。
/// 全表曾因此堆出 3579 行副本（同时缺着一样多的真行）。见 doc/lhb-seat-task-design.md。
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

    /// <summary>
    /// **整日替换**：删掉这一天的全部行，写入本批（同一个事务）。返回写入行数。
    ///
    /// ⚠ 传进来的必须是这一天**买卖两侧的全部行**。只传一侧就等于把另一侧永久删掉，
    /// 而且事后完全看不出来——行数判据只会觉得"那天本来就少"。收齐的判断在
    /// <see cref="StockPlatform.Logic.Models.LhbSeatDay.IsComplete"/>，调用方过了那一关才该走到这儿。
    ///
    /// 为什么是删了重写而不是 UPSERT：主键末列 <c>seq</c> 是位次、不是稳定标识，
    /// 靠它去重会在抓取行数波动时留下孤儿行（见类注释）。删了重写跟"这一天现在是什么样"
    /// 一一对应，抓多少次结果都一样。
    /// </summary>
    public int ReplaceForDay(DateTime day, IReadOnlyList<LhbSeat> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM LhbSeat WHERE trade_date = $td;";
            del.Parameters.AddWithValue("$td", day.Date.ToString(DateFormat, CultureInfo.InvariantCulture));
            del.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO LhbSeat
                (trade_date, code, name, is_buy, seat_code, seat_name, buy, sell, net,
                 explanation, rise_prob_3day, times_3day, trade_id, seq, close_price, change_rate, fetched_at,
                 buy_ratio, sell_ratio, change_type)
            VALUES ($td, $code, $name, $isbuy, $seat, $seatname, $buy, $sell, $net,
                    $expl, $prob, $times, $tid, $seq, $close, $chg, $fetched,
                    $bratio, $sratio, $ctype);
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$td", "$code", "$name", "$isbuy", "$seat", "$seatname", "$buy",
                                  "$sell", "$net", "$expl", "$prob", "$times", "$tid", "$seq", "$close",
                                  "$chg", "$fetched", "$bratio", "$sratio", "$ctype" })
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
            p["$bratio"].Value = (object?)s.BuyRatio ?? DBNull.Value;
            p["$sratio"].Value = (object?)s.SellRatio ?? DBNull.Value;
            p["$ctype"].Value = s.ChangeType;
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
