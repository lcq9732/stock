using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 把一只票各张表里的事读成**事件叙述**（2026-09-14），给观察项页的"一股一行"用。
/// **只读** current.sqlite。
///
/// ════ 这是现在唯一一条取值路径 ════
/// 曾经还有个 <c>SqliteWatchReadingSource</c>，那个是给**求值**用的（取一个数、判要不要触发），
/// 这个是给**阅读**用的（把来龙去脉讲成人话）。求值那条线 2026-09-15 整体退休了——
/// 它服务的推送/日报出口从来没做过。
///
/// ════ 谁进个股行、谁进市场观察 ════
/// 个股独有的（回购、解禁、预告、增减持、龙虎榜、分红）进个股行；
/// 跌破 MA20 这种每只票都有的，个股行里**保留自己那条数**（持有它就要看），
/// 同时由 <see cref="SqliteMarketWatchSource"/> 给出全市场广度——
/// 今天是几千只一起跌还是就它一只跌，含义完全不同。
/// </summary>
public class SqliteStockEventSource
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// 各类事项"多久以前就不用说了"的天数。
    ///
    /// ⚠ 必须有这道门槛（2026-09-14 实机踩到）：不设的话叙述里会冒出
    /// "2016-01-21 业绩预告 略增"、"2021-02-18 龙虎榜"——那不是事件，是**十年没有过事件**。
    /// 更糟的是它排在列里看着跟今年的公告一样，读的人会当成最新情况。
    /// （已退休的那套规则引擎当初为同一个原因加过时间窗，叙述这条线也得有。）
    ///
    /// 阈值按"这类事多久发生一次"定：预告一年 4 次、增减持不定期但一年总有、分红一年一次。
    /// 回购不设门槛——整轮叙述本身就是有价值的历史，而且已经按轮折叠过了。
    /// </summary>
    private const double StaleForecastDays = 200;    // 一年 4 次，半年够覆盖两次
    private const double StaleLhbDays = 180;         // 半年前上过榜跟现在没关系
    private const double StaleHolderDays = 365;
    private const double StaleDividendDays = 400;    // 年度分红，留出公告节奏的余量

    private readonly string _connectionString;

    public SqliteStockEventSource(string dbFilePath)
        => _connectionString = $"Data Source={dbFilePath};Mode=ReadOnly";

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    /// <summary>读这只票的全部事件，已按最终叙述顺序排好。</summary>
    public IReadOnlyList<WatchEvent> Read(string code)
    {
        var all = new List<WatchEvent>();
        try
        {
            using var conn = Open();
            all.AddRange(Buyback(conn, code));
            all.AddRange(ShareLift(conn, code));
            all.AddRange(EarningsForecast(conn, code));
            all.AddRange(HolderChange(conn, code));
            all.AddRange(Lhb(conn, code));
            all.AddRange(Dividend(conn, code));
            all.AddRange(PriceMa(conn, code, 20));
            all.AddRange(Indicators(conn, code));
        }
        catch
        {
            // 某张表还没建（对应任务没跑过）不该让整只票读不出来
        }
        return WatchEventComposer.Compose(all);
    }

    // ── 回购：跨几个月的过程，交给 BuybackTimeline 拆轮叙述 ──
    private static IEnumerable<WatchEvent> Buyback(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT announce_date, stage, as_of_date, cum_amount,
                   plan_cap_price, plan_amount_low, plan_amount_high
            FROM PlanAnnouncement WHERE code = $c AND kind = '回购' ORDER BY announce_date;
            """;
        cmd.Parameters.AddWithValue("$c", code);

        var rows = new List<PlanAnnouncement>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add(new PlanAnnouncement
                {
                    Code = code, Kind = PlanKind.Buyback,
                    AnnounceDate = Dt(r, 0) ?? default,
                    Stage = r.GetString(1),
                    AsOfDate = Dt(r, 2),
                    CumAmount = Dbl(r, 3),
                    PlanCapPrice = Dbl(r, 4),
                    PlanAmountLow = Dbl(r, 5),
                    PlanAmountHigh = Dbl(r, 6),
                });

        // 每轮给一个 Round，让 Composer 把两轮当两组分别排序
        var built = BuybackTimeline.Build(rows);
        int round = 0;
        WatchEvent? prev = null;
        foreach (var e in built)
        {
            // 主行（Indent=0）开启新的一组
            if (e.Indent == 0 || prev is null) round++;
            prev = e;
            yield return e with { Round = round };
        }
    }

    // ── 限售解禁：只报**未来最近一次**，量是判轻重的依据 ──
    private static IEnumerable<WatchEvent> ShareLift(SqliteConnection conn, string code)
    {
        var today = DateTime.Today;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT free_date, SUM(lift_shares), SUM(lift_market_cap), SUM(free_ratio)
            FROM ShareLift
            WHERE code = $c AND free_date = (
                SELECT MIN(free_date) FROM ShareLift WHERE code = $c AND free_date >= $today)
            GROUP BY free_date;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$today", today.ToString(DateFormat, CultureInfo.InvariantCulture));

        using var r = cmd.ExecuteReader();
        if (!r.Read() || Dt(r, 0) is not { } fd) yield break;

        var bits = new List<string>();
        if (Dbl(r, 1) is > 0 and var sh) bits.Add($"{Fmt(sh)}股");
        if (Dbl(r, 2) is > 0 and var cap) bits.Add($"{Fmt(cap)}元");
        if (Dbl(r, 3) is > 0 and var ratio) bits.Add($"占流通 {ratio:0.##}%");

        var ahead = (fd.Date - today).TotalDays;
        yield return new WatchEvent(fd, WatchCategory.ShareLift,
            $"限售解禁  {string.Join("，", bits)}　（还有 {ahead:0} 天）", IsFuture: true);
    }

    // ── 业绩预告：口径按优先级挑，别写死"净利润"（各家用词不一样）──
    private static IEnumerable<WatchEvent> EarningsForecast(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT notice_date, predict_type, amplitude_lower, amplitude_upper, report_date
            FROM EarningsForecast WHERE code = $c
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
        cmd.Parameters.AddWithValue("$c", code);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || Dt(r, 0) is not { } nd) yield break;
        if (IsStale(nd, StaleForecastDays)) yield break;

        var bits = new List<string> { "业绩预告" };
        if (!r.IsDBNull(1)) bits.Add(r.GetString(1));
        if (Dbl(r, 2) is { } lo && Dbl(r, 3) is { } hi)
            bits.Add(Math.Abs(hi - lo) < 0.01 ? $"净利同比 {lo:+0.##;-0.##}%"
                                              : $"净利同比 {lo:0.##}~{hi:0.##}%");
        if (Dt(r, 4) is { } rd) bits.Add($"{rd:yyyy-MM-dd} 报告期");
        yield return new WatchEvent(nd, WatchCategory.EarningsForecast, string.Join("  ", bits));
    }

    // ── 股东增减持：同一天多笔按方向聚合。⚠ change_shares 单位是万股 ──
    private static IEnumerable<WatchEvent> HolderChange(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT notice_date, direction, SUM(change_shares), SUM(change_ratio), MIN(after_ratio)
            FROM HolderChange
            WHERE code = $c AND notice_date = (SELECT MAX(notice_date) FROM HolderChange WHERE code = $c)
            GROUP BY notice_date, direction
            ORDER BY ABS(SUM(change_shares)) DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$c", code);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || Dt(r, 0) is not { } nd) yield break;
        if (IsStale(nd, StaleHolderDays)) yield break;

        var bits = new List<string> { r.IsDBNull(1) ? "股东变动" : $"股东{r.GetString(1)}" };
        if (Dbl(r, 2) is { } sh) bits.Add($"{Math.Abs(sh):0.##} 万股");
        if (Dbl(r, 3) is { } ratio) bits.Add($"占 {Math.Abs(ratio):0.##}%");
        if (Dbl(r, 4) is { } after) bits.Add($"剩 {after:0.##}%");
        yield return new WatchEvent(nd, WatchCategory.HolderChange, string.Join("  ", bits));
    }

    // ── 龙虎榜：为什么上榜 + 席位净买入 ──
    private static IEnumerable<WatchEvent> Lhb(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trade_date, reason, billboard_net_amt, change_rate
            FROM Lhb WHERE stock_code = $c
              AND trade_date = (SELECT MAX(trade_date) FROM Lhb WHERE stock_code = $c)
            ORDER BY ABS(COALESCE(billboard_net_amt, 0)) DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$c", code);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || Dt(r, 0) is not { } td) yield break;
        if (IsStale(td, StaleLhbDays)) yield break;

        var bits = new List<string> { "龙虎榜" };
        if (Dbl(r, 3) is { } chg) bits.Add($"当日 {chg:+0.##;-0.##}%");
        if (Dbl(r, 2) is { } net) bits.Add($"席位净{(net >= 0 ? "买" : "卖")} {Fmt(Math.Abs(net))}元");
        if (!r.IsDBNull(1)) bits.Add(r.GetString(1));
        yield return new WatchEvent(td, WatchCategory.Lhb, string.Join("  ", bits));
    }

    // ── 分红：口径是每 10 股 ──
    private static IEnumerable<WatchEvent> Dividend(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT announce_date, dividend_yuan, bonus_shares, transfer_shares, progress, ex_date
            FROM Dividend WHERE code = $c ORDER BY announce_date DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$c", code);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || Dt(r, 0) is not { } ad) yield break;
        if (IsStale(ad, StaleDividendDays)) yield break;

        var plan = new List<string>();
        if (Dbl(r, 1) is > 0 and var d) plan.Add($"派 {d:0.##} 元");
        if (Dbl(r, 2) is > 0 and var b) plan.Add($"送 {b:0.##} 股");
        if (Dbl(r, 3) is > 0 and var t) plan.Add($"转增 {t:0.##} 股");

        var text = "分红方案  " + (plan.Count > 0 ? $"每10股{string.Join("、", plan)}" : "不分配");
        // ⚠ progress 那列本身就可能是「不分配」——不去重会读出"不分配（不分配）"（2026-09-14 实测）
        var progress = r.IsDBNull(4) ? "" : r.GetString(4);
        if (progress.Length > 0 && !text.EndsWith(progress, StringComparison.Ordinal))
            text += $"（{progress}）";
        if (!r.IsDBNull(5) && r.GetString(5).Length >= 10) text += $"，除权 {r.GetString(5)[..10]}";
        yield return new WatchEvent(ad, WatchCategory.Dividend, text);
    }

    // ── 行情：对 MA 的偏离。⚠ 只在**跌破**时才说，站在均线上方是常态、不值一提 ──
    private static IEnumerable<WatchEvent> PriceMa(SqliteConnection conn, string code, int period)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT period_start, close FROM Bar
            WHERE code = $c AND granularity = 'day' ORDER BY period_start DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$n", period);

        var rows = new List<(DateTime Date, double Close)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (Dt(r, 0) is { } d) rows.Add((d, r.GetDouble(1)));

        if (rows.Count < period) yield break;
        rows.Reverse();

        var ma = rows.TakeLast(period).Average(x => x.Close);
        var last = rows[^1];
        if (ma <= 0 || last.Close >= ma) yield break;      // 没跌破就不说

        var dev = (last.Close / ma - 1) * 100;
        yield return new WatchEvent(last.Date, WatchCategory.Quote,
            $"跌破 MA{period}  收盘 {last.Close:0.##}，MA{period} {ma:0.##}，低 {Math.Abs(dev):0.##}%");
    }

    /// <summary>这只票**同时**要显示的行业指标数上限。挂得再多，一行叙述里塞五条外部指标也没人读。</summary>
    private const int MaxIndicators = 3;

    /// <summary>
    /// 挂在这只票上的行业指标（2026-09-14 加）。
    ///
    /// ════ 为什么个股行里也要显示 ════
    /// 碳酸锂指数在市场观察区已经有一条了，但那是"市场上发生了什么"。
    /// 用户的要求是：**知道它对哪只票有影响的，就该显示在那只票里**——
    /// 看宁德的时候不用先记住"碳酸锂跌了 3.6%"再去另一个区找它跟谁有关。
    /// 谁受影响是 <c>StockWatchIndicator</c> 回答的（板块规则铺出来的，见 WatchIndicatorRuleEngine），
    /// 连"为什么挂它"那句 reason 一起带出来。
    ///
    /// ⚠ 必须跟"上一个**不同值**"比，不能跟上一行比（doc/watch-item-design.md §4.3）：
    /// 这些是自然日序列，周末沿用周五的值、工作日也常连续同值，跟上一行比会恒等于 0。
    /// </summary>
    private static IEnumerable<WatchEvent> Indicators(SqliteConnection conn, string code)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.indicator_id, i.name, i.unit, s.reason
            FROM StockWatchIndicator s
            JOIN IndustryIndicator i ON i.indicator_id = s.indicator_id
            WHERE s.code = $c
            ORDER BY COALESCE(s.weight, 999) LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$n", MaxIndicators);

        var links = new List<(string Id, string Name, string Unit, string Reason)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                links.Add((r.GetString(0),
                           r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                           r.IsDBNull(2) ? "" : r.GetString(2),
                           r.IsDBNull(3) ? "" : r.GetString(3)));

        foreach (var (id, name, unit, reason) in links)
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
                    if (Dt(r, 0) is { } d) series.Add((d, r.GetDouble(1)));

            if (series.Count == 0) continue;

            var latest = series[0];
            var prev = series.Skip(1).FirstOrDefault(x => Math.Abs(x.Value - latest.Value) > 1e-9);
            var text = $"{name} {latest.Value:0.####}{unit}";
            if (prev != default && prev.Value != 0)
                text += $"（较 {prev.Date:MM-dd} {(latest.Value / prev.Value - 1) * 100:+0.##;-0.##}%）";
            // reason 说的是"为什么这条指标跟这只票有关"——不能丢，但也不必天天占半行正文，
            // 所以挂进 ToolTip（2026-09-14 改）。分析详情窗右栏窄，正文每个字都金贵。
            yield return new WatchEvent(latest.Date, WatchCategory.Indicator, text,
                Tip: reason.Length > 0 ? reason : null);
        }
    }

    /// <summary>老到不该再提了。未来的日期永远不算陈旧（解禁走的是另一条路）。</summary>
    private static bool IsStale(DateTime date, double days)
        => (DateTime.Today - date.Date).TotalDays > days;

    private static double? Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static DateTime? Dt(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null
           : DateTime.TryParse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Fmt(double v)
        => Math.Abs(v) >= 1e8 ? $"{v / 1e8:0.##}亿"
           : Math.Abs(v) >= 1e4 ? $"{v / 1e4:0.##}万"
           : $"{v:0.##}";
}
