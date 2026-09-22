using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的中标/订单公告源（2026-09-22）——一个请求都不发，按本地名册凭空造命中。
/// 照 <see cref="MockBarFetcher"/> 抽的，安全边界也一样（只在 DEBUG 构建里注册 + Debug 数据目录隔离）。
///
/// ════ 为什么补这一个 ════
/// 它是离线总开关最后一个漏的（2026-09-22 实测：验【拉取区间数据】时公告那一步在真抓巨潮全文检索，
/// 关键词「中标」翻了几十页、又逐条查详情）。总开关的意义就是"这一轮一个真请求都不发"，
/// 漏一个就不成立——而漏的那个往往正是跑得最久的那个。
///
/// ════ 造什么 ════
/// 从本地名册取前 <see cref="HitsPerKeyword"/> 只票，各造一条标题里含关键词的公告，
/// 发布日均匀铺在请求区间里。详情那半边按标题原样回一段文本。
/// 量很小是故意的：要验的是"这一步跑通了、切片和落库都对"，不是压测。
/// </summary>
public sealed class MockAnnouncementProvider(Func<IReadOnlyList<StockListEntry>> roster)
    : IAnnouncementSearchProvider, IAnnouncementDetailFetcher
{
    /// <summary>每个关键词造几条命中。</summary>
    public const int HitsPerKeyword = 20;

    public Task<List<AnnouncementSearchHit>> SearchAsync(
        string keyword, DateOnly start, DateOnly end,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var codes = roster().Take(HitsPerKeyword).ToList();
        var span = Math.Max(1, end.DayNumber - start.DayNumber);
        var hits = new List<AnnouncementSearchHit>(codes.Count);
        for (int i = 0; i < codes.Count; i++)
        {
            var day = start.AddDays(span * i / Math.Max(1, codes.Count));
            hits.Add(new AnnouncementSearchHit(
                codes[i].Code, codes[i].Name,
                $"[模拟源] {codes[i].Name}关于{keyword}项目的公告",
                day.ToDateTime(TimeOnly.MinValue), PdfUrl: null));
        }

        progress?.Report($"[模拟源] 关键词「{keyword}」造了 {hits.Count} 条命中"
                       + $"（{start:yyyy-MM-dd}~{end:yyyy-MM-dd}），未发任何请求");
        return Task.FromResult(hits);
    }

    public Task<(string ArtCode, string Content)?> FetchDetailAsync(
        string code, string title, DateOnly approxDate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<(string, string)?>(
            ($"mock-{code}-{approxDate:yyyyMMdd}",
             $"[模拟源] {title}。本公告为离线模拟生成，未发任何请求。"));
    }
}
