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
}
