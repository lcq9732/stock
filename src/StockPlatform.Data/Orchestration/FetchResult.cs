namespace StockPlatform.Data.Orchestration;

public class FetchResult
{
    public string? ProducedFile { get; set; }   // null if nothing new was fetched
    public string? OutboxPath { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class MergeResult
{
    public string NewMasterFile { get; set; } = "";
    public string OutboxPath { get; set; } = "";
}
