using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 按 <see cref="WatchKind"/> 去 <c>current.sqlite</c> 取观察项的当前值，喂给
/// <see cref="WatchEvaluator"/>。见 doc/watch-item-design.md §4.3。
///
/// **只读**——这边是 Analyzer 侧，current.sqlite 是 Fetcher 的产出
/// （见 <c>AnalyzerPaths</c> 类注释）。
///
/// ⚠ 行业指标那一路必须走 <see cref="WatchEvaluator.ReadLatestDistinct"/>：
/// <c>IndustryIndicatorValue</c> 是自然日序列，周末沿用周五的值、工作日也常连续同值，
/// 跟"上一行"比会在周末恒等于 0。
/// </summary>
public class SqliteWatchReadingSource
{
    private readonly string _connectionString;

    public SqliteWatchReadingSource(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>取一条观察项的当前值。取不到就返回空 reading（求值器会判成没触发）。</summary>
    public WatchReading Read(WatchItem item, IReadOnlyDictionary<Guid, string>? lastStages = null)
    {
        try
        {
            using var conn = Open();
            return item.Kind switch
            {
                WatchKind.PlanStage => ReadPlanStage(conn, item, lastStages),
                WatchKind.IndustryIndicator => ReadIndicator(conn, item),
                WatchKind.PriceMA => ReadPriceMa(conn, item),
                WatchKind.MarginBalance => ReadMargin(conn, item),
                WatchKind.HolderCount => ReadHolderCount(conn, item),
                WatchKind.EventTable => ReadEventTable(conn, item, lastStages),
                // Manual 没有数据源——这是设计里有意留的诚实出口，不是漏实现。
                _ => new WatchReading(null),
            };
        }
        catch
        {
            // 取值失败当成"没触发"，不让一条坏观察项把整轮求值搞挂。
            return new WatchReading(null);
        }
    }

    private static WatchReading ReadPlanStage(
        SqliteConnection conn, WatchItem item, IReadOnlyDictionary<Guid, string>? lastStages)
    {
        using var cmd = conn.CreateCommand();
        // 取最新一条进展。cum_amount 一起带回来：**0 是"公告明说尚未实施"**，
        // 跟 NULL（没抽到）不是一回事，消息里要分得开。
        cmd.CommandText = """
            SELECT stage, as_of_date, announce_date, cum_amount
            FROM PlanAnnouncement WHERE code = $c AND kind = $k
            ORDER BY announce_date DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$c", item.Code);
        cmd.Parameters.AddWithValue("$k", string.IsNullOrEmpty(item.Expr) ? PlanKind.Buyback : item.Expr);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new WatchReading(null);

        var stage = r.GetString(0);
        var asOf = ParseDate(r, 1) ?? ParseDate(r, 2);
        var amount = r.IsDBNull(3) ? (double?)null : r.GetDouble(3);

        // stage 文字里带上金额，这样"进展 → 进展"但金额从 0 变成非 0 时也能触发——
        // 首次回购公告万一没发或标题没匹配上，金额从 0 变正数就是兜底信号。
        var text = amount switch
        {
            null => stage,
            0 => $"{stage}（尚未实施）",
            _ => $"{stage}（已回购 {amount / 1e8:0.##} 亿元）",
        };
        lastStages?.TryGetValue(item.ItemId, out var prev);
        var prevText = lastStages != null && lastStages.TryGetValue(item.ItemId, out var p) ? p : null;
        return new WatchReading(asOf, amount, null, text, prevText);
    }

    private static WatchReading ReadIndicator(SqliteConnection conn, WatchItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trade_date, value FROM IndustryIndicatorValue
            WHERE indicator_id = $i AND value IS NOT NULL
            ORDER BY trade_date DESC LIMIT 60;
            """;
        cmd.Parameters.AddWithValue("$i", item.Expr);

        var series = new List<(DateTime, double)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    series.Add((d, r.GetDouble(1)));

        series.Reverse();   // 查询是倒序，ReadLatestDistinct 要升序
        return WatchEvaluator.ReadLatestDistinct(series);
    }

    private static WatchReading ReadPriceMa(SqliteConnection conn, WatchItem item)
    {
        if (!int.TryParse(item.Expr, out var period) || period <= 0) return new WatchReading(null);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT period_start, close FROM Bar
            WHERE code = $c AND granularity = 'day'
            ORDER BY period_start DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$c", item.Code);
        cmd.Parameters.AddWithValue("$n", period + 1);

        var rows = new List<(DateTime Date, double Close)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    rows.Add((d, r.GetDouble(1)));

        // 要 period+1 根才能同时算出今天和昨天的 MA——只有两天都算得出来，
        // 才谈得上"今天跌破、昨天还在上面"。少一根就只能判"现在低于均线"，那是状态不是事件，
        // 会每天都报一次。
        if (rows.Count < period + 1) return new WatchReading(null);
        rows.Reverse();

        // 值＝**对均线的偏离率**（%），不是收盘价——这样阈值固定为 0，
        // CrossDown 就精确表达"由上到下穿越均线"。用收盘价当值的话阈值得是动态的 MA，
        // 而 WatchItem.Threshold 是静态的，对不上。
        var maToday = rows.TakeLast(period).Average(x => x.Close);
        var maPrev = rows.Take(rows.Count - 1).TakeLast(period).Average(x => x.Close);
        var devToday = maToday == 0 ? 0 : (rows[^1].Close / maToday - 1) * 100;
        var devPrev = maPrev == 0 ? 0 : (rows[^2].Close / maPrev - 1) * 100;
        return new WatchReading(rows[^1].Date, devToday, devPrev);
    }

    private static WatchReading ReadMargin(SqliteConnection conn, WatchItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trade_date, margin_balance FROM MarginDetail
            WHERE code = $c AND margin_balance IS NOT NULL
            ORDER BY trade_date DESC LIMIT 2;
            """;
        cmd.Parameters.AddWithValue("$c", item.Code);
        return TwoRowReading(cmd);
    }

    private static WatchReading ReadHolderCount(SqliteConnection conn, WatchItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT report_date, holder_num FROM ShareholderCount
            WHERE code = $c AND holder_num IS NOT NULL
            ORDER BY report_date DESC LIMIT 2;
            """;
        cmd.Parameters.AddWithValue("$c", item.Code);
        return TwoRowReading(cmd);
    }

    /// <summary>L0 事件表：有没有新行。Expr＝表名，白名单限定。</summary>
    private static WatchReading ReadEventTable(
        SqliteConnection conn, WatchItem item, IReadOnlyDictionary<Guid, string>? lastStages)
    {
        // ⚠ 表名不能拼进 SQL 而不校验——Expr 来自 json，白名单是唯一安全的做法。
        var (table, dateCol, codeCol) = item.Expr switch
        {
            "EarningsForecast" => ("EarningsForecast", "notice_date", "code"),
            "EarningsSchedule" => ("EarningsSchedule", "appoint_date", "code"),
            "ShareLift" => ("ShareLift", "free_date", "code"),
            "HolderChange" => ("HolderChange", "notice_date", "code"),
            "Dividend" => ("Dividend", "announce_date", "code"),
            "Lhb" => ("Lhb", "trade_date", "stock_code"),
            _ => ("", "", ""),
        };
        if (table.Length == 0) return new WatchReading(null);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX({dateCol}) FROM {table} WHERE {codeCol} = $c;";
        cmd.Parameters.AddWithValue("$c", item.Code);
        var v = cmd.ExecuteScalar();
        if (v is not string s || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new WatchReading(null);

        var prev = lastStages != null && lastStages.TryGetValue(item.ItemId, out var p) ? p : null;
        return new WatchReading(d, null, null, $"{item.Expr} 最新 {d:yyyy-MM-dd}", prev);
    }

    private static WatchReading TwoRowReading(SqliteCommand cmd)
    {
        var rows = new List<(DateTime, double)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                rows.Add((d, r.GetDouble(1)));
        if (rows.Count == 0) return new WatchReading(null);
        return new WatchReading(rows[0].Item1, rows[0].Item2,
            rows.Count >= 2 ? rows[1].Item2 : null);
    }

    private static DateTime? ParseDate(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null
           : DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
