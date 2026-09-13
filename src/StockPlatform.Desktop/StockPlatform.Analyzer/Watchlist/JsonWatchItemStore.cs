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

    /// <summary>
    /// 把一条触发标成"已处理/未处理"（2026-09-12）。
    ///
    /// ⚠ 这是**人的状态**，跟数据分开：重算随时会重新求值、重新落触发，
    /// 但"我看过了"不该被任何重算抹掉。所以它存在触发记录本身上，
    /// 而 <see cref="AppendHits"/> 是"同项同日已存在就跳过"，天然不会覆盖已有的标记。
    /// </summary>
    /// <returns>改动了几条（找不到就是 0）。</returns>
    public int MarkHandled(IEnumerable<Guid> hitIds, bool handled)
    {
        var ids = hitIds.ToHashSet();
        if (ids.Count == 0) return 0;

        lock (_fileLock)
        {
            int changed = 0;
            // 标记可能跨年（比如翻到去年的记录去标），所以按年份逐个文件找
            foreach (var year in Enumerable.Range(DateTime.Today.Year - 5, 7))
            {
                var list = LoadHitsUnlocked(year);
                if (list.Count == 0) continue;

                int n = 0;
                foreach (var h in list.Where(h => ids.Contains(h.HitId)))
                {
                    if (h.Handled == handled) continue;
                    h.Handled = handled;
                    h.HandledAt = handled ? DateTime.Now : null;
                    n++;
                }
                if (n == 0) continue;

                var p = _hitsPath(year);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(list, JsonOptions));
                changed += n;
            }
            return changed;
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
