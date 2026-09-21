namespace StockPlatform.Logic.Services;

/// <summary>
/// 【拉取市场事件】每张表这一轮从哪天抓起——纯判据，零 IO。
/// 2026-09-21 从 <c>FetchOrchestrator.RunFetchMarketEventsAsync</c> 里那个局部函数抽出来。
///
/// ════ 三支，各有各的道理 ════
/// ① **整段回补**：不看水位线，从 <paramref name="floor"/> 重来一遍。
///    ⚠ 这个模式**不要靠删表来触发**——那会先丢数据再重下，中途失败就两头空。
///    这里是 UPSERT，停了再跑就行。
/// ② **首次**（本地一行都没有）：同样从 floor 起。
/// ③ **增量**：从水位线**那一天本身**起（不是次日）——公告是全天陆续发的，上次抓时当天
///    可能还没发完；主键 UPSERT 保证重抓不产生重复行。再额外往前推
///    <paramref name="lookbackDays"/> 天兜"公告补发/修订"：这几张都是按年切片的公告类数据，
///    多抓一小段成本极低。
/// </summary>
public static class MarketEventWindowRule
{
    /// <summary>这一轮从哪天抓起。</summary>
    /// <param name="fullBackfill">整段回补模式。</param>
    /// <param name="watermark">本地这张表最新那条的日期；null＝一行都没有（首次）。</param>
    /// <param name="floor">历史起点（再早数据源也没有）。</param>
    /// <param name="lookbackDays">增量时额外往前推几天。</param>
    public static (DateTime Start, string Mode) Start(
        bool fullBackfill, DateTime? watermark, DateTime floor, int lookbackDays)
    {
        if (fullBackfill)
            return (floor, "（整段回补：不看水位线，从头重取一遍）");

        if (watermark is not { } mark)
            return (floor, "（首次全量）");

        var start = mark.AddDays(-lookbackDays);
        if (start < floor) start = floor;
        return (start, $"（增量，含回看 {lookbackDays} 天补滞后字段）");
    }
}
