namespace StockPlatform.Logic.Models;

public class StockMeta
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Exchange { get; set; } = "";
    public DateTime? ListDate { get; set; }
    public DateTime LastUpdated { get; set; }
}
