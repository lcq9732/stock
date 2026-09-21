namespace StockPlatform.Logic.Services;

/// <summary>
/// "这一天的数据算不算最终的"、"下一轮增量该从哪天抓起"——两个判据，纯计算、无 IO。
///
/// 2026-09-09 从 <c>FetchOrchestrator</c> 的两个 private static 抽出来，抽的理由跟
/// <see cref="YearGapCalculator"/> 一样：**这类判据出错是静默的**。原来的增量起点算法漏了
/// "最新那根是盘中抓的"这一支，于是 2026-09-01 早上 09:25~10:10 跑【不复权首次整段回补】时
/// 抓到的 4020 只票当天K线（OHLC 是瞬时价、量额换手是半天累计值，002650 那天四价合一 6.04、
/// 成交量 23 手）被永久固化，day_adj 还原样继承了 1555 行；2026-07-16 11:25 抓的 1330 个
/// 指数/ETF（含上证指数——全库交易日锚兼 MA60 择时输入）同一个坑。没有单测能碰到它。
/// </summary>
public static class IncrementalWindowCalculator
{
    /// <summary>
    /// 认定"这一天已经收盘、数据不会再变"的时刻。故意取 16:00 而不是 15:00 收盘点：留一小时给
    /// 数据源结算、也不去处理早收盘的半天交易日——这个方向的误差只会让某天多等一会儿才被认定为
    /// 最终，不会出现"提前认定成最终、其实还会变"的反向错误。
    /// </summary>
    public const int MarketCloseHour = 16;

    /// <summary>
    /// A股开市首日（上交所第一个交易日）。**"整段回补"的起点就是它**——不看水位线、也不看回看年数
    /// 的那个模式，要的就是"把这条标的的历史一次补到底"。
    ///
    /// ⚠ 用硬常量，不用"日历自己的首日"：日历是从库里归纳出来的，**缺哪段就瞎哪段**，
    /// 拿它当"市场起点"会把"日历不知道"误读成"确实没开市"——2026-09-06 静默漏抓 2360 只老股
    /// 就是这么来的（见 project_trading_calendar_pitfall）。开市日是事实，不是推断。
    ///
    /// 2026-09-21 从 <c>FetchOrchestrator</c> 抽到这里：指数日K迁进新框架之后两边都要用它，
    /// 各写一个日期就是等着哪天只改一边。
    /// </summary>
    public static readonly DateTime AShareMarketOpen = new(1990, 12, 19);

    /// <summary>某个交易日的数据是不是"收盘后确认过"的最终值：抓取时刻晚于那天 16:00 就算
    /// （隔了几天才补的天然满足）。早于它就是盘中抓的半成品。</summary>
    public static bool IsConfirmedFinal(DateTime fetchedAt, DateTime tradingDay) =>
        fetchedAt >= tradingDay.Date.AddHours(MarketCloseHour);

    /// <summary>
    /// 一只标的的增量抓取起点。<paramref name="latest"/> 是本地最新那根的 (日期, 抓取时刻)。
    /// <list type="bullet">
    /// <item>本地一根都没有 → 从 <paramref name="end"/> 往前回看 <paramref name="lookbackYears"/> 年；</item>
    /// <item>最新那根**已确认** → 从它的次日起（是今天且已确认时返回"明天"，调用方按 start &gt; end 跳过）；</item>
    /// <item>最新那根**未确认**（盘中抓的）→ **回退到它自己**，重抓覆盖。</item>
    /// </list>
    /// 第三条要配合 <c>SqliteBarRepository.InsertOrRefreshUnconfirmed</c> 才生效：光回退起点不够，
    /// 重抓回来还得能覆盖掉库里那一行（原来的写入是 INSERT OR IGNORE，会把新数据默默丢掉）。
    /// </summary>
    public static DateTime IncrementalStart(
        (DateTime PeriodStart, DateTime FetchedAt)? latest, DateTime end, int lookbackYears)
    {
        if (latest == null) return end.AddYears(-lookbackYears);
        var (periodStart, fetchedAt) = latest.Value;
        return IsConfirmedFinal(fetchedAt, periodStart) ? periodStart.AddDays(1) : periodStart;
    }
}
