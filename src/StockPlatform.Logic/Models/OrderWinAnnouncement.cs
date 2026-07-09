namespace StockPlatform.Logic.Models;

/// <summary>
/// A company announcement matched by an order/contract-win keyword sweep (e.g. "中标"、"签订合同").
/// Discovered via a market-wide full-text search (cninfo) and, when possible, enriched with the
/// full plain-text body via a per-stock detail lookup (EastMoney) so <see cref="TotalAmountYuan"/>
/// can be extracted without downloading/parsing the PDF attachment.
/// </summary>
public class OrderWinAnnouncement
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime PublishDate { get; set; }

    /// <summary>Which keyword this hit was found under (e.g. "中标") — one announcement can be
    /// stored once per matching keyword search since keywords are searched independently.</summary>
    public string Keyword { get; set; } = "";

    /// <summary>EastMoney's announcement id, only populated when the detail lookup found a match.</summary>
    public string? ArtCode { get; set; }

    /// <summary>cninfo's PDF attachment URL — always available from the discovery step, useful as
    /// a fallback when the EastMoney detail lookup fails to find a matching article.</summary>
    public string? PdfUrl { get; set; }

    /// <summary>Full plain-text body, only populated when the EastMoney detail lookup succeeded.</summary>
    public string? Content { get; set; }

    /// <summary>Best-effort amount extracted from a "合计" line in <see cref="Content"/>, in yuan.
    /// Null when there was no content to parse or no total line was found — deliberately not
    /// summed from individual line items, since a mis-parsed summary would silently double count.</summary>
    public double? TotalAmountYuan { get; set; }

    public string Source { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}
