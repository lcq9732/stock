using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的大宗交易源（2026-09-18）——一个请求都不发，按交易日凭空造行。
/// 说明书见 doc/offline-mock-design.md；安全边界跟 <see cref="MockBarFetcher"/> 一样
/// （只在 DEBUG 构建里挂上去 + Debug 数据目录隔离）。
///
/// ⚠ **`ReportedCount` 必须跟实际造的行数对上**：这张表是整日替换，任务侧和
/// <c>BlockTradeDayWriter</c> 都会用 <see cref="BlockTradeDay.IsComplete"/> 把"没抓全"的
/// 那天整天挡下来。对不上的话模拟源一开每天都成"残缺日"。
/// 真要模拟截断，用 <see cref="Short"/>。
///
/// <c>DailyRank</c> 按行号递增：真源的这一列跨抓取会变（见 project_block_trade_daily_rank），
/// 但同一次查询内部是稳的——模拟源照这个语义来就够了。
/// </summary>
public sealed class MockBlockTradeProvider : IBlockTradeDayFetcher
{
    /// <summary>接口要求（2026-09-22）。模拟源不发请求，也就没有退避/重试可报，一直是空的。</summary>
    public event Action<string>? OnStatus;
    /// <summary>每天几行。</summary>
    public const int RowsPerDay = 6;

    /// <summary>一眼就知道是假数据的成交额（元）。</summary>
    public const double Amount = 1_111_111;

    /// <summary>这天（含）起返回 0 行。**默认是今天**——大宗当晚才发布。</summary>
    public DateOnly? EmptyFrom { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>这些天少给一行、但 count 照报全数——模拟"某页被限流截断"。</summary>
    public HashSet<DateOnly> Short { get; init; } = [];

    /// <summary>这些天抛异常。</summary>
    public HashSet<DateOnly> Throws { get; init; } = [];

    public Task<BlockTradeDay> FetchBlockTradesOfDayAsync(DateTime day, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var d = DateOnly.FromDateTime(day.Date);

        if (Throws.Contains(d))
            throw new InvalidOperationException($"[模拟源] 大宗交易 {d:yyyy-MM-dd} 按约定抓取失败");

        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || (EmptyFrom is { } e && d >= e))
            return Task.FromResult(new BlockTradeDay(day.Date, [], 0));

        var rows = new List<BlockTrade>();
        for (int i = 0; i < RowsPerDay; i++)
        {
            // 前一半沪市、后一半深市——日频体检有"某个交易所整天没有"的判据。
            string code = i < RowsPerDay / 2 ? $"6000{i:D2}" : $"0000{i:D2}";
            rows.Add(new BlockTrade
            {
                Code = code,
                Name = $"模拟{i}",
                TradeDate = day.Date,
                DailyRank = i + 1,
                DealPrice = 11.11,
                DealVolume = 10_000,
                DealAmount = Amount + i,
                PremiumRatio = 0,
                ClosePrice = 11.11,
            });
        }

        int reported = rows.Count;
        if (Short.Contains(d)) rows.RemoveAt(rows.Count - 1);   // count 照报全数
        return Task.FromResult(new BlockTradeDay(day.Date, rows, reported));
    }
}
