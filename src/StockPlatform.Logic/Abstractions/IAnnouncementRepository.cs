using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

public interface IAnnouncementRepository
{
    void EnsureSchema();
    void InsertOrIgnore(IEnumerable<OrderWinAnnouncement> items);
    List<OrderWinAnnouncement> Query(string? code = null, DateTime? start = null, DateTime? end = null);
}
