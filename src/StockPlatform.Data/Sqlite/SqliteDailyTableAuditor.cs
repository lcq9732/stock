using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// **日频表的覆盖体检**（2026-09-04 新增，全库数据体检的第三块）——K线之外那些"每个交易日
/// 全市场都该有一批数据"的表：资金净流入、融资余额、龙虎榜，以及 2026-09-03 接进来的三张东财表。
///
/// ════ 为什么要单独做一套，不能沿用 K线那套 ════
/// K线是"每只票每天一行"，缺不缺可以逐只按交易日历比对。这些表不是：融资余额只有两融标的有、
/// 龙虎榜只有当天上榜的票有、大宗交易只有成交过的票有——**按标的比对必然满屏误报**。
/// 能确定的只有一件事：一个交易日里，这张表**一行都没有**，那一定是漏抓了（全市场那天集体
/// 没有融资余额、集体没人上龙虎榜，不可能）。所以判据是三档：
///   · 空日：交易日历里有、这张表 0 行 → 报"缺"；
///   · 尾部滞后（2026-09-06 加，见 <see cref="Spec.LagDays"/>）：表里最新那天比交易日历落后
///     太多 → 报"落后 N 个交易日"；
///   · 偏少日：行数不到中位数的 <see cref="ThinRatio"/> → 只报"可疑"，不算缺。
///     2026-08-20 那种"跑早了只拿到三分之一"的半拉子轮次就是靠这一档才看得见。
///
/// ════ 为什么加尾部滞后这一档（2026-09-06） ════
/// 原来的区间上界取的是**表里最新那天**，于是"最近几天根本没抓到"这种最常见的失效，
/// 在体检眼里是一片空白——区间根本没延伸到那里。2026-09-06 实测：龙虎榜停在 09-01、
/// 落后了 3 个交易日（09-02/03/04 东财席位表明明都有上榜股），体检一个字都没报。
/// 判据不能简单地"上界改成 cutoff"：各表的正常发布节奏不同（融资余额是交易所 T+1），
/// 所以每张表自带一个"允许落后几天"，超了才报。
///
/// ════ 为什么只查表自己的 [最早, 最晚] 区间 ════
/// 各表的历史深度天差地别（东财资金流明细只给 120 天、融资余额能追到 2015 年）。
/// 卡在各自区间内，就不用在代码里维护"这张表该有多少年历史"这种迟早过期的知识。
///
/// ════ 为什么只报、不进待补名单 ════
/// 这些数据的补法各不相同（有的按交易日重抓、有的整段回补、东财那几张只有近三个月），
/// 塞进【重新拉取失败】那条统一的逐段重试里既补不对也说不清。所以体检报出是哪一天缺，
/// 并直接写明该跑哪一项——判断留给人，这跟板块指数、day_adj 的处理是一致的。
/// 例外是资金净流入：2026-09-06 起它的空日会记进 Manifest 交给【重新拉取失败】补，
/// 因为新浪那个源一次请求就返回整只票的全部历史，一轮就能把所有缺日一起补掉（见
/// FetchOrchestrator.FillMissingNetInflowDaysAsync）。
/// </summary>
public sealed class SqliteDailyTableAuditor(string dbPath)
{
    /// <summary>行数低于**邻近**中位数的这个比例就算"偏少"。0.2 很宽松——只想抓半拉子轮次，不想天天报噪声。</summary>
    private const double ThinRatio = 0.2;

    /// <summary>
    /// "偏少"的基准取**它前面**这么多个交易日的中位数，**不是全区间中位数**（2026-09-06 改）。
    ///
    /// 各表的日均行数在十年里本身就在变：大宗交易 2016 年日均 71 行、2017 年 195 行、
    /// 2024 年 83 行。拿全区间中位数（196）当基准，2016 和 2024 的正常日会被系统性判成"偏少"——
    /// 那次体检报出来的 6 个可疑日里有 5 个是这么来的（熔断日 2016-01-04 的成交额其实比邻近
    /// 还高 7%，靠成交额豁免根本挡不住，靠邻近行数一比就正常了）。
    /// </summary>
    private const int ThinWindowRadius = 10;

