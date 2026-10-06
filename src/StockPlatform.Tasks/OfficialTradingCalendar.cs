using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Services;

namespace StockPlatform.Tasks;

/// <summary>
/// 从 TradingDay 表（官方日历）读出 <see cref="TradingCalendar"/>，给逐只增量的任务判
/// "窗口里有没有交易日"用（2026-10-05 日K三口径、10-06 资金净流入）。
///
/// 表空或读失败返回 null——判据（<see cref="IncrementalWindowCalculator.NoTradingDayIn"/>）
/// 会放行去抓，只是节假日照旧空跑。
///
/// ⚠ 别换成从K线归纳的日历（<c>BarFetchTaskBase.LocalTradingCalendar</c>）：那个只到本地
/// 最新一根，判不了"今天是不是交易日"。
/// </summary>
public static class OfficialTradingCalendar
{
    public static TradingCalendar? Load(ITradingDayRepository? tradingDays, Action<string> report)
    {
        if (tradingDays == null) return null;
        try
        {
            var days = tradingDays.GetAll();
            return days.Count > 0 ? new TradingCalendar(days) : null;
        }
        catch (Exception ex)
        {
            report($"⚠ 读交易日历失败（{ex.Message}），这一轮不按交易日跳过，节假日会照旧发请求。");
            return null;
        }
    }
}
