using System.Globalization;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 全库数据体检（2026-09-02）：按股票逐只对照交易日历找日线空洞，以及那张
/// "确认数据源没有"的白名单表（<c>MissingBarConfirmed</c>）的存取。
///
/// ════ 为什么按股票查、而不是按日期查 ════
/// 按日期查（"这一天有日线的股票数明显偏少"）只找得到"某天全市场大面积缺"那种，
/// 抓不到"个别票零星缺几天"。按股票查才彻底。
///
/// ════ 停牌怎么办 ════
/// 停牌那几天在数据上跟漏抓一模一样——交易日历里有、这只票没有，靠查是分不开的。
/// 所以流程是：体检报出来 → 去抓一次 → 连着两轮拿不到 → 写进白名单，往后体检跳过。
/// 白名单是"抓过、确认拿不到"的结论，不是体检直接下的判断。
/// </summary>
public sealed class SqliteMissingBarRepository(string dbPath)
{
    private const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// 找出这一批标的在自己 <c>[起点, 最晚]</c> 区间内缺掉的交易日。起点是第一根K线和上市日里更早的那个
    /// （不早于水位表确认"之前没有"的日子），见方法体里的注释。
    ///
    /// 交易日历用**上证指数的前复权日线**：它不停牌不退市，是全库现成的交易日锚，
    /// 这样就不用在本地维护一份 A 股节假日日历（那玩意每年都要更新、还容易忘）。
    /// ⚠ 日历固定读 <see cref="Granularity.Day"/>，**不跟着 <paramref name="granularity"/> 走**
    /// （2026-09-04 修）：后复权/不复权只对个股和退市股抓，指数根本没有那两个口径的K线，
    /// 日历要是也按被查口径取，查 day_hfq/day_raw 时日历会是空的、一个空洞都报不出来。
    ///
    /// <paramref name="ignoreConfirmed"/>=true 就是「彻底体检」：连白名单里已确认的也重查一遍，
    /// 用在"怀疑数据源当时抽风、后来补上了"的时候。白名单按口径分开存，互不影响。
    /// </summary>
    /// <param name="codes">这一批要查的标的（分批跑，免得一次几千万行扫描把内存和时间都吃满）。</param>
    /// <param name="granularity">要体检哪一套日线：day / day_hfq / day_raw / day_adj。</param>
    /// <returns>每只标的缺失的交易日，按日期升序；一天不缺的不出现在结果里。</returns>
    public Dictionary<string, List<DateTime>> FindGaps(
        IReadOnlyList<string> codes, string granularity, string calendarCode, bool ignoreConfirmed = false)
    {
        var result = new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);
        if (codes.Count == 0) return result;

        using var conn = Open();
        using var cmd = conn.CreateCommand();

        // 参数化 IN 列表：代码是从本地库读出来的（不是外部输入），但照样走参数，
        // 免得以后有人把它接到别的来源上。
        var names = new List<string>(codes.Count);
        for (int i = 0; i < codes.Count; i++)
        {
            var n = $"$c{i}";
            names.Add(n);
            cmd.Parameters.AddWithValue(n, codes[i]);
        }
        var inList = string.Join(",", names);

