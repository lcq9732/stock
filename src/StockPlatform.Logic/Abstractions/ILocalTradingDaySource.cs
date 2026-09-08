namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// "本地K线里出现过哪些日期"——交易日历 2004 年及以前那段的归纳来源（2026-09-08）。
///
/// 为什么单开一个窄接口，而不是把 <c>GetDistinctPeriodStarts</c> 加进 <see cref="IBarRepository"/>：
/// 那个接口有三个实现（含 CutoffBarRepository 装饰器和测试里的假仓储），为一个只用一次的查询
/// 让三处都跟着改不划算；而窄接口让交易日历任务能用假数据在毫秒级测完，不碰 23GB 的库。
///
/// ⚠ 必须是**全市场并集**，不能拿单只指数代替——2026-09-06 踩过：上证指数本地只有 2016 年起，
/// 拿它当日历导致 2,360 只最该补历史的老股被判成"缺口里没有交易日"静默跳过。
/// </summary>
public interface ILocalTradingDaySource
{
    /// <summary>全市场日K出现过的所有日期（去重、升序）。⚠ 23GB 库上要走索引扫几十秒，别频繁调。</summary>
    List<DateTime> GetDistinctDays();
}
