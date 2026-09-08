using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// <see cref="ILocalTradingDaySource"/> 的实现——直接问 <see cref="SqliteBarRepository"/> 要
/// 全市场日K的日期并集。薄适配，只为把那个具体类挡在任务代码之外（方便测试）。
/// </summary>
public sealed class SqliteLocalTradingDaySource(SqliteBarRepository bars) : ILocalTradingDaySource
{
    public List<DateTime> GetDistinctDays() => bars.GetDistinctPeriodStarts(Granularity.Day);
}
