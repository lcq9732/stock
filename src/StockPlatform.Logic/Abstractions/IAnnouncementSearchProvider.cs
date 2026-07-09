namespace StockPlatform.Logic.Abstractions;

/// <summary>One market-wide full-text search hit — discovery only, no full body text yet.</summary>
public record AnnouncementSearchHit(string Code, string Name, string Title, DateTime PublishDate, string? PdfUrl);

/// <summary>Market-wide keyword search across all listed companies' announcements (e.g. cninfo's
/// full-text search) — the "discovery" half of the order-win pipeline (see
/// <see cref="IAnnouncementDetailFetcher"/> for the "detail" half).</summary>
public interface IAnnouncementSearchProvider
{
    Task<List<AnnouncementSearchHit>> SearchAsync(string keyword, DateOnly start, DateOnly end, IProgress<string>? progress = null, CancellationToken ct = default);
}
