namespace StockPlatform.Data.Orchestration;

using StockPlatform.Logic.Abstractions;

/// <summary>
/// "抓融资余额的某一天并落库"这一个动作（2026-09-18）。照 <see cref="LhbSeatDayWriter"/> 抽的。
///
/// 单独成类是因为它有**三个调用方**，各写一份必然漂移：
///   · <c>MarginTask</c>——日常增量、整段回补、补待办；
///   · <c>FetchOrchestrator.BackfillDailyAsync</c>——【拉取历史区间】里的两融那半边。
///
/// ════ 跟龙虎榜/席位/大宗那三个 writer 最要紧的差别 ════
/// 这张表是 <b>InsertOrIgnore 合并</b>，不是整日替换。于是那三张表最要命的
/// "抓不全就整天不落库"在这里**反过来**：两所分批发布，抓到半天也该写进去，
/// 主键去重，下轮把另一半补上。所以这里**不做** count 校验、不抛"没抓全"。
///
/// 残缺日因此也是靠"再抓一次、合并"修好的（见 <see cref="PartialDayRepair"/> 的进度文案）。
/// </summary>
/// <param name="provider">交易所官方源（上交所 JSON + 深交所 xlsx，合并返回）。</param>
/// <param name="repository">落库。</param>
/// <param name="dbLock">
/// 写库的互斥锁，**可选**。编排器有自己的 <c>_dbLock</c>，传进来才是同一把；
/// 新框架的任务没有，传 null 即可。
/// </param>
public sealed class MarginDayWriter(
    IMarginProvider provider,
    IMarginRepository repository,
    object? dbLock = null)
{
    /// <summary>
    /// 抓某一天并合并落库，返回写入行数。
    /// 返回 0＝那天数据源上没有（调用方据此决定动不动"确认没有数据"名单，
    /// 判据见 <c>DailyNoDataGate</c>——**不在这里判**，因为那是排期的事，
    /// 而【拉取历史区间】那条路自己已经在判了，放进来会做两遍）。
    /// </summary>
    public async Task<int> RefetchAsync(DateOnly day, CancellationToken ct = default)
    {
        var rows = await provider.GetDetailAsync(day, ct);
        if (rows.Count == 0) return 0;
        if (dbLock == null) repository.InsertOrIgnore(rows);
        else lock (dbLock) repository.InsertOrIgnore(rows);
        return rows.Count;
    }
}