    /// <summary>
    /// **清淡日豁免**（2026-09-06）：当天全市场成交额不到邻近水平的这个比例时，不把它算成"偏少日"。
    ///
    /// 起因是大宗交易那 6 个"偏少日"全是误报：2016-01-04（熔断，13:34 就收市了）、
    /// 2016-01-07（熔断，9:57 收市）、2017-01-26 / 2018-02-14（春节前最后一个交易日）……
    /// 这些日子市场本身就没怎么交易，任何"行数"判据都会报，而它们根本没什么可补的。
    /// 用成交额归一之后，正常交易日的检出力一点不打折——半拉子轮次那天市场是正常成交的。
    /// </summary>
    private const double QuietRatio = 0.6;

    /// <summary>算"邻近成交额中位数"时前后各取这么多个交易日。十年里成交额量级变化极大
    /// （2016 年和 2024 年差好几倍），拿全区间中位数当基准会把整段低迷期都判成清淡日。</summary>
    private const int QuietWindowRadius = 10;

    /// <summary>表名/列名只允许这些字符。它们来自下面的常量表、不是外部输入，这道校验是防以后有人接错。</summary>
    private static readonly Regex SafeIdent = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <param name="Table">表名。</param>
    /// <param name="DateColumn">交易日那一列（全都是 yyyy-MM-dd 文本，跟 Bar.period_start 的
    /// "yyyy-MM-dd HH:mm:ss" 不同，比对时要截前 10 位）。</param>
    /// <param name="Label">日志里的名字。</param>
    /// <param name="HowToFill">缺了该跑哪一项——体检只报不补，这句话就是给人的下一步。</param>
    /// <param name="LagDays">允许比交易日历落后几个交易日（2026-09-06 加）。体检的 cutoff 本身
    /// 已经让开了最近两天，所以绝大多数表填 0 就够；融资余额是交易所 T+1 发布，填 1。</param>
    /// <param name="WindowDays">数据源只给最近这么多个交易日（2026-09-06 加，0＝不限）。
    /// 窗口之外的日子不参与 empty/thin 判定——东财资金流明细只给 120 天，而本地表里还留着
    /// 更早那些"逐只回补时先抓的几只票"的零星行，不排掉的话它们会被当成 47 天"只抓了一半"。</param>
    public sealed record Spec(
        string Table, string DateColumn, string Label, string HowToFill,
        int LagDays = 0, int WindowDays = 0);

    /// <summary>
    /// 要体检的日频表。**只放"每个交易日全市场必有一批"的表**——机构调研、限售解禁、股东增减持
    /// 那几张是按公告来的，某天一条都没有很正常，放进来只会天天误报。
    /// </summary>
    public static readonly Spec[] DailyTables =
    [
        new("NetInflow",       "period_start", "资金净流入",
            "空日已记进待补名单，跑一次【重新拉取失败】即可（一轮全市场，约 1.75 小时）"),
        new("MarginDetail",    "trade_date",   "融资余额",
            "【融资余额】只回看最近几个交易日，久远的那天要用【拉取区间数据】补",
            LagDays: 1),
        new("Lhb",             "trade_date",   "龙虎榜",
            "【龙虎榜】按天重跑（模式选「增量」、日期格填那一天）——"
            + "「首次整段回补」会把十年里的法定节假日挨个空抓一遍，补零星几天别用它"),
        new("NetInflowDetail", "trade_date",   "资金流明细(东财)",
            "【资金流明细】重跑一次——⚠ 数据源只给最近 120 天，更早的补不回来了",
            WindowDays: 120),
        new("LhbSeat",         "trade_date",   "龙虎榜席位(东财)", "【龙虎榜席位】重跑一次"),
        new("BlockTrade",      "trade_date",   "大宗交易(东财)",   "【市场事件】重跑一次"),
    ];

