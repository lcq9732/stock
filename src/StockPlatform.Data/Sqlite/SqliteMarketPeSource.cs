using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 一次性读出算「全市场/行业 PE 分位」所需的全部输入（2026-09-14）。只读、不写。
///
/// ════ 为什么要专门一个类 ════
/// 分位要的是全市场 3900 多只票的 PE，逐只走 <c>GetAllByCode</c> 是 3900 次查询。
/// 这里四条 SQL 一次捞完（约 1~2 秒），比逐只快两个数量级。
///
/// ════ ⚠ 口径必须跟 PE 那一行完全一致 ════
/// <c>PE = 收盘价 × 总股本 ÷ TTM归母净利</c>，三个输入一个都不能换：
///   · 股数用 <c>FundamentalMetric.total_shares</c>，**不是**财报的 <c>share_capital</c>
///     ——后者是实收资本（金额），面值不是 1 元的票会错到离谱（中芯国际差 35 倍）；
///   · TTM 调 <see cref="FinancialAnalyzer.TtmFromCumulative"/>，不在这里另写一遍；
///   · 价格取本地最新一根日K的收盘，跟窗口标题上显示的那个价是同一个。
/// 参考分位跟个股值口径不同的话，比了等于没比。
/// </summary>
public class SqliteMarketPeSource
{
    private readonly string _connectionString;

    public SqliteMarketPeSource(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";

    /// <summary>全市场盈利股的 PE（code → PE），以及行业归属（code → 一级/二级行业名）。</summary>
    public (Dictionary<string, double> Pes,
            Dictionary<string, (string? Level1, string? Level2)> Industries) Read()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var shares = ReadTotalShares(conn);
        var price = ReadLatestClose(conn);
        var pes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (code, ttm) in ReadTtmProfit(conn))
        {
            if (ttm <= 0) continue;                                  // 只统计盈利股
            if (!shares.TryGetValue(code, out var s) || s <= 0) continue;
            if (!price.TryGetValue(code, out var p) || p <= 0) continue;
            pes[code] = p * s / ttm;
        }
        return (pes, ReadIndustries(conn));
    }

    /// <summary>最新一个 as_of_date 那一批总股本。</summary>
    private static Dictionary<string, double> ReadTotalShares(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, value FROM FundamentalMetric
            WHERE metric_key = 'total_shares'
              AND as_of_date = (SELECT MAX(as_of_date) FROM FundamentalMetric WHERE metric_key = 'total_shares')
              AND value > 0;
            """;
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetDouble(1);
        return map;
    }

    /// <summary>
    /// 往回捞几天的日线来定"最新收盘"。<b>不是可调参数，是性能命门</b>：Bar 有一千多万行，
    /// 不带时间下界的 <c>GROUP BY code</c> 要 4 秒以上（实测 4.21s），带上之后 0.20s——
    /// 快 21 倍，而窗口内拿不到价的只多出 320 个标的，全是长期停牌/已摘牌的，
    /// 本来就不该进当期分位。
    ///
    /// 45 天的由来跟 <see cref="SqliteMarketWatchSource"/> 一致：最长的春节休市加前后周末也就
    /// 十来天，45 天留足了余量。真要是库停更超过 45 天，下面会自动退回全表查询。
    /// </summary>
    private const int CloseLookbackDays = 45;

    /// <summary>
    /// 每只票本地最新一根日K的收盘价。
    ///
    /// ⚠ 跟个股那一行取价的口径有个**刻意的小差别**：窗口那边用的是"全历史最新一根"，
    /// 这里只看最近 <see cref="CloseLookbackDays"/> 天。所以一只停牌很久的票，它自己的 PE
    /// 照样能按陈旧价算出来显示，却不会进分位样本——这是对的：拿三个月前的价去撑行业中位，
    /// 污染的是所有同行的参考值。
    /// </summary>
    private static Dictionary<string, double> ReadLatestClose(SqliteConnection conn)
    {
        var map = ReadLatestCloseSince(conn, DateTime.Today.AddDays(-CloseLookbackDays));
        // 窗口内一行都没有＝本地库停更超过 45 天（或是全新的库）。这时宁可慢 4 秒也要给出结果。
        return map.Count > 0 ? map : ReadLatestCloseSince(conn, null);
    }

    private static Dictionary<string, double> ReadLatestCloseSince(SqliteConnection conn, DateTime? since)
    {
        var gate = since.HasValue ? "AND period_start >= $since" : "";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT b.code, b.close FROM Bar b
            JOIN (SELECT code, MAX(period_start) AS ps FROM Bar
                  WHERE granularity = 'day' {gate} GROUP BY code) m
              ON b.code = m.code AND b.period_start = m.ps
            WHERE b.granularity = 'day' AND b.close > 0 {gate.Replace("period_start", "b.period_start")};
            """;
        if (since.HasValue)
            cmd.Parameters.AddWithValue("$since", since.Value.ToString("yyyy-MM-dd"));
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetDouble(1);
        return map;
    }

    /// <summary>
    /// 每只票的 TTM 归母净利。只捞最近 3 年的 <c>np_parent</c>（TTM 最多用到上年年报），
    /// 然后交给 <see cref="FinancialAnalyzer.TtmFromCumulative"/> 算——口径只有那一份。
    /// </summary>
    private static Dictionary<string, double> ReadTtmProfit(SqliteConnection conn)
    {
        var byCode = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT code, report_date, value FROM FinancialReport
                WHERE metric_key = 'np_parent' AND value IS NOT NULL
                  AND report_date >= $from;
                """;
            cmd.Parameters.AddWithValue("$from", DateTime.Today.AddYears(-3).ToString("yyyy-MM-dd"));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.GetString(0);
                if (!DateTime.TryParse(r.GetString(1), CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d)) continue;
                if (!byCode.TryGetValue(code, out var periods))
                    byCode[code] = periods = new Dictionary<DateTime, double>();
                periods[d.Date] = r.GetDouble(2);
            }
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (code, periods) in byCode)
        {
            DateTime cur = default;
            foreach (var d in periods.Keys) if (d > cur) cur = d;
            if (cur == default) continue;

            // 缺某一期要传 null 而不是 0——0 会被当成"这期净利是零"，TTM 减出个假数。
            double? At(DateTime d) => periods.TryGetValue(d, out var v) ? v : null;
            var ttm = FinancialAnalyzer.TtmFromCumulative(
                cur, At(cur), At(new DateTime(cur.Year - 1, 12, 31)), At(cur.AddYears(-1)));
            if (ttm is { } v) result[code] = v;
        }
        return result;
    }

    /// <summary>
    /// 东财行业归属的一级/二级（<c>StockIndustryEm</c>）。三级不取——样本中位只有 8 只，
    /// 分位算不出意义（见 <c>IndustryPeStats</c>）。
    /// </summary>
    private static Dictionary<string, (string?, string?)> ReadIndustries(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, board_level, board_name FROM StockIndustryEm
            WHERE board_level IN (1, 2) AND board_name IS NOT NULL AND board_name <> '';
            """;
        var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var code = r.GetString(0);
            map.TryGetValue(code, out var cur);
            map[code] = r.GetInt32(1) == 1 ? (r.GetString(2), cur.Item2) : (cur.Item1, r.GetString(2));
        }
        return map;
    }
}
