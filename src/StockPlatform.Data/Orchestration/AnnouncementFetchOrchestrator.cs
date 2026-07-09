using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Orchestration;

public class AnnouncementFetchResult
{
    public int Discovered { get; set; }
    public int DetailMatched { get; set; }
    public int AmountExtracted { get; set; }
    public int Stored { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Ties together the order-win announcement pipeline (see doc: 中标/订单公告 抓取方案):
/// 1. Discovery — market-wide keyword search (<see cref="IAnnouncementSearchProvider"/>, cninfo)
///    across all listed companies for a date window, per keyword (e.g. "中标"、"签订合同").
/// 2. Detail — best-effort per-hit lookup of the full plain-text body (<see cref="IAnnouncementDetailFetcher"/>,
///    EastMoney), so amounts don't require PDF parsing. A hit with no detail match is still stored
///    (title/date/PDF link only) — a soft miss, not a run failure.
/// 3. Extraction — <see cref="OrderWinExtractor"/> pulls a headline total amount out of the body
///    when present.
/// 4. Storage — <see cref="IAnnouncementRepository"/> (SQLite), deduped on (code, title, publish_date).
/// </summary>
public class AnnouncementFetchOrchestrator
{
    private readonly IAnnouncementSearchProvider _searchProvider;
    private readonly IAnnouncementDetailFetcher _detailFetcher;
    private readonly IAnnouncementRepository _repository;

    public AnnouncementFetchOrchestrator(
        IAnnouncementSearchProvider searchProvider,
        IAnnouncementDetailFetcher detailFetcher,
        IAnnouncementRepository repository)
    {
        _searchProvider = searchProvider;
        _detailFetcher = detailFetcher;
        _repository = repository;
    }

    public async Task<AnnouncementFetchResult> RunAsync(
        IReadOnlyList<string> keywords, DateOnly start, DateOnly end,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        _repository.EnsureSchema();
        var result = new AnnouncementFetchResult();
        var now = DateTime.Now;

        foreach (var keyword in keywords)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"开始搜索关键词\"{keyword}\"（{start:yyyy-MM-dd} 至 {end:yyyy-MM-dd}）...");

            List<AnnouncementSearchHit> hits;
            try
            {
                hits = await _searchProvider.SearchAsync(keyword, start, end, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Errors.Add($"关键词\"{keyword}\"搜索失败：{ex.Message}");
                continue;
            }

            result.Discovered += hits.Count;
            progress?.Report($"关键词\"{keyword}\"共命中 {hits.Count} 条公告，开始逐条查详情...");

            var toStore = new List<OrderWinAnnouncement>();
            var doneCount = 0;
            foreach (var hit in hits)
            {
                ct.ThrowIfCancellationRequested();
                var record = new OrderWinAnnouncement
                {
                    Code = hit.Code,
                    Name = hit.Name,
                    Title = hit.Title,
                    PublishDate = hit.PublishDate,
                    Keyword = keyword,
                    PdfUrl = hit.PdfUrl,
                    Source = "cninfo+eastmoney",
                    FetchedAt = now,
                };

                try
                {
                    var detail = await _detailFetcher.FetchDetailAsync(hit.Code, hit.Title, DateOnly.FromDateTime(hit.PublishDate), ct);
                    if (detail != null)
                    {
                        result.DetailMatched++;
                        record.ArtCode = detail.Value.ArtCode;
                        record.Content = detail.Value.Content;
                        record.TotalAmountYuan = OrderWinExtractor.ExtractTotalAmountYuan(detail.Value.Content);
                        if (record.TotalAmountYuan != null) result.AmountExtracted++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A single stock's detail lookup failing (rate limit, no match, etc.) shouldn't
                    // drop the whole hit — it just falls back to a discovery-only record.
                    result.Errors.Add($"{hit.Code} 详情查询失败：{ex.Message}");
                }

                toStore.Add(record);
                doneCount++;
                if (doneCount % 10 == 0 || doneCount == hits.Count)
                    progress?.Report($"关键词\"{keyword}\"详情查询进度 ({doneCount}/{hits.Count})");
            }

            _repository.InsertOrIgnore(toStore);
            result.Stored += toStore.Count;
        }

        progress?.Report(
            $"完成：发现 {result.Discovered} 条，匹配到详情 {result.DetailMatched} 条，" +
            $"提取到金额 {result.AmountExtracted} 条，已存入本地库 {result.Stored} 条" +
            (result.Errors.Count > 0 ? $"，其中 {result.Errors.Count} 条出错（不影响其余结果）" : ""));

        return result;
    }
}
