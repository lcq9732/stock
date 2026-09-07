namespace StockPlatform.Logic.Services;

/// <summary>
/// 本地已知的交易日历——从库里已有的K线日期**归纳**出来，不维护节假日表
/// （本项目一贯做法：判交易日以已有行情为锚，见 <see cref="MarketIndexCatalog"/> 的注释）。
///
/// ════ 为什么必须有 <see cref="CoversFrom"/> ════
/// 归纳出来的日历只覆盖它见过的那段时间。拿它判断"某区间内有没有交易日"之前，必须先问一句
/// **"日历覆盖得到这个区间吗"**——覆盖不到的时候，"查不到交易日"的真实含义是
/// **日历不知道**，而不是**确实没有交易日**。把这两者混为一谈会静默漏数据。
///
/// 2026-09-06 就踩了这个坑：往年回补（【拉取区间数据 1990~2016】）拿上证指数的 day 序列当日历，
/// 而它自己也只有 2016-01-04 起，于是 2,360 只**恰恰最需要补 2016 年前历史**的老股被判成
/// "缺口里没有交易日"，一个请求都没发就跳过了；反倒是 2,978 只 2016 年后才上市、根本没有那段
/// 历史的票被认真抓了一遍。日志上看是"跳过 2360 只（本地已是最新）"，完全不像出了错。
/// </summary>
public sealed class TradingCalendar
{
    private readonly List<DateTime> _days;   // 升序去重，供二分查找

    public TradingCalendar(IEnumerable<DateTime> days)
    {
        _days = days.Select(d => d.Date).Distinct().OrderBy(d => d).ToList();
    }

    public int Count => _days.Count;

    /// <summary>已知最早的交易日；日历为空时为 null。</summary>
    public DateTime? Earliest => _days.Count > 0 ? _days[0] : null;

    /// <summary>已知最晚的交易日；日历为空时为 null。</summary>
    public DateTime? Latest => _days.Count > 0 ? _days[^1] : null;

    /// <summary>
    /// 日历是否覆盖到 <paramref name="from"/> 这一天——即已知最早的交易日不晚于它。
    /// 只有覆盖得到，<see cref="HasAnyIn"/> 返回 false 才能被解读成"确实没有交易日"。
    /// </summary>
    public bool CoversFrom(DateTime from) => _days.Count > 0 && _days[0] <= from.Date;

    /// <summary>闭区间 [start, end] 内是否存在已知交易日。二分查找，O(log n)。</summary>
    public bool HasAnyIn(DateTime start, DateTime end)
    {
        if (_days.Count == 0 || start.Date > end.Date) return false;
        int i = _days.BinarySearch(start.Date);
        if (i < 0) i = ~i;                        // 取第一个 >= start 的位置
        return i < _days.Count && _days[i] <= end.Date;
    }
}
