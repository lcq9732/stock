namespace StockPlatform.Data.Orchestration;

using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

/// <summary>
/// "抓大宗交易的某一天并落库"这一个动作（2026-09-17）。
///
/// 单独成类是因为它有**两个调用方**，而且这两方都不能各写一份：
///   · <c>BlockTradeTask</c>——日常增量、整段回补，一天一批；
///   · <c>FetchOrchestrator.DailyRefetcherFor</c>——【重新拉取失败】补残缺日时按天重抓。
/// 各写一份必然漂移，而这里面正好藏着这张表最要命的一条规矩（见下）。
///
/// ⚠ **抓不全就抛，绝不落库**。大宗是整日替换（删掉那天重写），拿收了一半的数据去替换
/// 完整的一天，库里看不出来、体检也看不出来——行数判据只会觉得"那天本来就少"。
/// 所以宁可抛异常让上层记成失败/残缺日，下轮再来。
/// </summary>
/// <param name="provider">东财 datacenter。</param>
/// <param name="repository">落库。</param>
/// <param name="dbLock">
/// 写库的互斥锁，**可选**。编排器有自己的 <c>_dbLock</c>，传进来才是同一把；
/// 新框架的任务没有，传 null 即可。
/// </param>
public sealed class BlockTradeDayWriter(
    IBlockTradeDayFetcher provider,
    IMarketEventRepository repository,
    object? dbLock = null)
{
    /// <summary>
    /// 抓某一天并整日替换，返回写入行数。
    /// 那天数据源本来就没有（接口自报 0 行）时返回 0，**不删**已有的行。
    /// </summary>
    /// <exception cref="InvalidOperationException">没抓全——实收行数跟接口自报的对不上。</exception>
    public async Task<int> RefetchAsync(DateOnly day, CancellationToken ct = default)
    {
        var d = day.ToDateTime(TimeOnly.MinValue);
        var one = await provider.FetchBlockTradesOfDayAsync(d, ct);
        if (!one.IsComplete)
            throw new InvalidOperationException(
                $"{d:yyyy-MM-dd} 的大宗交易只收到 {one.Rows.Count} 行、接口自报 {one.ReportedCount} 行，"
                + "多半是某页被限流截断——没抓全就不落库，免得拿半天的数据盖掉完整的一天。");
        if (one.Rows.Count == 0) return 0;
        if (dbLock == null) return repository.ReplaceBlockTradesForDay(d, one.Rows);
        lock (dbLock) return repository.ReplaceBlockTradesForDay(d, one.Rows);
    }
}
