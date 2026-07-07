namespace StockPlatform.Logic.Services;

/// <summary>Naming convention for shared files (see doc/data-platform-design.md section 6.1).</summary>
public static class FileNaming
{
    public static string MasterFile(DateOnly asOfDate) => $"stockdata_master_{asOfDate:yyyyMMdd}.sqlite";
    public static string DailyFile(DateOnly date) => $"stockdata_daily_{date:yyyyMMdd}.sqlite";

    public static bool TryParseMasterDate(string fileName, out DateOnly date) =>
        TryParseDate(fileName, "stockdata_master_", out date);

    public static bool TryParseDailyDate(string fileName, out DateOnly date) =>
        TryParseDate(fileName, "stockdata_daily_", out date);

    private static bool TryParseDate(string fileName, string prefix, out DateOnly date)
    {
        date = default;
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith(prefix)) return false;
        var datePart = name[prefix.Length..];
        return DateOnly.TryParseExact(datePart, "yyyyMMdd", out date);
    }
}
