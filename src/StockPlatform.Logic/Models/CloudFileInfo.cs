namespace StockPlatform.Logic.Models;

public class CloudFileInfo
{
    public string Name { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
}