    /// <param name="Spec">哪张表。</param>
    /// <param name="From">这张表本地覆盖到的最早交易日（已按 <see cref="Spec.WindowDays"/> 裁过）。</param>
    /// <param name="To">最晚交易日。</param>
    /// <param name="TradingDays">区间内本该有数据的交易日数。</param>
    /// <param name="EmptyDays">一行都没有的交易日（按日期升序）。</param>
    /// <param name="ThinDays">行数不到**邻近**中位数 20% 的交易日（已排掉清淡日）。</param>
    /// <param name="MedianRows">有数据那些天的行数中位数（全区间），给人判断量级用——
    /// 判定用的是邻近中位数，见 <see cref="ThinWindowRadius"/>。</param>
    /// <param name="TailMissingDays">表里最新那天之后、还该有数据的交易日（2026-09-06 加）——
    /// 已经扣掉 <see cref="Spec.LagDays"/> 允许的滞后，非空就是真落后了。</param>
    public sealed record Result(
        Spec Spec, DateTime From, DateTime To, int TradingDays,
        List<DateTime> EmptyDays, List<(DateTime Day, int Rows)> ThinDays, int MedianRows,
        List<DateTime> TailMissingDays);

    /// <summary>
    /// 体检一张表。表不存在（老库还没抓过这类数据）或一行都没有时返回 null——那不是"缺数据"，
    /// 是"这项功能还没用过"，报出来只会让人以为出了问题。
    /// </summary>
    /// <param name="calendarCode">交易日锚，跟K线体检用同一个（上证指数的前复权日线）。</param>
    /// <param name="cutoff">这一天之后的日子不算缺（数据源可能还没更新完）。</param>
    public Result? Check(Spec spec, string calendarCode, DateTime cutoff)
    {
        if (!SafeIdent.IsMatch(spec.Table) || !SafeIdent.IsMatch(spec.DateColumn))
            throw new ArgumentException($"表名/列名不合法：{spec.Table}.{spec.DateColumn}");

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t;";
            probe.Parameters.AddWithValue("$t", spec.Table);
            if (Convert.ToInt32(probe.ExecuteScalar() ?? 0) == 0) return null;
        }

        // 交易日历（连当天全市场成交额一起取，清淡日豁免要用）。截前 10 位：Bar.period_start 是
        // "yyyy-MM-dd HH:mm:ss"，日频表是 "yyyy-MM-dd"。
        var calendar = ReadCalendar(conn, calendarCode, cutoff);
        if (calendar.Count == 0) return null;

        var rowsByDay = ReadRowCounts(conn, spec);
        if (rowsByDay.Count == 0) return null;

        string tableMax = rowsByDay.Keys.Max(StringComparer.Ordinal)!;
        string lo = WindowLowerBound(spec, calendar, rowsByDay.Keys.Min(StringComparer.Ordinal)!);

        var inRange = calendar
            .Select((c, i) => (c.Day, c.Amount, Index: i))
            .Where(c => string.CompareOrdinal(c.Day, lo) >= 0 && string.CompareOrdinal(c.Day, tableMax) <= 0)
            .ToList();
        if (inRange.Count == 0) return null;

        var rowSeries = inRange.Select(c => rowsByDay.GetValueOrDefault(c.Day)).ToList();
        var withRows = rowSeries.Where(n => n > 0).OrderBy(n => n).ToList();
        if (withRows.Count == 0) return null;
        int median = withRows[withRows.Count / 2];   // 只用来在日志里报"每日约 N 行"的量级

        var empty = new List<DateTime>();
        var thin = new List<(DateTime Day, int Rows)>();
        for (int i = 0; i < inRange.Count; i++)
        {
            var c = inRange[i];
            if (!TryParse(c.Day, out var day)) continue;
            int n = rowSeries[i];
            if (n == 0) { empty.Add(day); continue; }

            int nearby = TrailingMedian(rowSeries, i);   // 基准是它之前的水平，不是十年的总中位数
            if (nearby <= 0 || n >= nearby * ThinRatio) continue;
            if (IsQuietDay(calendar, c.Index)) continue;  // 长假前后：市场本身就没怎么交易
            thin.Add((day, n));
        }

        // 尾部滞后：表里最新那天之后、日历上还剩几个交易日（cutoff 之内），扣掉允许的滞后。
        var tail = calendar
            .Where(c => string.CompareOrdinal(c.Day, tableMax) > 0)
            .Select(c => TryParse(c.Day, out var d) ? d : (DateTime?)null)
            .Where(d => d.HasValue).Select(d => d!.Value)
            .ToList();
        if (tail.Count <= spec.LagDays) tail.Clear();

