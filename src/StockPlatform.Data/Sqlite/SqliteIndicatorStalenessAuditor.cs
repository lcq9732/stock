using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// **日频景气指标停更了没有**（2026-09-17）——当日完整性体检的第④段判据。
///
/// ════ 为什么是单独一类判据 ════
/// 它既不是"全市场逐只必有"（每个指标是一条自己的序列），也不像覆盖式快照那样只有一份最新值
/// ——45 条序列各有各的发布节奏。真实失效是"某几个指标悄悄停更了"，而界面上一点看不出来：
/// 【行业景气指标】那一项每轮都报"完成"，因为它确实把接口给的都抓回来了，只是接口不给了。
///
/// ════ 判据：跟**它自己的**历史节奏比，不信 frequency 标注 ════
/// 判"落后几天"必须有个基准，而这里有三个陷阱：
///
///   1. **东财的 `frequency` 标注不一定准**（同一张表的 `granularity` 就已知不准）。
///      标成"日"的实测里就有每周发两三次的（涤纶价格，历史最长停 6 天）。
///      所以判据不能写"日频就该每天有"。
///   2. **这些指标的日期不是交易日**。国内汽油/柴油供应价的最新值落在 2026-09-12（周六）——
///      拿交易日历去数"落后几个交易日"，它直接算不出来。所以这一段全程用**自然日**。
///   3. 各指标的正常停更长度差很多：期货类最长停 3~4 天（周末+假日），涤纶类 6 天，
///      国内油价 15 天。一个统一阈值要么天天误报、要么什么都检不出。
///
/// 所以基准从**每个指标自己**近 <see cref="LookbackDays"/> 天的数据里算：相邻两个值之间
/// 最长隔了多少天（`maxGap`），现在停得比那还久（再加 <see cref="GraceDays"/> 天宽限）才报。
/// 语义就是"它从来没停这么久"。
///
/// 这条判据自带对陷阱 1 的免疫：某个指标要是被标成"日"、其实是周频，它的 `maxGap` 自然就是
/// 7~8 天，判据跟着宽容——不需要我们去纠正东财的标注。
///
/// ════ 实测定参（2026-09-17，锚 09-16）════
/// 45 个日频指标全部有足够样本，`落后天数 − maxGap` 的分布是 −4(31 个) / −1(8 个) / −2 / −3 / −11，
/// **最大 −1**（涤纶那三个：停 5 天、历史最长停 6 天）。也就是这条判据现在零误报，
/// 而最紧的那个离触发线还差 1 天——<see cref="GraceDays"/> 就是为它留的。
///
/// ════ 只报不补 ════
/// 停更的原因可能是数据源下线了这个指标、改了编号、或者接口挂了，三种的处理完全不同，
/// 塞进待办里"自动重抓"补不对。报出来点名该跑哪一项，判断留给人。
/// </summary>
public sealed class SqliteIndicatorStalenessAuditor(string dbPath)
{
    /// <summary>算一个指标"自己的发布节奏"时往回看多少**自然日**。
    /// 90 天在实测里给出 58~64 个样本，足够看出它最长停多久是常态。</summary>
    public const int LookbackDays = 90;

    /// <summary>
    /// 宽限（自然日）。判据是"停得比它自己历史上最长的那次还久"，再加这一天余量——
    /// 实测最紧的那三个离触发线只差 1 天，不留余量的话它下次多停一天就喊。
    ///
    /// 这类数据不在关键路径上（景气指标是传统行业分析的输入，晚几天不影响选股），
    /// 所以宁可保守：漏报一天没代价，天天误报会让人从此不看这一行。
    /// </summary>
    public const int GraceDays = 1;

    /// <summary>样本少于这个数就不判这个指标——刚接进来的指标没有节奏可言，
    /// 拿两三个点推出来的"最长间隔"只会瞎报。</summary>
    public const int MinSamples = 4;

    /// <summary>只看这个频率的指标。月频停一个月是常态，而它在 90 天窗口里只有 3 个样本，
    /// 判了只会瞎报；周频同理。要扩到别的频率得先有各自的实测数字。</summary>
    private const string DailyFrequency = "日";

    /// <param name="Name">指标名（没有名字就退回 indicator_id）。</param>
    /// <param name="Last">最新一个值是哪天。</param>
    /// <param name="Behind">已经多少**自然日**没更新（相对锚）。</param>
    /// <param name="MaxGap">它平时最多停多少天（近 <see cref="LookbackDays"/> 天里的最长间隔）。</param>
    public sealed record StaleIndicator(string Name, DateTime Last, int Behind, int MaxGap);

    /// <param name="Judged">有多少个指标真的判了（样本够的那些）。0 = 这类数据本地还没攒起来。</param>
    /// <param name="Stale">停更的那些，按"超出自身节奏多少"降序。</param>
    public sealed record Result(int Judged, IReadOnlyList<StaleIndicator> Stale);

    /// <summary>
    /// 查一轮。表不存在（老库没抓过这类数据）返回 <c>Judged = 0</c>——那是判不了，不是告警。
    /// </summary>
    /// <param name="anchor">锚（最新交易日）。落后天数按自然日相对它算。</param>
    public Result Check(DateTime anchor)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' "
                              + "AND name IN ('IndustryIndicator','IndustryIndicatorValue');";
            if (Convert.ToInt32(probe.ExecuteScalar() ?? 0) < 2) return new Result(0, []);
        }

        var since = anchor.AddDays(-LookbackDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // 一次查完所有日频序列近 90 天的日期（走 IndustryIndicatorValue 主键，几毫秒），
        // 在内存里按指标分组算节奏——逐指标发查询是 45 次往返，没必要。
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.indicator_id, COALESCE(i.name, i.indicator_id), v.trade_date
            FROM IndustryIndicator i
            JOIN IndustryIndicatorValue v ON v.indicator_id = i.indicator_id
            WHERE i.frequency = $freq AND v.trade_date >= $since
            ORDER BY i.indicator_id, v.trade_date;
            """;
        cmd.Parameters.AddWithValue("$freq", DailyFrequency);
        cmd.Parameters.AddWithValue("$since", since);

        var byIndicator = new Dictionary<string, (string Name, List<DateTime> Days)>(StringComparer.Ordinal);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var raw = r.GetString(2);
                // 截前 10 位：这一列按 schema 是 "yyyy-MM-dd"，截一下比相信它便宜
                if (!DateTime.TryParseExact(raw.Length > 10 ? raw[..10] : raw, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                var id = r.GetString(0);
                if (!byIndicator.TryGetValue(id, out var entry))
                    byIndicator[id] = entry = (r.GetString(1), new List<DateTime>());
                entry.Days.Add(d);
            }
        }

        int judged = 0;
        var stale = new List<StaleIndicator>();
        foreach (var (name, days) in byIndicator.Values)
        {
            if (days.Count < MinSamples) continue;   // 没有节奏可言，判不了
            judged++;

            int maxGap = 0;
            for (int i = 1; i < days.Count; i++)
                maxGap = Math.Max(maxGap, (int)(days[i] - days[i - 1]).TotalDays);

            int behind = (int)(anchor - days[^1]).TotalDays;
            if (behind > maxGap + GraceDays) stale.Add(new StaleIndicator(name, days[^1], behind, maxGap));
        }

        return new Result(judged, stale.OrderByDescending(s => s.Behind - s.MaxGap).ToList());
    }
}
