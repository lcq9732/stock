namespace StockPlatform.Data.Orchestration;

using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

/// <summary>
/// "把一批龙虎榜行落库"这一个动作（2026-09-17）——派生对应值，然后**整天替换**。
///
/// ════ 为什么抽出来 ════
/// 迁到新框架之前，这张表有**四个入口、三套口径**：
///
/// | 入口 | 落库 | 派生对应值 |
/// |---|---|---|
/// | 日常增量（`FetchLhbRangeAsync`） | `ReplaceDays` | 有 |
/// | 只抓某一天 | `InsertOrIgnore` | **无** |
/// | 整段回补 | `InsertOrIgnore` | **无** |
/// | 补残缺日 | `InsertOrIgnore` | **无** |
///
/// 后三条写进去的新行 <c>deviation</c> 永远是空的，而增量那条会算。四份实现放着必然继续漂移，
/// 所以收成一个类，统一到**已经正确的那条**（派生 + 整天替换）。
///
/// ⚠ 整天替换而不是 upsert：<c>reason</c>（上榜原因）是主键的一部分，而它的文本会随数据源和
/// 交易所措辞变化——upsert 只会并排多出一套行，26 万行的历史会变成 53 万行，且没有任何字段
/// 能一眼分出哪套是哪套。<c>ReplaceDays</c> 的注释里记着这笔账。
/// </summary>
/// <param name="dbPath">当前库路径——派生对应值要读本地日K。</param>
/// <param name="repository">落库。</param>
/// <param name="provider">数据源，只有 <see cref="RefetchAsync"/>（按天重抓）用得到。</param>
/// <param name="dbLock">
/// 写库的互斥锁，**可选**。编排器有自己的 <c>_dbLock</c>，传进来才是同一把；
/// 新框架的任务没有，传 null 即可。
/// </param>
public sealed class LhbDayWriter(
    string dbPath,
    ILhbRepository repository,
    ILhbProvider? provider = null,
    object? dbLock = null)
{
    /// <summary>
    /// 落一批：先补派生列，再按行里的日期整天替换。返回写入行数。
    ///
    /// 派生器**每批新建一个**：它内部按 (代码,粒度) 缓存日K，而这里给 loadBars 传的是
    /// **限定在这批日期附近**的区间——一只票取全历史日K是几百倍的浪费。缓存跟着批走，
    /// 语义才不会串。
    /// </summary>
    public int Write(IReadOnlyList<LhbRow> rows)
    {
        if (rows.Count == 0) return 0;

        // 派生只需要上榜日和它前一天的K线，前后各放 10 天足够跨过长假
        var from = rows.Min(r => r.TradeDate).AddDays(-10);
        var to = rows.Max(r => r.TradeDate).AddDays(1);
        var barRepo = new SqliteBarRepository(dbPath);
        var deriver = new LhbDeviationDeriver((code, gran) => barRepo.Query(code, gran, from, to));
        deriver.Apply(rows);

        if (dbLock == null) return repository.ReplaceDays(rows).Inserted;
        lock (dbLock) return repository.ReplaceDays(rows).Inserted;
    }

    /// <summary>
    /// 抓某一天并落库，返回写入行数。那天数据源本来就没有（0 行）时返回 0，**不删**已有的行。
    ///
    /// 【重新拉取失败】补残缺日走这条；任务侧日常走 <see cref="Write"/>（一批是一个月片）。
    /// 两边共用同一套落库口径，正是抽这个类的目的。
    /// </summary>
    public async Task<int> RefetchAsync(DateOnly day, CancellationToken ct = default)
    {
        if (provider == null)
            throw new InvalidOperationException("这个 LhbDayWriter 没配数据源，只能用 Write()。");
        var rows = await provider.GetDailyAsync(day, ct);
        return rows.Count == 0 ? 0 : Write(rows);
    }
}
