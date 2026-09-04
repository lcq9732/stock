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
/// 没有融资余额、集体没人上龙虎榜，不可能）。所以判据只有两档，都不需要拍阈值：
///   · 空日：交易日历里有、这张表 0 行 → 报"缺"；
///   · 偏少日：行数不到中位数的 <see cref="ThinRatio"/> → 只报"可疑"，不算缺。
///     2026-08-20 那种"跑早了只拿到三分之一"的半拉子轮次就是靠这一档才看得见。
///
/// ════ 为什么只查表自己的 [最早, 最晚] 区间 ════
/// 各表的历史深度天差地别（东财资金流明细只给 120 天、融资余额能追到 2015 年）。
/// 卡在各自区间内，就不用在代码里维护"这张表该有多少年历史"这种迟早过期的知识。
///
/// ════ 为什么只报、不进待补名单 ════
/// 这些数据的补法各不相同（有的按交易日重抓、有的整段回补、东财那几张只有近三个月），
/// 塞进【重新拉取失败】那条统一的逐段重试里既补不对也说不清。所以体检报出是哪一天缺，
/// 并直接写明该跑哪一项——判断留给人，这跟板块指数、day_adj 的处理是一致的。
/// </summary>
public sealed class SqliteDailyTableAuditor(string dbPath)
{
    /// <summary>行数低于中位数的这个比例就算"偏少"。0.2 很宽松——只想抓半拉子轮次，不想天天报噪声。</summary>
    private const double ThinRatio = 0.2;

    /// <summary>表名/列名只允许这些字符。它们来自下面的常量表、不是外部输入，这道校验是防以后有人接错。</summary>
    private static readonly Regex SafeIdent = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <param name="Table">表名。</param>
    /// <param name="DateColumn">交易日那一列（全都是 yyyy-MM-dd 文本，跟 Bar.period_start 的
    /// "yyyy-MM-dd HH:mm:ss" 不同，比对时要截前 10 位）。</param>
    /// <param name="Label">日志里的名字。</param>
    /// <param name="HowToFill">缺了该跑哪一项——体检只报不补，这句话就是给人的下一步。</param>
    public sealed record Spec(string Table, string DateColumn, string Label, string HowToFill);

    /// <summary>
    /// 要体检的日频表。**只放"每个交易日全市场必有一批"的表**——机构调研、限售解禁、股东增减持
    /// 那几张是按公告来的，某天一条都没有很正常，放进来只会天天误报。
    /// </summary>
    public static readonly Spec[] DailyTables =
    [
        new("NetInflow",       "period_start", "资金净流入",
            "【资金净流入】按天重跑（【补指定历史日】填那一天），久远的用【拉取区间数据】"),
        new("MarginDetail",    "trade_date",   "融资余额",
            "【融资余额】只回看最近几个交易日，久远的那天要用【拉取区间数据】补"),
        new("Lhb",             "trade_date",   "龙虎榜",
            "【龙虎榜】按天重跑（【补指定历史日】填那一天），久远的用【拉取区间数据】"),
        new("NetInflowDetail", "trade_date",   "资金流明细(东财)",
            "【资金流明细】重跑一次——⚠ 数据源只给最近 120 天，更早的补不回来了"),
        new("LhbSeat",         "trade_date",   "龙虎榜席位(东财)", "【龙虎榜席位】重跑一次"),
        new("BlockTrade",      "trade_date",   "大宗交易(东财)",   "【市场事件】重跑一次"),
    ];

    /// <param name="Spec">哪张表。</param>
    /// <param name="From">这张表本地覆盖到的最早交易日。</param>
    /// <param name="To">最晚交易日。</param>
    /// <param name="TradingDays">区间内本该有数据的交易日数。</param>
    /// <param name="EmptyDays">一行都没有的交易日（按日期升序）。</param>
    /// <param name="ThinDays">行数不到中位数 20% 的交易日。</param>
    /// <param name="MedianRows">有数据那些天的行数中位数，给人判断量级用。</param>
    public sealed record Result(
        Spec Spec, DateTime From, DateTime To, int TradingDays,
        List<DateTime> EmptyDays, List<(DateTime Day, int Rows)> ThinDays, int MedianRows);

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

        // 交易日历那边要截前 10 位：Bar.period_start 是 "yyyy-MM-dd HH:mm:ss"，日频表是 "yyyy-MM-dd"。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH cal AS (
                SELECT DISTINCT substr(period_start, 1, 10) AS d FROM Bar
                WHERE code = $cal AND granularity = $g
            ),
            cnt AS (
                SELECT {spec.DateColumn} AS d, COUNT(*) AS n FROM {spec.Table} GROUP BY {spec.DateColumn}
            )
            SELECT c.d, COALESCE(r.n, 0)
            FROM cal c
            LEFT JOIN cnt r ON r.d = c.d
            WHERE c.d >= (SELECT MIN(d) FROM cnt)
              AND c.d <= (SELECT MAX(d) FROM cnt)
              AND c.d <= $cutoff
            ORDER BY c.d;
            """;
        cmd.Parameters.AddWithValue("$cal", calendarCode);
        cmd.Parameters.AddWithValue("$g", Granularity.Day);
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var days = new List<(DateTime Day, int Rows)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!DateTime.TryParseExact(reader.GetString(0), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                days.Add((d, reader.GetInt32(1)));
            }
        }
        if (days.Count == 0) return null;

        var withRows = days.Where(x => x.Rows > 0).Select(x => x.Rows).OrderBy(n => n).ToList();
        if (withRows.Count == 0) return null;
        int median = withRows[withRows.Count / 2];
        int thinBelow = (int)(median * ThinRatio);

        return new Result(
            spec, days[0].Day, days[^1].Day, days.Count,
            days.Where(x => x.Rows == 0).Select(x => x.Day).ToList(),
            days.Where(x => x.Rows > 0 && x.Rows < thinBelow).ToList(),
            median);
    }
}
