namespace StockPlatform.Data.Orchestration;

public class FetchResult
{
    public List<string> Errors { get; set; } = new();
}

/// <summary>See <see cref="FetchOrchestrator.GetDataStatus"/>.</summary>
public class DataStatus
{
    public DateTime? EarliestDay { get; set; }
    public DateTime? LatestDay { get; set; }
    public DateTime? LastFetchAt { get; set; }
    public string? LastFetchKind { get; set; }
}
