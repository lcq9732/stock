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

    /// <summary>更新某条自选的手动交易信息（买入日期/买入价/股数/卖出日期/卖出价，自选股Tab里直接
    /// 编辑）——按 Id 定位、只改这五个字段后整体保存。加载-修改-保存都在锁内完成，跟 Add/Remove
    /// 一样保持单写者语义。</summary>
    public void UpdateTradeInfo(Guid id, DateTime? buyDate, double? buyPrice, int? shares, DateTime? sellDate, double? sellPrice)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var entry = all.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.BuyDate = buyDate;
            entry.BuyPrice = buyPrice;
            entry.Shares = shares;
            entry.SellDate = sellDate;
            entry.SellPrice = sellPrice;
            Save(all);
        }
    }

    /// <summary>把若干条自选加入/移出"我的交易池"（2026-07-31新增）——交易池是"我打算买卖、要每天盯"的
    /// 那一小撮，跟"算法验证样本"分开（见 <see cref="WatchlistEntry.InTradePool"/>）。返回实际改动的条数。
    /// 移出时不清空买卖信息（交易记录要留痕）：**未平仓的持仓**移不出去（<see cref="WatchlistEntry.IsInTradePool"/>
    /// 恒为真），要移出得先清掉买入信息；已平仓的可以正常移出。</summary>
    public int SetTradePool(IEnumerable<Guid> ids, bool inPool)
    {
        lock (_fileLock)
        {
            var idSet = ids.ToHashSet();
            var all = LoadUnlocked();
            int changed = 0;
            foreach (var e in all.Where(e => idSet.Contains(e.Id)))
            {
                // 两个标记一起写，保证"加入↔移出"可以来回切：加入=显式加入+清掉移出标记；
                // 移出=打上移出标记+清掉加入标记（见 WatchlistEntry.IsInTradePool 的判定）。
                var before = e.IsInTradePool;
                e.InTradePool = inPool;
                e.RemovedFromPool = !inPool;
                if (e.IsInTradePool != before) changed++;
            }
            Save(all);
            return changed;
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
