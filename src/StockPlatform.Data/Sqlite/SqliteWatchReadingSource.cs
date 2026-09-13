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
    /// <summary>库里日期列一律是这个格式，比较用字符串比就行（ISO 格式字典序＝时间序）。</summary>
    private const string DateFormat = "yyyy-MM-dd";

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
                WatchKind.ScheduleAhead => ReadScheduleAhead(conn, item),
                WatchKind.EventRecent => ReadEventRecent(conn, item),
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
        //
        // ⚠ **null 和 0 必须在界面上也分得开**（2026-09-11 用户反馈）：
        // 0＝公告明说"尚未实施"（真答案），null＝正文没抽到（数据缺失）。
        // 原来 null 显示成裸的"进展"，跟抽取失败完全看不出区别——而金额抽取率只有六成多，
        // 这种行不少。显示成"金额没读出来"，人才知道该自己点开公告看。
        var text = amount switch
        {
            null => $"{stage}（金额没读出来，需查原文）",
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

        // ⚠ 偏离率是**内部口径**，别让它漏到界面上（2026-09-11 用户反馈）：
        // 原来消息直接写"下穿 0（0.1981 → -0.4497）"，人根本读不出这是"跌破 MA20"。
        // 这里把人话准备好，求值器优先用它。
        var text = $"收盘 {rows[^1].Close:0.##}，MA{period} {maToday:0.##}"
                   + $"（低 {Math.Abs(devToday):0.##}%，昨天还高 {devPrev:0.##}%）";
        return new WatchReading(rows[^1].Date, devToday, devPrev, text);
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

    /// <summary>
    /// 表名 → (表, 日期列, 股票码列)。⚠ 表名不能不校验就拼进 SQL——Expr 来自 json，
    /// 白名单是唯一安全的做法。
    /// </summary>
    private static (string Table, string DateCol, string CodeCol) Resolve(string expr) => expr switch
    {
        "EarningsForecast" => ("EarningsForecast", "notice_date", "code"),
        "EarningsSchedule" => ("EarningsSchedule", "appoint_date", "code"),
        "ShareLift" => ("ShareLift", "free_date", "code"),
        "HolderChange" => ("HolderChange", "notice_date", "code"),
        "Dividend" => ("Dividend", "announce_date", "code"),
        "Lhb" => ("Lhb", "trade_date", "stock_code"),
        _ => ("", "", ""),
    };

    /// <summary>
    /// **未来日程**（解禁、预约披露日）：取**最近一次将来的**，值＝还有几天。
    ///
    /// ⚠ 不能用 <c>MAX(日期)</c>：这类表是日程表不是历史表（数据字典里 ShareLift 明写着
    /// "含未来解禁计划"），MAX 拿到的是**最远**那次——实测取到了 2030 年的解禁。
    /// </summary>
    private static WatchReading ReadScheduleAhead(SqliteConnection conn, WatchItem item)
    {
        var (table, dateCol, codeCol) = Resolve(item.Expr);
        if (table.Length == 0) return new WatchReading(null);

        var today = DateTime.Today;
        var todayText = today.ToString(DateFormat, CultureInfo.InvariantCulture);

        // 解禁要连**量**一起报：只说"哪天解禁"没用，解禁 0.1% 和解禁 30% 是完全不同的事
        // （2026-09-11 用户反馈）。同一天可能有好几批（不同 share_type），按日期聚合。
        if (item.Expr == "ShareLift")
        {
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT free_date, SUM(lift_shares), SUM(lift_market_cap), SUM(free_ratio)
                FROM ShareLift
                WHERE code = $c AND free_date = (
                    SELECT MIN(free_date) FROM ShareLift WHERE code = $c AND free_date >= $today)
                GROUP BY free_date;
                """;
            q.Parameters.AddWithValue("$c", item.Code);
            q.Parameters.AddWithValue("$today", todayText);

            using var r = q.ExecuteReader();
            if (!r.Read()) return new WatchReading(null);
            if (!DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var fd))
                return new WatchReading(null);

            var ahead = (fd.Date - today).TotalDays;
            var parts = new List<string> { $"{fd:yyyy-MM-dd}（还有 {ahead:0} 天）" };
            if (!r.IsDBNull(1) && r.GetDouble(1) > 0) parts.Add($"{Fmt(r.GetDouble(1))}股");
            if (!r.IsDBNull(2) && r.GetDouble(2) > 0) parts.Add($"{Fmt(r.GetDouble(2))}元");
            double? freeRatio = r.IsDBNull(3) ? null : r.GetDouble(3);
            if (freeRatio is > 0) parts.Add($"占流通 {freeRatio:0.##}%");

            // Magnitude＝占流通比，**判轻重用**（解禁 74 股不该跟占流通 30% 同档）。
            // 跟 Value（还有几天，判触发用）分开传，混用会把"还有 27 天"当成"占 27%"。
            return new WatchReading(fd, ahead, null, string.Join("，", parts), Magnitude: freeRatio);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT MIN({dateCol}) FROM {table}
            WHERE {codeCol} = $c AND {dateCol} >= $today;
            """;
        cmd.Parameters.AddWithValue("$c", item.Code);
        cmd.Parameters.AddWithValue("$today", todayText);

        if (cmd.ExecuteScalar() is not string s
            || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new WatchReading(null);

        var days = (d.Date - today).TotalDays;
        // TradeDate 记的是**那件事发生的日子**，不是求值那天——触发记录按它归档。
        return new WatchReading(d, days, null, $"{d:yyyy-MM-dd}（还有 {days:0} 天）");
    }

    /// <summary>大数按亿/万缩写，界面上一眼能比大小。</summary>
    private static string Fmt(double v)
        => Math.Abs(v) >= 1e8 ? $"{v / 1e8:0.##}亿"
           : Math.Abs(v) >= 1e4 ? $"{v / 1e4:0.##}万"
           : $"{v:0.##}";

    /// <summary>
    /// **已发生的事**（业绩预告、增减持、龙虎榜、分红）：取最新一条，值＝几天前发生。
    ///
    /// ⚠ 窗口由调用方的 Threshold 把关。不加窗口的话，"这票上一次上龙虎榜是 2011 年"
    /// 也会被当成事件报出来——那不是事件，是十五年没上过榜。
    /// </summary>
    private static WatchReading ReadEventRecent(SqliteConnection conn, WatchItem item)
    {
        var (table, dateCol, codeCol) = Resolve(item.Expr);
        if (table.Length == 0) return new WatchReading(null);

        // 分红要连**方案内容**一起报：只说"哪天有分红公告"没用，底仓关心的是派多少、到哪一步了
        // （2026-09-11 用户反馈）。口径是每 10 股，见数据字典 Dividend 节。
        if (item.Expr == "Dividend")
        {
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT announce_date, dividend_yuan, bonus_shares, transfer_shares, progress, ex_date
                FROM Dividend WHERE code = $c ORDER BY announce_date DESC LIMIT 1;
                """;
            q.Parameters.AddWithValue("$c", item.Code);

            using var r = q.ExecuteReader();
            if (!r.Read()) return new WatchReading(null);
            if (!DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ad))
                return new WatchReading(null);

            var ago = (DateTime.Today - ad.Date).TotalDays;
            var plan = new List<string>();
            if (!r.IsDBNull(1) && r.GetDouble(1) > 0) plan.Add($"派 {r.GetDouble(1):0.##} 元");
            if (!r.IsDBNull(2) && r.GetDouble(2) > 0) plan.Add($"送 {r.GetDouble(2):0.##} 股");
            if (!r.IsDBNull(3) && r.GetDouble(3) > 0) plan.Add($"转增 {r.GetDouble(3):0.##} 股");

            var text = $"{ad:yyyy-MM-dd}（{Ago(ago)}）"
                       + (plan.Count > 0 ? $" 每10股{string.Join("、", plan)}" : " 不分配")
                       + (r.IsDBNull(4) ? "" : $"（{r.GetString(4)}）")
                       + (r.IsDBNull(5) || r.GetString(5).Length == 0 ? "" : $"，除权 {r.GetString(5)[..10]}");
            return new WatchReading(ad, ago, null, text);
        }

        // 业绩预告：**预增还是预亏、幅度多少**才是信息，日期本身说明不了要不要管。
        // ⚠ 一次公告会有好几行（净利润／营业收入／扣非净利润各一行），只取"净利润"那行——
        // 取错口径会出现"营收略增"却被当成利润预增。
        if (item.Expr == "EarningsForecast")
        {
            // ⚠ 口径名**不能写死成「净利润」**（2026-09-11 踩过）：各家用词不一样，
            // 徐工机械写的是「归属于上市公司股东的净利润」，写死就匹配不上、整条退回只显示日期。
            // 按优先级挑：归母 > 净利润 > 扣非 > 其它，最后才退而求其次拿任意一行。
            // 「每股收益」那类口径的 amplitude 常年为空（全库 4%），排在最后。
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT notice_date, predict_type, amplitude_lower, amplitude_upper, report_date
                FROM EarningsForecast
                WHERE code = $c
                ORDER BY notice_date DESC,
                         CASE
                           WHEN predict_finance LIKE '%归属于上市公司股东的净利润%' THEN 0
                           WHEN predict_finance = '净利润' THEN 1
                           WHEN predict_finance LIKE '%扣除非经常%' THEN 2
                           WHEN predict_finance LIKE '%每股%' THEN 9
                           ELSE 5
                         END,
                         CASE WHEN amplitude_lower IS NULL THEN 1 ELSE 0 END
                LIMIT 1;
                """;
            q.Parameters.AddWithValue("$c", item.Code);
            using var r = q.ExecuteReader();
            if (r.Read() && DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var nd))
            {
                var ago = (DateTime.Today - nd.Date).TotalDays;
                var bits = new List<string> { $"{nd:yyyy-MM-dd}（{Ago(ago)}）" };
                if (!r.IsDBNull(4)) bits.Add($"{r.GetString(4)[..10]} 报告期");
                if (!r.IsDBNull(1)) bits.Add(r.GetString(1));            // 略增/预亏/扭亏…
                if (!r.IsDBNull(2) && !r.IsDBNull(3))
                {
                    double lo = r.GetDouble(2), hi = r.GetDouble(3);
                    bits.Add(Math.Abs(hi - lo) < 0.01 ? $"净利同比 {lo:+0.##;-0.##}%"
                                                      : $"净利同比 {lo:0.##}~{hi:0.##}%");
                }
                return new WatchReading(nd, ago, null, string.Join("，", bits));
            }
            // 没有"净利润"口径的（有些只报营收）就退回通用查询，别整条丢掉
        }

        // 股东增减持：**增还是减、多少股、还剩多少**。同一天常有好几笔（同一股东分批），按方向聚合。
        // ⚠ change_shares 的单位是**万股**（实测 -610.0 表示减持 610 万股），别当成股。
        if (item.Expr == "HolderChange")
        {
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT notice_date, direction, SUM(change_shares), SUM(change_ratio), MIN(after_ratio)
                FROM HolderChange
                WHERE code = $c AND notice_date = (SELECT MAX(notice_date) FROM HolderChange WHERE code = $c)
                GROUP BY notice_date, direction
                ORDER BY ABS(SUM(change_shares)) DESC LIMIT 1;
                """;
            q.Parameters.AddWithValue("$c", item.Code);
            using var r = q.ExecuteReader();
            if (r.Read() && DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var nd))
            {
                var ago = (DateTime.Today - nd.Date).TotalDays;
                var bits = new List<string> { $"{nd:yyyy-MM-dd}（{Ago(ago)}）" };
                if (!r.IsDBNull(1)) bits.Add(r.GetString(1));            // 增持/减持
                if (!r.IsDBNull(2)) bits.Add($"{Math.Abs(r.GetDouble(2)):0.##} 万股");
                if (!r.IsDBNull(3)) bits.Add($"占 {Math.Abs(r.GetDouble(3)):0.##}%");
                if (!r.IsDBNull(4)) bits.Add($"剩 {r.GetDouble(4):0.##}%");
                return new WatchReading(nd, ago, null, string.Join("，", bits));
            }
        }

        // 龙虎榜：**为什么上榜、席位净买入多少**。同一天常因多个原因各上一次，按净额取最大那条。
        if (item.Expr == "Lhb")
        {
            using var q = conn.CreateCommand();
            q.CommandText = """
                SELECT trade_date, reason, billboard_net_amt, change_rate, turnover_rate
                FROM Lhb
                WHERE stock_code = $c AND trade_date = (SELECT MAX(trade_date) FROM Lhb WHERE stock_code = $c)
                ORDER BY ABS(COALESCE(billboard_net_amt, 0)) DESC LIMIT 1;
                """;
            q.Parameters.AddWithValue("$c", item.Code);
            using var r = q.ExecuteReader();
            if (r.Read() && DateTime.TryParse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var td))
            {
                var ago = (DateTime.Today - td.Date).TotalDays;
                var bits = new List<string> { $"{td:yyyy-MM-dd}（{Ago(ago)}）" };
                if (!r.IsDBNull(3)) bits.Add($"当日 {r.GetDouble(3):+0.##;-0.##}%");
                if (!r.IsDBNull(2)) bits.Add($"席位净{(r.GetDouble(2) >= 0 ? "买" : "卖")} {Fmt(Math.Abs(r.GetDouble(2)))}元");
                if (!r.IsDBNull(1)) bits.Add(r.GetString(1));
                return new WatchReading(td, ago, null, string.Join("，", bits));
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX({dateCol}) FROM {table} WHERE {codeCol} = $c;";
        cmd.Parameters.AddWithValue("$c", item.Code);

        if (cmd.ExecuteScalar() is not string s
            || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return new WatchReading(null);

        var days = (DateTime.Today - d.Date).TotalDays;
        return new WatchReading(d, days, null, $"{d:yyyy-MM-dd}（{Ago(days)}）");
    }

    private static string Ago(double days) => days <= 0 ? "今天" : $"{days:0} 天前";

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
