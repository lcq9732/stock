using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

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
///   · 残缺日（2026-09-16 由原"偏少日"扩成两条判据）：这天**有行但不全**——行数不到邻近
///     中位数的 <see cref="Spec.ThinRatio"/>（阈值按表配，快照型 0.7、事件型 0.2），
///     或者**某个交易所整天一行都没有**（见 <see cref="Spec.CheckMarkets"/>）。
///     2026-08-20 那种"跑早了只拿到三分之一"的半拉子轮次靠前一条看得见；
///     2026-08-21 两融"只抓到沪市、深市整天没有"（占正常量 49%，旧阈值 0.2 检不出）
///     靠后一条才抓得住。残缺日**会记进待办**（见 <see cref="Spec.OwnerTaskId"/>）。
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
    /// <summary>事件型表的行数阈值：行数波动本来就大，只抓极端的半拉子轮次，不想天天报噪声。</summary>
    private const double EventTableThinRatio = 0.2;

    /// <summary>
    /// 全市场快照型表的行数阈值（2026-09-16）。这类表每天覆盖固定的全体标的、行数稳定，
    /// 所以可以卡得紧。实测 0.7 对 MarginDetail 是"正好报出那 2 天、零误报"；
    /// 再往上到 0.8 会误判 2010 年两融刚开市那几天（42 行 vs 邻近 54，标的正在逐步纳入）。
    /// </summary>
    private const double SnapshotTableThinRatio = 0.7;

    /// <summary>
    /// 出现频率达到这个比例的交易所，视为"这张表每天都该有"（2026-09-16）。
    ///
    /// 基线**从数据自己算**、不写死"沪深"：北交所是后来才有的，写死会让 2021 年以前全部误报；
    /// 实测 NetInflowDetail 的核心市场是沪深北三个，而 2015 年的 MarginDetail 只有沪深两个。
    /// </summary>
    private const double CoreMarketFreq = 0.9;

    /// <summary>
    /// 市场判据的**样本量下限**（2026-09-16）：当天能归类到交易所的行少于这个数，就不判市场缺失。
    ///
    /// 少量样本里"某个交易所一笔都没有"是噪声不是信号。实测两类误报都是它挡掉的：
    ///   · 龙虎榜 2004 年全年才 223 条、全是深市中小板（沪市 2006 年才进来），日均 1~2 笔——
    ///     不加这道下限会报出 268 天"缺沪市"；
    ///   · 大宗交易 2016-01-04（熔断，13:34 收市）只有 19 行、全是 112xxx 企业债，
    ///     一行都归不了类，于是"沪深两个都缺"。⚠ 这天挡不住清淡日豁免——它的成交额
    ///     其实比邻近还高 7%（见 <see cref="QuietRatio"/> 那段的原注释）。
    ///
    /// 取 30 的依据：MarginDetail 出事那两天有 1,998 行可分类，离这条线远得很；
    /// 而上面两类噪声都在个位数到二十几行。中间这段空白里取哪个值都一样。
    /// </summary>
    private const int MarketMinSample = 30;

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
    /// <param name="ThinRatio">
    /// 行数低于**邻近**中位数的这个比例就算"偏少"（2026-09-16 从全局常量改成按表配置）。
    ///
    /// ⚠ **不能全表统一**，这是实测出来的：同一个 0.7 对 MarginDetail 是完美（正好报出那 2 天、
    /// 零误报），套到龙虎榜就从 2 天炸成 420 天。因为两类表性质不同——全市场快照型每天覆盖
    /// 固定的全体标的、行数稳定；事件型的行数取决于当天发生多少事件，天然剧烈波动。
    /// 各阈值的实测检出量见 doc/partial-day-repair-design.md §3.1。
    /// </param>
    /// <param name="CheckMarkets">是否启用"某个交易所整天没数据"判据（见 <see cref="Exchange"/>）。</param>
    /// <param name="CodeColumn">市场判据要读的代码列。⚠ Lhb 是 stock_code，不是 code。</param>
    /// <param name="CoverageFloor">
    /// 起点过滤（0＝不启用）：覆盖只数达到全量这个比例的第一天，才开始参与判定。
    ///
    /// 起因是 NetInflowDetail 全表 MIN 是 2025-11-14，可那天**只有 1 只票**——零星数据。
    /// 真实的全市场覆盖从 2026-03-13（1,526 只）起、03-16 才 5,464 只。不过滤的话，
    /// 零星期的每一天都会被两条判据同时判成残缺，报出一大堆无意义的告警。
    /// </param>
    /// <param name="OwnerTaskId">
    /// 残缺日待办记在哪个任务名下（<see cref="RetryTaskIds"/> 的取值，空＝不记待办、只报）。
    ///
    /// 放在 Spec 上而不是另开一张查表：这里已经有 <see cref="HowToFill"/> 这种"该跑哪一项"
    /// 的信息了，两处放迟早分叉。
    /// </param>
    public sealed record Spec(
        string Table, string DateColumn, string Label, string HowToFill,
        int LagDays = 0, int WindowDays = 0,
        double ThinRatio = 0.2, bool CheckMarkets = false,
        string CodeColumn = "code", double CoverageFloor = 0,
        string OwnerTaskId = "");

    /// <summary>
    /// 要体检的日频表。**只放"每个交易日全市场必有一批"的表**——机构调研、限售解禁、股东增减持
    /// 那几张是按公告来的，某天一条都没有很正常，放进来只会天天误报。
    /// </summary>
    public static readonly Spec[] DailyTables =
    [
        new("NetInflow",       "period_start", "资金净流入",
            "空日已记进待补名单，跑一次【重新拉取失败】即可（一轮全市场，约 1.75 小时）",
            ThinRatio: SnapshotTableThinRatio, CheckMarkets: true, OwnerTaskId: RetryTaskIds.NetInflow),
        new("MarginDetail",    "trade_date",   "融资余额",
            "残缺日已记进待补名单，跑一次【重新拉取失败】即可；整天缺的用【拉取区间数据】补",
            LagDays: 1,
            ThinRatio: SnapshotTableThinRatio, CheckMarkets: true, OwnerTaskId: RetryTaskIds.Margin),
        new("Lhb",             "trade_date",   "龙虎榜",
            "【龙虎榜】按天重跑（模式选「只抓某一天」、日期格填那一天）——"
            + "「首次整段回补」会把十年里的法定节假日挨个空抓一遍，补零星几天别用它",
            ThinRatio: EventTableThinRatio, CheckMarkets: true, CodeColumn: "stock_code",
            OwnerTaskId: RetryTaskIds.Lhb),
        new("NetInflowDetail", "trade_date",   "资金流明细(东财)",
            "【资金流明细】重跑一次——⚠ 数据源只给最近 120 天，更早的补不回来了",
            WindowDays: 120,
            ThinRatio: SnapshotTableThinRatio, CheckMarkets: true, CoverageFloor: 0.8),
            // ↑ 故意不配 OwnerTaskId：这张表只报不补。补一天要走 push2his 逐股通道 5500 个请求
            //   （快照那条通道只给最近一个交易日，补不了历史），代价远超收益；而且加了
            //   CoverageFloor 之后实测残缺 0 天——早先那 6 天全落在覆盖未达标期里。
        new("LhbSeat",         "trade_date",   "龙虎榜席位(东财)", "【龙虎榜席位】重跑一次",
            ThinRatio: EventTableThinRatio, CheckMarkets: true, OwnerTaskId: RetryTaskIds.LhbSeat),
        new("BlockTrade",      "trade_date",   "大宗交易(东财)",   "【市场事件】重跑一次",
            ThinRatio: EventTableThinRatio, CheckMarkets: true, OwnerTaskId: RetryTaskIds.MarketEvents),
    ];

    /// <summary>残缺日命中了哪条判据（可同时命中，所以是 Flags）。</summary>
    [Flags]
    public enum PartialReason
    {
        None = 0,
        /// <summary>行数不到邻近中位数的 <see cref="Spec.ThinRatio"/>。</summary>
        ThinRows = 1,
        /// <summary>某个核心交易所这天一行都没有。</summary>
        MissingMarket = 2,
    }

    /// <summary>
    /// 一个残缺日：这天**有行、但不全**。
    ///
    /// ⚠ 跟"空日"（一行都没有）必须分开处理，不只是措辞问题——它们的**复查判据不一样**：
    /// 空日复查看 `COUNT > 0` 就够，残缺日本来就有行，那样复查会立刻判"已补齐"、静默划掉。
    /// 这也是待办里 PartialDay 不能并进 MissingDays 的原因（见 doc/partial-day-repair-design.md §4.3）。
    /// </summary>
    /// <param name="Rows">这天实际有多少行。</param>
    /// <param name="Nearby">邻近中位数（判 ThinRows 的基准）；没算出基准时是 0。</param>
    /// <param name="MissingMarkets">缺了哪几个核心交易所（没命中 MissingMarket 时是空数组）。</param>
    public sealed record PartialDay(
        DateTime Day, int Rows, int Nearby, Exchange[] MissingMarkets, PartialReason Reason);

    /// <param name="Spec">哪张表。</param>
    /// <param name="From">这张表本地覆盖到的最早交易日（已按 <see cref="Spec.WindowDays"/> 裁过）。</param>
    /// <param name="To">最晚交易日。</param>
    /// <param name="TradingDays">区间内本该有数据的交易日数。</param>
    /// <param name="EmptyDays">一行都没有的交易日（按日期升序）。</param>
    /// <param name="PartialDays">**残缺日**：这天有行、但不全（2026-09-16 取代原来的 ThinDays）。
    /// 两条判据命中任意一条即入列，已排掉清淡日。见 <see cref="PartialDay"/>。</param>
    /// <param name="MedianRows">有数据那些天的行数中位数（全区间），给人判断量级用——
    /// 判定用的是邻近中位数，见 <see cref="ThinWindowRadius"/>。</param>
    /// <param name="TailMissingDays">表里最新那天之后、还该有数据的交易日（2026-09-06 加）——
    /// 已经扣掉 <see cref="Spec.LagDays"/> 允许的滞后，非空就是真落后了。</param>
    public sealed record Result(
        Spec Spec, DateTime From, DateTime To, int TradingDays,
        List<DateTime> EmptyDays, List<PartialDay> PartialDays, int MedianRows,
        List<DateTime> TailMissingDays);

    /// <summary>
    /// 体检一张表。表不存在（老库还没抓过这类数据）或一行都没有时返回 null——那不是"缺数据"，
    /// 是"这项功能还没用过"，报出来只会让人以为出了问题。
    /// </summary>
    /// <param name="calendarCode">交易日锚，跟K线体检用同一个（上证指数的前复权日线）。</param>
    /// <param name="cutoff">这一天之后的日子不算缺（数据源可能还没更新完）。</param>
    public Result? Check(Spec spec, string calendarCode, DateTime cutoff)
    {
        ValidateIdents(spec);

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

        // 市场判据要的"这天出现了哪些交易所"。只有启用的表才查——多一次全表扫。
        var marketsByDay = spec.CheckMarkets
            ? ReadMarketsByDay(conn, spec)
            : new Dictionary<string, DayMarkets>(StringComparer.Ordinal);
        var coreMarkets = CoreMarketsOf(marketsByDay);

        string tableMax = rowsByDay.Keys.Max(StringComparer.Ordinal)!;
        // 两道下界取较晚的：滚动窗口（数据源只给最近 N 天）和覆盖率起点（早期零星数据）。
        string lo = WindowLowerBound(spec, calendar, rowsByDay.Keys.Min(StringComparer.Ordinal)!);
        string coverageLo = CoverageLowerBound(spec, rowsByDay);
        if (string.CompareOrdinal(coverageLo, lo) > 0) lo = coverageLo;

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
        var partial = new List<PartialDay>();
        for (int i = 0; i < inRange.Count; i++)
        {
            var c = inRange[i];
            if (!TryParse(c.Day, out var day)) continue;
            int n = rowSeries[i];
            if (n == 0) { empty.Add(day); continue; }

            // 清淡日（熔断、长假前后）市场本身就没怎么交易，**两条判据都要豁免**：那种日子
            // 行数天然偏少，某个交易所一笔大宗都没有也正常（实测 BlockTrade 的 3 个可疑日里
            // 有 2 个是 2016 年的熔断日）。
            if (IsQuietDay(calendar, c.Index)) continue;

            int nearby = TrailingMedian(rowSeries, i);   // 基准是它之前的水平，不是十年的总中位数
            var reason = PartialReason.None;
            if (nearby > 0 && n < nearby * spec.ThinRatio) reason |= PartialReason.ThinRows;

            var missing = MissingMarketsOn(marketsByDay, coreMarkets, c.Day);
            if (missing.Length > 0) reason |= PartialReason.MissingMarket;

            if (reason != PartialReason.None)
                partial.Add(new PartialDay(day, n, nearby, missing, reason));
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
        return new Result(spec, from, to, inRange.Count, empty, partial, median, tail);
    }

    /// <summary>
    /// **只复查指定的那几天**（2026-09-16 加）——补完残缺日之后要判断"补上了没有"，
    /// 判据必须跟 <see cref="Check"/> 完全一致。
    ///
    /// ⚠ 这个方法存在的全部理由：不能用 `COUNT > 0` 复查残缺日。残缺日本来就有行
    /// （2026-08-21 有 1,998 行沪市），拿"有没有行"去复查，深市补没补上都会被判成
    /// "已补齐"、从待办里静默划掉——那正是本功能要修的 bug 换个地方重演。
    ///
    /// 邻近中位数仍要取这些天**前面**那段的行数，所以还是得读一段序列，只是不必扫全区间。
    /// </summary>
    /// <returns>这些天里**仍然残缺**的那些。全好了就返回空表。</returns>
    public List<PartialDay> CheckDays(Spec spec, string calendarCode, IEnumerable<DateTime> days)
    {
        var wanted = days.Select(d => d.ToString("yyyy-MM-dd")).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return new List<PartialDay>();

        ValidateIdents(spec);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // cutoff 取要查的最后一天——复查是"这几天现在怎么样"，跟"最近有没有落后"无关。
        var cutoff = wanted.Max(StringComparer.Ordinal)!;
        var calendar = ReadCalendar(conn, calendarCode, DateTime.ParseExact(cutoff, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (calendar.Count == 0) return new List<PartialDay>();

        var rowsByDay = ReadRowCounts(conn, spec);
        var marketsByDay = spec.CheckMarkets
            ? ReadMarketsByDay(conn, spec)
            : new Dictionary<string, DayMarkets>(StringComparer.Ordinal);
        var coreMarkets = CoreMarketsOf(marketsByDay);

        var rowSeries = calendar.Select(c => rowsByDay.GetValueOrDefault(c.Day)).ToList();
        var still = new List<PartialDay>();
        for (int i = 0; i < calendar.Count; i++)
        {
            if (!wanted.Contains(calendar[i].Day)) continue;
            if (!TryParse(calendar[i].Day, out var day)) continue;

            int n = rowSeries[i];
            // 整天变空了：那已经不是"残缺"而是"空日"，交给空日那条线，这里不留着
            if (n == 0) continue;
            if (IsQuietDay(calendar, i)) continue;

            int nearby = TrailingMedian(rowSeries, i);
            var reason = PartialReason.None;
            if (nearby > 0 && n < nearby * spec.ThinRatio) reason |= PartialReason.ThinRows;

            var missing = MissingMarketsOn(marketsByDay, coreMarkets, calendar[i].Day);
            if (missing.Length > 0) reason |= PartialReason.MissingMarket;

            if (reason != PartialReason.None)
                still.Add(new PartialDay(day, n, nearby, missing, reason));
        }
        return still;
    }

    /// <summary>这天缺了哪几个核心交易所。样本量不够就返回空——理由见 <see cref="MarketMinSample"/>。</summary>
    private static Exchange[] MissingMarketsOn(
        Dictionary<string, DayMarkets> marketsByDay, HashSet<Exchange> coreMarkets, string day)
    {
        if (coreMarkets.Count == 0) return [];
        var dm = marketsByDay.GetValueOrDefault(day);
        if (dm == null || dm.Classified < MarketMinSample) return [];
        return coreMarkets.Where(m => !dm.Markets.Contains(m)).ToArray();
    }

    private static void ValidateIdents(Spec spec)
    {
        // CodeColumn 也要校验：2026-09-16 加市场判据时它才进来，漏了就等于开了个注入口子
        if (!SafeIdent.IsMatch(spec.Table) || !SafeIdent.IsMatch(spec.DateColumn)
            || !SafeIdent.IsMatch(spec.CodeColumn))
            throw new ArgumentException($"表名/列名不合法：{spec.Table}.{spec.DateColumn}/{spec.CodeColumn}");
    }

    /// <summary>每个交易日出现了哪些交易所。走 <see cref="MarketClassifier"/> 而不是在 SQL 里
    /// 写 substr 前缀规则——920 是北交所，自写前缀曾害得 342 只票静默抓不到。</summary>
    private static Dictionary<string, DayMarkets> ReadMarketsByDay(SqliteConnection conn, Spec spec)
    {
        var byDay = new Dictionary<string, DayMarkets>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        // DISTINCT：同股同日可能多行（大宗一天多笔、龙虎榜一只票多条），按"有没有这只票"算
        cmd.CommandText = $"SELECT DISTINCT {spec.DateColumn} AS d, {spec.CodeColumn} AS c FROM {spec.Table};";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
            var ex = MarketClassifier.ExchangeOf(r.GetString(1));
            // 归不了类的不参与判定，也**不计入样本量**：2016-01-04 那天 19 行全是 112xxx 企业债，
            // 要是把它们算进样本，这天就会被当成"样本够多、但沪深都缺"。
            if (ex == Exchange.Unknown) continue;
            if (!byDay.TryGetValue(r.GetString(0), out var dm))
                byDay[r.GetString(0)] = dm = new DayMarkets();
            dm.Markets.Add(ex);
            dm.Classified++;
        }
        return byDay;
    }

    /// <summary>某一天出现过的交易所，以及能归类到交易所的标的数（样本量，见 <see cref="MarketMinSample"/>）。</summary>
    private sealed class DayMarkets
    {
        public HashSet<Exchange> Markets { get; } = new();
        public int Classified { get; set; }
    }

    /// <summary>出现频率 ≥ <see cref="CoreMarketFreq"/> 的交易所——"这张表每天都该有"的基线。
    /// 从数据自己算，不写死沪深：北交所是后来才有的，写死会让 2021 年以前全部误报。</summary>
    private static HashSet<Exchange> CoreMarketsOf(Dictionary<string, DayMarkets> marketsByDay)
    {
        if (marketsByDay.Count == 0) return new HashSet<Exchange>();
        var freq = new Dictionary<Exchange, int>();
        foreach (var dm in marketsByDay.Values)
            foreach (var ex in dm.Markets)
                freq[ex] = freq.GetValueOrDefault(ex) + 1;
        double need = marketsByDay.Count * CoreMarketFreq;
        return freq.Where(kv => kv.Value >= need).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>覆盖率起点：行数达到全量 <see cref="Spec.CoverageFloor"/> 的第一天。
    /// 早期那些"逐只回补时先抓的几只票"的零星行不该参与判定（NetInflowDetail 全表 MIN 那天
    /// 只有 1 只票，不过滤的话零星期每天都会被判成残缺）。</summary>
    private static string CoverageLowerBound(Spec spec, Dictionary<string, int> rowsByDay)
    {
        if (spec.CoverageFloor <= 0 || rowsByDay.Count == 0) return "";
        int full = rowsByDay.Values.Max();
        double need = full * spec.CoverageFloor;
        var qualified = rowsByDay.Where(kv => kv.Value >= need).Select(kv => kv.Key).ToList();
        return qualified.Count == 0 ? "" : qualified.Min(StringComparer.Ordinal)!;
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
