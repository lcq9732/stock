using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StockPlatform.Mobile.Services;

/// <summary>手机端自选股，存本地 JSON（App 数据目录 watchlist.json）。桌面版的 JsonWatchlistStore 在
/// WPF 工程里、字段也多，这里用一个精简版：只记代码/名称/选中日/当时价/加入时间，够做"选中后涨跌幅"跟踪。</summary>
public class WatchEntry
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataDate { get; set; } = "";   // 选中时最新一根日线的日期 yyyy-MM-dd
    public double PriceAtPick { get; set; }        // 那天的收盘价
    public string AddedAt { get; set; } = "";
}

public class WatchlistStore
{
    private readonly string _path;

    public WatchlistStore(string dataDir) => _path = Path.Combine(dataDir, "watchlist.json");

    public List<WatchEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<WatchEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch { return new(); }
    }

    private void Save(List<WatchEntry> list) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>加入自选，按 (代码, 选中日) 去重；返回是否新加。</summary>
    public bool Add(WatchEntry e)
    {
        var list = Load();
        if (list.Any(x => x.Code == e.Code && x.DataDate == e.DataDate)) return false;
        list.Add(e);
        Save(list);
        return true;
    }

    public void Remove(string code, string dataDate)
    {
        var list = Load();
        list.RemoveAll(x => x.Code == code && x.DataDate == dataDate);
        Save(list);
    }
}
