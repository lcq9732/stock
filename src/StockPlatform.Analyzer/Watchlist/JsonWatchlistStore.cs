using System.IO;
using System.Text.Json;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// Reads/writes the user's watchlist as a flat JSON array — plenty for what's realistically a few
/// dozen-to-hundred hand-picked entries, not worth a SQLite table (and keeping it out of
/// total.sqlite matters more than the storage format — see AnalyzerPaths.WatchlistPath).
/// </summary>
public class JsonWatchlistStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonWatchlistStore(string filePath)
    {
        _filePath = filePath;
    }

    public List<WatchlistEntry> Load()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_filePath)) return new List<WatchlistEntry>();
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return new List<WatchlistEntry>();
            return JsonSerializer.Deserialize<List<WatchlistEntry>>(json) ?? new List<WatchlistEntry>();
        }
    }

    private void Save(List<WatchlistEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    /// <summary>Adds the given entries, skipping any that already exist for the same
    /// (Code, Method, DataDate) — re-checking the same stock/method/day combo shouldn't pile up
    /// duplicate rows every time the user re-runs the same analysis and re-checks it. Returns how
    /// many were actually added (for the caller to report back to the user).</summary>
    public int Add(IEnumerable<WatchlistEntry> newEntries)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var existingKeys = all.Select(EntryKey).ToHashSet();
            int added = 0;
            foreach (var entry in newEntries)
            {
                if (!existingKeys.Add(EntryKey(entry))) continue;
                all.Add(entry);
                added++;
            }
            if (added > 0) Save(all);
            return added;
        }
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_fileLock)
        {
            var idSet = ids.ToHashSet();
            var all = LoadUnlocked();
            all.RemoveAll(e => idSet.Contains(e.Id));
            Save(all);
        }
    }

    private List<WatchlistEntry> LoadUnlocked()
    {
        if (!File.Exists(_filePath)) return new List<WatchlistEntry>();
        var json = File.ReadAllText(_filePath);
        if (string.IsNullOrWhiteSpace(json)) return new List<WatchlistEntry>();
        return JsonSerializer.Deserialize<List<WatchlistEntry>>(json) ?? new List<WatchlistEntry>();
    }

    private static (string, string, DateTime) EntryKey(WatchlistEntry e) => (e.Code, e.Method, e.DataDate.Date);
}
