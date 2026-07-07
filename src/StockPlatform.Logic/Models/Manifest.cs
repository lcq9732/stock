namespace StockPlatform.Logic.Models;

public class ManifestFileRef
{
    public string File { get; set; } = "";
    public DateOnly AsOfDate { get; set; }
}

public class ManifestDailyRef
{
    public string File { get; set; } = "";
    public DateOnly Date { get; set; }
}

/// <summary>
/// Describes the current state of the shared data set (see doc/data-platform-design.md section 6.1).
/// Lives alongside the master/daily files wherever they're stored (local staging folder or, once
/// automated, the cloud drive itself).
/// </summary>
public class Manifest
{
    public ManifestFileRef? CurrentMaster { get; set; }
    public ManifestFileRef? PreviousMaster { get; set; }
    public List<ManifestDailyRef> DailyFiles { get; set; } = new();
}
