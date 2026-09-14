using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// **市场层面**的观察项（2026-09-14）——不属于任何一只票的那些。**只读** current.sqlite。
///
/// ════ 为什么要从个股行里分出来 ════
/// "跌破 MA20"每只票都会有。熊市里几千只同时触发，逐票列在个股行里没有任何区分度，
/// 只会把真正个股独有的事（回购买了没、解禁多少）淹掉。
/// 而"今天 4127 只跌破 MA20，占全市场 68%"本身是有用的——
/// **今天是几千只一起跌，还是就它一只跌，含义完全不同**。
///
/// ⚠ 个股行里**仍然保留**自己那条 MA20（见 <see cref="SqliteStockEventSource"/>）。
/// 这里给的是广度背景，不是替代。
/// </summary>
public class SqliteMarketWatchSource
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>算 MA20 要往回捞几个自然日的日线。20 个交易日大约 28 天，留足节假日余量。</summary>
    private const int LookbackDays = 45;

    private readonly string _connectionString;

    public SqliteMarketWatchSource(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";

    public IReadOnlyList<MarketWatchItem> Read()
    {
        var items = new List<MarketWatchItem>();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            if (MaBreadth(conn, 20) is { } b) items.Add(b);
            items.AddRange(IndicatorMoves(conn));
        }
        catch
        {
            // 缺表/缺数据不该让整个面板空掉
        }
        return items.OrderByDescending(x => x.Date).ToList();
    }

    /// <summary>
    /// 全市场跌破 MA20 的只数与占比。
    ///
    /// ⚠ 只统计**最新交易日有行情**的票——停牌、退市的票最后一根日线停在过去，
    /// 把它们算进分母会让占比长期偏低，且那个"跌破"是几年前的状态不是今天的事。
    ///
    /// ⚠ <c>LENGTH(code) = 6</c> 是在**筛掉 ETF 和指数**（2026-09-14 实机踩到）：
    /// 它们带市场前缀存（sh510300 / sz159915，8 位），不加这条分母是 7168——
    /// 比 A 股总数还多一千六，占比自然就不是"全市场股票"的占比了。个股一律是裸 6 位码。
    /// </summary>
    private static MarketWatchItem? MaBreadth(SqliteConnection conn, int period)
    {
        using var maxCmd = conn.CreateCommand();
        maxCmd.CommandText = "SELECT MAX(period_start) FROM Bar WHERE granularity = 'day';";
        if (maxCmd.ExecuteScalar() is not string maxText
            || !DateTime.TryParse(maxText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
            return null;

        var since = asOf.AddDays(-LookbackDays).ToString(DateFormat, CultureInfo.InvariantCulture);

        // 一次查完，别按票循环——全市场五千多只，一只一条 SQL 会跑到天荒地老。
        // 走 ix_bar_gran_date(granularity, period_start)，日期区间是索引前缀，扫的是十来万行。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH recent AS (
                SELECT code, period_start, close,
                       ROW_NUMBER() OVER (PARTITION BY code ORDER BY period_start DESC) AS rn
                FROM Bar
                WHERE granularity = 'day' AND period_start >= $since AND close IS NOT NULL
                  AND LENGTH(code) = 6
            ),
            win AS (
                SELECT code, AVG(close) AS ma, COUNT(*) AS n,
                       MAX(CASE WHEN rn = 1 THEN close END)        AS last_close,
                       MAX(CASE WHEN rn = 1 THEN period_start END) AS last_date
                FROM recent WHERE rn <= $p GROUP BY code
            )
            SELECT SUM(CASE WHEN last_close < ma THEN 1 ELSE 0 END), COUNT(*)
            FROM win WHERE n = $p AND ma > 0 AND last_date = $asOf;
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$p", period);
        cmd.Parameters.AddWithValue("$asOf", maxText);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;

        long below = r.GetInt64(0), total = r.GetInt64(1);
        if (total == 0) return null;

        return new MarketWatchItem(asOf,
            $"全市场 {below} 只跌破 MA{period}（占 {below * 100.0 / total:0.#}%，共 {total} 只）");
    }

    /// <summary>
    /// 自选票挂着的行业指标里，**最近真的动了**的那些。
    ///
    /// ⚠ 必须跟"上一个**不同值**"比，不能跟上一行比（doc/watch-item-design.md §4.3）：
    /// 这些是自然日序列，周末沿用周五的值、工作日也常连续同值，跟上一行比会恒等于 0。
    /// </summary>
    private static IEnumerable<MarketWatchItem> IndicatorMoves(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        // 只看自选票挂上的指标——全库四千多个指标，没挂的跟我无关。
        cmd.CommandText = """
            SELECT DISTINCT s.indicator_id, i.name, i.unit
            FROM StockWatchIndicator s
            JOIN IndustryIndicator i ON i.indicator_id = s.indicator_id
            ORDER BY i.name;
            """;

        var indicators = new List<(string Id, string Name, string Unit)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                indicators.Add((r.GetString(0),
                                r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                                r.IsDBNull(2) ? "" : r.GetString(2)));

        foreach (var (id, name, unit) in indicators)
        {
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT trade_date, value FROM IndustryIndicatorValue
                WHERE indicator_id = $i AND value IS NOT NULL
                ORDER BY trade_date DESC LIMIT 60;
                """;
            q.Parameters.AddWithValue("$i", id);

            var series = new List<(DateTime Date, double Value)>();
            using (var r = q.ExecuteReader())
                while (r.Read())
                    if (DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        series.Add((d, r.GetDouble(1)));

            if (series.Count == 0) continue;

            var latest = series[0];
            // 往回找第一个**不同**的值，那才是上一次变动
            var prev = series.Skip(1).FirstOrDefault(x => Math.Abs(x.Value - latest.Value) > 1e-9);
            var text = $"{name} {latest.Value:0.####}{unit}";
            if (prev != default && prev.Value != 0)
            {
                var pct = (latest.Value / prev.Value - 1) * 100;
                text += $"（较 {prev.Date:MM-dd} {pct:+0.##;-0.##}%）";
            }
            yield return new MarketWatchItem(latest.Date, text);
        }
    }
}
