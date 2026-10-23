namespace StockPlatform.Logic.Abstractions;

/// <summary>Per-stock announcement detail lookup — the "detail" half of the order-win pipeline
/// (see <see cref="IAnnouncementSearchProvider"/> for the "discovery" half). Given a hit found by
/// discovery (which only has a title/date, no full text), tries to find the matching article on a
/// source that exposes plain-text bodies directly (avoiding PDF parsing) and return its content.
/// Returns null if no matching article could be found — callers should treat this as a soft
/// failure (keep the discovery-only record) rather than an error.</summary>
public interface IAnnouncementDetailFetcher
{
    Task<(string ArtCode, string Content)?> FetchDetailAsync(string code, string title, DateOnly approxDate, CancellationToken ct = default);

    /// <summary>
    /// 已知文章编号（之前 <see cref="FetchDetailAsync"/> 配上的 art_code），直接取正文——一个请求，
    /// 不用再在该股最近 100 条公告里按标题匹配，所以也不受"最近 100 条"的限制（2026-10-23，给重抽用）。
    /// 源头说这篇没有正文返回 null；网络/限流照常抛异常。
    /// </summary>
    Task<string?> FetchContentAsync(string artCode, CancellationToken ct = default);
}
