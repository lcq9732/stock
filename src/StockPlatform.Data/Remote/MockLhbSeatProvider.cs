using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的龙虎榜席位源（2026-09-18）——一个请求都不发，按交易日凭空造行。
/// 说明书见 doc/offline-mock-design.md；安全边界跟 <see cref="MockBarFetcher"/> 一样
/// （只在 DEBUG 构建里挂上去 + Debug 数据目录隔离）。
///
/// ⚠ **`ReportedBuy`/`ReportedSell` 必须跟实际造的行数对上**：这张表是整日替换，
/// 任务侧和 <c>LhbSeatDayWriter</c> 都会用 <see cref="LhbSeatDay.IsComplete"/> 把
/// "没抓全"的那天整天挡下来。对不上的话，模拟源一开，每天都变成"残缺日"，
/// 测的就不是要测的东西了。真要模拟截断，用 <see cref="ShortBuy"/>/<see cref="ShortSell"/>。
/// </summary>
public sealed class MockLhbSeatProvider : ILhbSeatDayFetcher
{
    /// <summary>每天每侧几个席位。</summary>
    public const int SeatsPerSide = 5;

    /// <summary>这天（含）起返回 0 行。**默认是今天**——龙虎榜当晚才发布。</summary>
    public DateOnly? EmptyFrom { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>这些天**买方**少给一行、但 count 照报全数——模拟"某页被限流截断"。</summary>
    public HashSet<DateOnly> ShortBuy { get; init; } = [];

    /// <summary>这些天**卖方**少给一行、但 count 照报全数。</summary>
    public HashSet<DateOnly> ShortSell { get; init; } = [];

    /// <summary>这些天抛异常。</summary>
    public HashSet<DateOnly> Throws { get; init; } = [];

    public Task<LhbSeatDay> FetchLhbSeatsOfDayAsync(DateTime day, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var d = DateOnly.FromDateTime(day.Date);

        if (Throws.Contains(d))
            throw new InvalidOperationException($"[模拟源] 龙虎榜席位 {d:yyyy-MM-dd} 按约定抓取失败");

        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || (EmptyFrom is { } e && d >= e))
            return Task.FromResult(new LhbSeatDay(day.Date, [], 0, 0));

        var rows = new List<LhbSeat>();
        foreach (var isBuy in new[] { true, false })
            for (int i = 0; i < SeatsPerSide; i++)
            {
                // 前一半沪市、后一半深市——日频体检有"某个交易所整天没有"的判据。
                string code = i < SeatsPerSide / 2 ? $"6000{i:D2}" : $"0000{i:D2}";
                rows.Add(new LhbSeat
                {
                    Code = code,
                    Name = $"模拟{i}",
                    TradeDate = day.Date,
                    IsBuy = isBuy,
                    SeatCode = $"100{i}",
                    SeatName = $"模拟营业部{i}",
                    Buy = isBuy ? 1_111_111 - i : null,
                    Sell = isBuy ? null : 1_111_111 - i,
                    Net = (isBuy ? 1 : -1) * (1_111_111 - i),
                    Explanation = "日涨幅偏离值达到7%的前5只证券",
                    FetchedAt = day.Date.AddHours(20),
                });
            }

        // count 照报全数，再把该少的那行拿掉——这正是"限流截断"在真源上的样子。
        int reported = SeatsPerSide;
        if (ShortBuy.Contains(d)) rows.Remove(rows.Last(r => r.IsBuy));
        if (ShortSell.Contains(d)) rows.Remove(rows.Last(r => !r.IsBuy));

        EastMoneyLhbSeatProvider.AssignSeq(rows);
        return Task.FromResult(new LhbSeatDay(day.Date, rows, reported, reported));
    }
}
