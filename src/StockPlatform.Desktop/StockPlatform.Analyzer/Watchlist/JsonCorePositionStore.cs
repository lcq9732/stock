using System.IO;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 底仓记录的持久化（<c>data\core-positions.json</c>）——写法照
/// <see cref="JsonWatchlistStore"/>：一个扁平 JSON 数组，改完立刻整份落盘，文件不存在或坏了
/// 就当空列表。底仓realistically 就几只到十几只，不值得上 SQLite。
/// </summary>
public class JsonCorePositionStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonCorePositionStore(string filePath)
    {
        _filePath = filePath;
    }

    public List<CorePosition> Load()
    {
        lock (_fileLock) return LoadUnlocked();
    }

    private List<CorePosition> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_filePath)) return new List<CorePosition>();
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json)) return new List<CorePosition>();
            return JsonSerializer.Deserialize<List<CorePosition>>(json) ?? new List<CorePosition>();
        }
        catch (Exception)
        {
            // 同 JsonWatchlistStore：文件坏了不能让整个程序起不来，退回空列表。
            return new List<CorePosition>();
        }
    }

    private void Save(List<CorePosition> items)
        => File.WriteAllText(_filePath, JsonSerializer.Serialize(items, JsonOptions));

    /// <summary>加入底仓——**一只股票只留一条记录**（同 JsonWatchlistStore.Add 的去重口径）。
    /// 返回真正新增的条数。</summary>
    public int Add(IEnumerable<CorePosition> newItems)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var existing = all.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
            int added = 0;
            foreach (var item in newItems)
            {
                if (!existing.Add(item.Code)) continue;
                all.Add(item);
                added++;
            }
            if (added > 0) Save(all);
            return added;
        }
    }

    /// <summary>整份覆盖某条记录的成交明细（同 JsonWatchlistStore.UpdateLots：录成交窗口点保存后
    /// 把改完的整份 Lots 写回）。</summary>
    public void UpdateLots(Guid id, IEnumerable<TradeLot> lots)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.Lots = lots.ToList();
            Save(all);
        }
    }

    /// <summary>改目标年化股息（在【录成交】窗口里填，2026-08-20 从表格挪进去的）。</summary>
    public void UpdateTargetDividend(Guid id, double targetAnnualDividend)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.TargetAnnualDividend = targetAnnualDividend;
            Save(all);
        }
    }

    /// <summary>改建仓理由/退出条件备注（表格里直接编辑）。</summary>
    public void UpdateNote(Guid id, string note)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.Note = note ?? "";
            Save(all);
        }
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_fileLock)
        {
            var all = LoadUnlocked();
            var set = ids.ToHashSet();
            int before = all.Count;
            all.RemoveAll(e => set.Contains(e.Id));
            if (all.Count != before) Save(all);
        }
    }
}