        // 思路：交易日历 × 每只票的存续区间 = 它"本该有"的那些日子，
        // 再 LEFT JOIN 实际日线找空洞，最后 LEFT JOIN 白名单排掉已确认拿不到的。
        //
        // 存续区间的起点（2026-10-16 改，见 doc/ipo-issue-design.md §6）：原来是这只票自己的第一根K线，
        // 于是**上市日到第一根之间缺的那几天永远查不出来**——2023-08 上市的 26 只创业板股不复权少了
        // 头 8~22 天（301371 敷尔佳从 08-31 才有，上市是 08-01），前复权/后复权却是齐的，体检一直全绿。
        // 现在起点＝ min(第一根, 上市日)，但不早于水位表 BarProbeFloor 里"数据源确认这天之前没有"的日子
        // （1990~1993 年那几只老股上市头几天数据源本来就没有，09-12 探测确认过，不该再报）。
        //   · 上市日读名册 StockMeta.list_date（【新股发行】写的）；没有的（退市股、还没跑过那一项）照旧从第一根算；
        //   · 本地K线比上市日还早的（600018 吸收合并后重新上市，上市日 2006、K线 2000 年就有）起点不动。
        // 体检报空洞、【重新拉取失败】补完复查都走这一个方法，所以两边自动是同一个判据——
        // 只在体检那边加的话，复查看不到这一段，补不上的也会被当成"已补齐"划掉。
        //
        // 终点（2026-10-16 改，见 doc/ipo-issue-design.md §7）：原来是这只票自己的最后一根，于是**最后一根之后
        // 缺的日子也永远查不出来**——某个口径悄悄停更了，体检照样全绿；原先只有一行"最新一根停在 10 个交易日
        // 以内"的只报不补的提示（分不清长期停牌和漏抓）。现在**个股和 ETF** 的终点延伸到交易日历的最后一天：
        //   · 停牌、摘牌的日子不在这里排——体检拿官网停复牌记录扣（SqliteSuspensionLookup），这里只管"该有没有"；
        //   · 指数不停牌、用不着；板块指数是本地合成的，板块撤销后自然不再更新，延伸只会制造噪音；
        //   · day_adj 不延伸：它落后于不复权是【重算回测序列】还没跑，体检另有"落后于不复权"一行专门报；
        //   · 日历最后那一两天还没结算——体检自己按 cutoff 截（最近两天归【当日完整性体检】），
        //     【重新拉取失败】的复查只看待办那一段的日期范围，多出来的日子不影响它。
        cmd.CommandText = $"""
            WITH cal AS (
                SELECT period_start AS d FROM Bar
                WHERE code = $cal AND granularity = $calg
            ),
            span AS (
                SELECT code, MIN(period_start) lo, MAX(period_start) hi
                FROM Bar
                WHERE granularity = $g AND code IN ({inList})
                GROUP BY code
            ),
            rng AS (
                SELECT s.code,
                       CASE WHEN m.list_date IS NOT NULL AND m.list_date <> ''
                            THEN MIN(s.lo, MAX(substr(m.list_date, 1, 10) || ' 00:00:00',
                                               COALESCE(substr(f.no_data_before, 1, 10) || ' 00:00:00', '')))
                            ELSE s.lo END AS lo,
                       CASE WHEN m.type IN ('stock', 'etf') AND $g <> 'day_adj'
                            THEN MAX(s.hi, COALESCE((SELECT MAX(d) FROM cal), s.hi))
                            ELSE s.hi END AS hi
                FROM span s
                LEFT JOIN StockMeta m ON m.code = s.code
                LEFT JOIN BarProbeFloor f ON f.code = s.code AND f.granularity = $g
            )
            SELECT r.code, c.d
            FROM rng r
            JOIN cal c ON c.d >= r.lo AND c.d <= r.hi
            LEFT JOIN Bar b
                   ON b.code = r.code AND b.granularity = $g AND b.period_start = c.d
            LEFT JOIN MissingBarConfirmed m
                   ON m.code = r.code AND m.granularity = $g AND m.period_start = c.d
            WHERE b.code IS NULL AND ($ignoreConfirmed = 1 OR m.code IS NULL)
            ORDER BY r.code, c.d;
            """;
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$calg", Granularity.Day);
        cmd.Parameters.AddWithValue("$cal", calendarCode);
        cmd.Parameters.AddWithValue("$ignoreConfirmed", ignoreConfirmed ? 1 : 0);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var day = DateTime.ParseExact(reader.GetString(1), DateFormat, CultureInfo.InvariantCulture);
            if (!result.TryGetValue(code, out var list)) result[code] = list = [];
            list.Add(day);
        }
        return result;
    }

    /// <summary>把"抓过仍拿不到"的那些日子写进白名单，往后体检跳过。</summary>
    public void Confirm(IEnumerable<(string Code, DateTime Day)> entries, string granularity, int tries)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO MissingBarConfirmed (code, granularity, period_start, tries, confirmed_at)
            VALUES ($code, $g, $d, $tries, $at)
            ON CONFLICT(code, granularity, period_start) DO UPDATE SET
                tries = excluded.tries, confirmed_at = excluded.confirmed_at;
            """;
        var pCode = cmd.CreateParameter(); pCode.ParameterName = "$code"; cmd.Parameters.Add(pCode);
        var pDay = cmd.CreateParameter(); pDay.ParameterName = "$d"; cmd.Parameters.Add(pDay);
        cmd.Parameters.AddWithValue("$g", granularity);
        cmd.Parameters.AddWithValue("$tries", tries);
        cmd.Parameters.AddWithValue("$at", DateTime.Now.ToString(DateFormat, CultureInfo.InvariantCulture));

        foreach (var (code, day) in entries)
        {
            pCode.Value = code;
            pDay.Value = day.ToString(DateFormat, CultureInfo.InvariantCulture);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>白名单里有多少条（界面上报个数，让人知道"确认没有的"攒了多少）。</summary>
    public int ConfirmedCount()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM MissingBarConfirmed;";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch { return 0; }
    }

    /// <summary>清空白名单——「彻底体检」用，把之前确认过的结论作废、全部重查。</summary>
    public void ClearConfirmed()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MissingBarConfirmed;";
        cmd.ExecuteNonQuery();
    }
}
