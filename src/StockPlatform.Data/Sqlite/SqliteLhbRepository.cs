using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>龙虎榜的本地存取（Lhb 表）——按交易日累积保留历史，仿
/// <see cref="SqliteNetInflowRepository"/>。三种写入语义并存，用途见
/// <see cref="ILhbRepository"/> 上各方法的说明。</summary>
public class SqliteLhbRepository : ILhbRepository
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    /// <summary>全部列，写入语句共用。顺序跟 <see cref="Bind"/> 里的参数一一对应。</summary>
    private const string Columns = """
        trade_date, stock_code, stock_name, close_price, deviation, volume, amount, reason, fetched_at,
        change_rate, turnover_rate, free_market_cap,
        billboard_buy_amt, billboard_sell_amt, billboard_net_amt, billboard_deal_amt,
        deal_amount_ratio, deal_net_ratio, explain_text, trade_id, change_type, trade_market,
        d1_chg, d2_chg, d5_chg, d10_chg, d20_chg, d30_chg, source, deviation_source
        """;

    private const string Values = """
        $date, $code, $name, $close, $dev, $vol, $amt, $reason, $at,
        $chgRate, $turnover, $freeCap,
        $bbBuy, $bbSell, $bbNet, $bbDeal,
        $dealAmtRatio, $dealNetRatio, $explain, $tradeId, $changeType, $tradeMarket,
        $d1, $d2, $d5, $d10, $d20, $d30, $source, $devSource
        """;

    public SqliteLhbRepository(string dbFilePath)
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

    public void InsertOrIgnore(IEnumerable<LhbRow> rows) => Write(rows, "INSERT OR IGNORE");

    public void Upsert(IEnumerable<LhbRow> rows) => Write(rows, "INSERT OR REPLACE");

    private void Write(IEnumerable<LhbRow> rows, string verb)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = PrepareInsert(conn, tx, verb);
        foreach (var r in rows) { Bind(cmd, r); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>
    /// 整天替换。一个事务里先删后插，中途失败整天回滚——**不能让某一天处在"旧的删了、新的
    /// 没写进去"的状态**：龙虎榜按天判"这天有没有数据"，半截的一天会被后续逻辑当成"这天
    /// 没人上榜"，而那是个一次定案、再也不会回头补的判断。
    /// </summary>
    public (int Deleted, int Inserted) ReplaceDays(IEnumerable<LhbRow> rows)
    {
        var byDay = rows.GroupBy(r => r.TradeDate.Date).ToList();
        if (byDay.Count == 0) return (0, 0);

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int deleted = 0, inserted = 0;
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Lhb WHERE trade_date = $date;";
            var p = del.CreateParameter(); p.ParameterName = "$date"; del.Parameters.Add(p);
            foreach (var g in byDay)
            {
                p.Value = g.Key.ToString(DateFormat, CultureInfo.InvariantCulture);
                deleted += del.ExecuteNonQuery();
            }
        }

        using (var cmd = PrepareInsert(conn, tx, "INSERT OR REPLACE"))
            foreach (var g in byDay)
                foreach (var r in g) { Bind(cmd, r); inserted += cmd.ExecuteNonQuery(); }

        tx.Commit();
        return (deleted, inserted);
    }

    public int CountRows(DateOnly start, DateOnly end, string? source = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Lhb WHERE trade_date >= $s AND trade_date <= $e"
                        + (source == null ? ";" : " AND source = $src;");
        cmd.Parameters.AddWithValue("$s", start.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$e", end.ToString(DateFormat, CultureInfo.InvariantCulture));
        if (source != null) cmd.Parameters.AddWithValue("$src", source);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 导出整张表到另一个 sqlite 文件。用 ATTACH + <c>CREATE TABLE … AS SELECT</c>，
    /// 26 万行走的是 SQLite 内部的批量路径，比逐行读出来再写回去快一个量级。
    /// 目标文件已存在就先删——备份是"这一刻的快照"，跟上一次的半份混在一起没有意义。
    /// </summary>
    public int ExportTo(string targetDbPath)
    {
        var dir = Path.GetDirectoryName(targetDbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(targetDbPath)) File.Delete(targetDbPath);

        using var conn = Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $p AS bak;";
            attach.Parameters.AddWithValue("$p", targetDbPath);
            attach.ExecuteNonQuery();
        }
        try
        {
            using var copy = conn.CreateCommand();
            copy.CommandText = "CREATE TABLE bak.Lhb AS SELECT * FROM main.Lhb;";
            copy.ExecuteNonQuery();

            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM bak.Lhb;";
            return Convert.ToInt32(count.ExecuteScalar());
        }
        finally
        {
            using var detach = conn.CreateCommand();
            detach.CommandText = "DETACH DATABASE bak;";
            detach.ExecuteNonQuery();
        }
    }

    public DateTime? GetLatestTradeDate()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(trade_date) FROM Lhb;";
        var result = cmd.ExecuteScalar();
        if (result == null || result is DBNull) return null;
        return DateTime.ParseExact((string)result, DateFormat, CultureInfo.InvariantCulture);
    }

    public HashSet<DateOnly> GetTradeDates()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT trade_date FROM Lhb;";
        var set = new HashSet<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (DateOnly.TryParseExact(reader.GetString(0), DateFormat, out var d)) set.Add(d);
        return set;
    }

    private static SqliteCommand PrepareInsert(SqliteConnection conn, SqliteTransaction tx, string verb)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"{verb} INTO Lhb ({Columns}) VALUES ({Values});";
        foreach (var name in new[]
                 {
                     "$date", "$code", "$name", "$close", "$dev", "$vol", "$amt", "$reason", "$at",
                     "$chgRate", "$turnover", "$freeCap",
                     "$bbBuy", "$bbSell", "$bbNet", "$bbDeal",
                     "$dealAmtRatio", "$dealNetRatio", "$explain", "$tradeId", "$changeType", "$tradeMarket",
                     "$d1", "$d2", "$d5", "$d10", "$d20", "$d30", "$source", "$devSource",
                 })
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static void Bind(SqliteCommand cmd, LhbRow r)
    {
        cmd.Parameters["$date"].Value = r.TradeDate.ToString(DateFormat, CultureInfo.InvariantCulture);
        cmd.Parameters["$code"].Value = r.StockCode;
        cmd.Parameters["$name"].Value = Text(r.StockName);
        cmd.Parameters["$close"].Value = r.ClosePrice;
        cmd.Parameters["$dev"].Value = Nullable(r.Deviation);
        cmd.Parameters["$vol"].Value = Nullable(r.Volume);
        cmd.Parameters["$amt"].Value = r.Amount;
        cmd.Parameters["$reason"].Value = r.Reason;
        cmd.Parameters["$at"].Value = r.FetchedAt.ToString(TimeFormat, CultureInfo.InvariantCulture);
        cmd.Parameters["$chgRate"].Value = Nullable(r.ChangeRate);
        cmd.Parameters["$turnover"].Value = Nullable(r.TurnoverRate);
        cmd.Parameters["$freeCap"].Value = Nullable(r.FreeMarketCap);
        cmd.Parameters["$bbBuy"].Value = Nullable(r.BillboardBuyAmt);
        cmd.Parameters["$bbSell"].Value = Nullable(r.BillboardSellAmt);
        cmd.Parameters["$bbNet"].Value = Nullable(r.BillboardNetAmt);
        cmd.Parameters["$bbDeal"].Value = Nullable(r.BillboardDealAmt);
        cmd.Parameters["$dealAmtRatio"].Value = Nullable(r.DealAmountRatio);
        cmd.Parameters["$dealNetRatio"].Value = Nullable(r.DealNetRatio);
        cmd.Parameters["$explain"].Value = Text(r.Explain);
        cmd.Parameters["$tradeId"].Value = Text(r.TradeId);
        cmd.Parameters["$changeType"].Value = Text(r.ChangeType);
        cmd.Parameters["$tradeMarket"].Value = Text(r.TradeMarket);
        cmd.Parameters["$d1"].Value = Nullable(r.D1Chg);
        cmd.Parameters["$d2"].Value = Nullable(r.D2Chg);
        cmd.Parameters["$d5"].Value = Nullable(r.D5Chg);
        cmd.Parameters["$d10"].Value = Nullable(r.D10Chg);
        cmd.Parameters["$d20"].Value = Nullable(r.D20Chg);
        cmd.Parameters["$d30"].Value = Nullable(r.D30Chg);
        cmd.Parameters["$source"].Value = Text(r.Source);
        cmd.Parameters["$devSource"].Value = Text(r.DeviationSource);
    }

    /// <summary>null → DBNull。**不折成 0**：0 在这些数值列里是有意义的值
    /// （净买额真的可以是 0），拿它冒充"没有"会让下游统计悄悄失真。</summary>
    private static object Nullable(double? v) => v.HasValue ? v.Value : DBNull.Value;

    private static object Text(string? s) => string.IsNullOrEmpty(s) ? DBNull.Value : s;
}