        TryParse(inRange[0].Day, out var from);
        TryParse(inRange[^1].Day, out var to);
        return new Result(spec, from, to, inRange.Count, empty, thin, median, tail);
    }

    private static List<(string Day, double Amount)> ReadCalendar(
        SqliteConnection conn, string calendarCode, DateTime cutoff)
    {
        var calendar = new List<(string Day, double Amount)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT substr(period_start, 1, 10) AS d, COALESCE(amount, 0)
            FROM Bar
            WHERE code = $cal AND granularity = $g AND substr(period_start, 1, 10) <= $cutoff
            ORDER BY d;
            """;
        cmd.Parameters.AddWithValue("$cal", calendarCode);
        cmd.Parameters.AddWithValue("$g", Granularity.Day);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        while (r.Read()) calendar.Add((r.GetString(0), r.IsDBNull(1) ? 0 : r.GetDouble(1)));
        return calendar;
    }

    private static Dictionary<string, int> ReadRowCounts(SqliteConnection conn, Spec spec)
    {
        var rowsByDay = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {spec.DateColumn} AS d, COUNT(*) FROM {spec.Table} GROUP BY {spec.DateColumn};";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            rowsByDay[r.GetString(0)] = r.GetInt32(1);
        }
        return rowsByDay;
    }

    /// <summary>滚动窗口表（东财资金流明细只给 120 天）的下界：窗口之外的零星行不参与判定。</summary>
    private static string WindowLowerBound(Spec spec, List<(string Day, double Amount)> calendar, string tableMin)
    {
        if (spec.WindowDays <= 0 || calendar.Count <= spec.WindowDays) return tableMin;
        var windowStart = calendar[^spec.WindowDays].Day;
        return string.CompareOrdinal(windowStart, tableMin) > 0 ? windowStart : tableMin;
    }

    /// <summary>基准至少要有这么多个有数据的邻近日才作数，否则宁可不判（返回 0＝这天不报）。</summary>
    private const int ThinMinSamples = 3;

    /// <summary>
    /// 这一天的基准行数：**它前面**那 <see cref="ThinWindowRadius"/> 个交易日的行数中位数
    /// （不含自己、不含空日——连着几天没抓到时，空日会把基准拉到 0，于是一天都报不出来）。
    ///
    /// 为什么只看前面、不看后面：这张表某天起量级永久跳一档是会发生的（接了新数据源、
    /// 覆盖范围变了），前后都算的话跳变点两边各有十天会被另一半拉偏、平白误报一串。
    /// 而"这天是不是只抓到零头"要比的本来就是**它之前**的水平。
    /// 区间开头那几天前面没有数据，才回退到用后面的补齐样本。
    /// </summary>
    private static int TrailingMedian(List<int> rows, int index)
    {
        var near = new List<int>();
        for (int i = index - 1; i >= 0 && near.Count < ThinWindowRadius; i--)
            if (rows[i] > 0) near.Add(rows[i]);

        // 开头几天：前面凑不够样本，用后面的补
        for (int i = index + 1; i < rows.Count && near.Count < ThinWindowRadius; i++)
            if (rows[i] > 0) near.Add(rows[i]);

        if (near.Count < ThinMinSamples) return 0;
        near.Sort();
        return near[near.Count / 2];
    }

    private static bool TryParse(string s, out DateTime day) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>
    /// 这一天全市场是不是"本来就没怎么交易"——拿它跟前后各 <see cref="QuietWindowRadius"/> 个
    /// 交易日的成交额中位数比。成交额缺失（老库的 amount 是 0/NULL）时一律返回 false，
    /// 保持加这条豁免之前的行为：宁可多报，也不要因为没有成交额就把真问题静音。
    /// </summary>
    private static bool IsQuietDay(List<(string Day, double Amount)> calendar, int index)
    {
        double today = calendar[index].Amount;
        if (today <= 0) return false;

        int from = Math.Max(0, index - QuietWindowRadius);
        int to = Math.Min(calendar.Count - 1, index + QuietWindowRadius);
        var near = new List<double>();
        for (int i = from; i <= to; i++)
            if (i != index && calendar[i].Amount > 0) near.Add(calendar[i].Amount);
        if (near.Count == 0) return false;

        near.Sort();
        double median = near[near.Count / 2];
        return today < median * QuietRatio;
    }
}
