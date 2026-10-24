namespace StockPlatform.Logic.Abstractions;

/// <summary>Per-stock announcement detail lookup — the "detail" half of the order-win pipeline
/// (see <see cref="IAnnouncementSearchProvider"/> for the "discovery" half). Given a hit found by
/// discovery (which only has a title/date, no full text), tries to find the matching article on a
/// source that exposes plain-text bodies directly (avoiding PDF parsing) and return its content.
/// Returns null if no matching article could be found — callers should treat this as a soft
/// failure (keep the discovery-only record) rather than an error.</summary>
public interface IAnnouncementDetailFetcher
{
    /// <summary>
    /// 取数过程中的状态播报——限流退避、重试、"还在主动暂停、剩 N 秒"那类（2026-10-24）。
    ///
    /// ⚠ **不订阅的代价是静默**：这些消息大多来自 <c>RateLimiter</c>，它在长暂停期间每 30 秒
    /// 报一次"仍在主动暂停"，可之前这里没有这个事件、没人转发，于是一次 15 分钟的主动休息
    /// 在日志里一个字都没有，跟"调用进去没出来"长得一模一样，看门狗也分不开。
    /// 跟 <see cref="IBarDataFetcher.OnStatus"/> 是同一套做法，订阅方用骨架的 <c>ForwardStatus</c>。
    /// </summary>
    event Action<string>? OnStatus;

    Task<(string ArtCode, string Content)?> FetchDetailAsync(string code, string title, DateOnly approxDate, CancellationToken ct = default);

    /// <summary>
    /// 已知文章编号（之前 <see cref="FetchDetailAsync"/> 配上的 art_code），直接取正文——一个请求，
    /// 不用再在该股最近 100 条公告里按标题匹配，所以也不受"最近 100 条"的限制（2026-10-23，给重抽用）。
    /// 源头说这篇没有正文返回 null；网络/限流照常抛异常。
    /// </summary>
    Task<string?> FetchContentAsync(string artCode, CancellationToken ct = default);
}
