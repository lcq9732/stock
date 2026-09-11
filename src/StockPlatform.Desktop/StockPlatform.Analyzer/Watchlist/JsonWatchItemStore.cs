using System.IO;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 观察项和触发记录的 json 存取（2026-09-11），见 doc/watch-item-design.md §4.2。
/// 路数同 <see cref="JsonWatchlistStore"/>：分析程序自己的本地状态，不进 current.sqlite。
///
/// ⚠ 两种增删语义，别混（设计文档 §2.1）：
/// · <b>观察项</b>（items.json）是**待办**——有挂有摘，派生项每轮重建。
/// · <b>触发记录</b>（hits-{年}.json）是**事实**——只增不删，它是"当时确实报过"的证据。
/// </summary>
public class JsonWatchItemStore
{
    private readonly string _itemsPath;
    private readonly Func<int, string> _hitsPath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonWatchItemStore(string itemsPath, Func<int, string> hitsPath)
    {
        _itemsPath = itemsPath;
        _hitsPath = hitsPath;
    }

    public List<WatchItem> LoadItems()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_itemsPath)) return [];
                var json = File.ReadAllText(_itemsPath);
                if (string.IsNullOrWhiteSpace(json)) return [];
                return JsonSerializer.Deserialize<List<WatchItem>>(json) ?? [];
            }
            catch
            {
                // 文件坏了返回空而不是抛：读不出来不该拦住整个分析程序启动。
                // ⚠ 但调用方**不能**把空结果当成"没有观察项"去写回——那会把手写项抹掉。
                // 所以 SaveItems 里有一道空集合保护，见那边的注释。
                return [];
            }
        }
    }

    /// <summary>
    /// 写回全部观察项。
    /// ⚠ 空集合直接拒绝写：<see cref="LoadItems"/> 在文件损坏时返回空，
    /// 如果那时候正好触发一次"重算 → 写回"，手写的观察项就被静默抹掉了。
    /// 真要清空得手动删文件。跟库里那几张表"空集合是空操作"是同一条铁律。
    /// </summary>
    public void SaveItems(List<WatchItem> items)
    {
        if (items.Count == 0 && File.Exists(_itemsPath)) return;
        lock (_fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_itemsPath)!);
            File.WriteAllText(_itemsPath, JsonSerializer.Serialize(items, JsonOptions));
        }
    }

    public List<WatchHit> LoadHits(int year)
    {
        lock (_fileLock)
        {
            try
            {
                var p = _hitsPath(year);
                if (!File.Exists(p)) return [];
                var json = File.ReadAllText(p);
                if (string.IsNullOrWhiteSpace(json)) return [];
                return JsonSerializer.Deserialize<List<WatchHit>>(json) ?? [];
            }
            catch { return []; }
        }
    }

    /// <summary>
    /// 追加触发记录，**同一条观察项同一个交易日只记一次**——
    /// 不去重的话每天求值都会给同一件事再记一条，一个月后日报里全是重复。
    /// </summary>
    /// <returns>真正新增的条数。</returns>
    public int AppendHits(IEnumerable<WatchHit> hits)
    {
        var list = hits.ToList();
        if (list.Count == 0) return 0;

        lock (_fileLock)
        {
            int added = 0;
            foreach (var g in list.GroupBy(h => h.TriggerTradeDate.Year))
            {
                var existing = LoadHitsUnlocked(g.Key);
                var seen = existing
                    .Select(h => (h.ItemId, h.TriggerTradeDate.Date))
                    .ToHashSet();

                foreach (var h in g)
                    if (seen.Add((h.ItemId, h.TriggerTradeDate.Date)))
                    { existing.Add(h); added++; }

                if (added > 0)
                {
                    var p = _hitsPath(g.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    File.WriteAllText(p, JsonSerializer.Serialize(existing, JsonOptions));
                }
            }
            return added;
        }
    }

    private List<WatchHit> LoadHitsUnlocked(int year)
    {
        try
        {
            var p = _hitsPath(year);
            if (!File.Exists(p)) return [];
            var json = File.ReadAllText(p);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonSerializer.Deserialize<List<WatchHit>>(json) ?? [];
        }
        catch { return []; }
    }
}
