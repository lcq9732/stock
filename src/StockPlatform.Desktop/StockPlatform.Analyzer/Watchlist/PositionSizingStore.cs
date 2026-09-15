using System.IO;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 仓位计算器参数的持久化（<c>data\position-sizing.json</c>）——写法完全照 <see cref="TradeFeeStore"/>：
/// 全程序一份 <see cref="Current"/>，改完立刻落盘，文件不存在或坏了就退回默认值。
/// </summary>
public class PositionSizingStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PositionSizingSettings Current { get; private set; } = new();

    public PositionSizingStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json)) return;
                Current = JsonSerializer.Deserialize<PositionSizingSettings>(json) ?? new PositionSizingSettings();
            }
            catch (Exception)
            {
                Current = new PositionSizingSettings();
            }
        }
    }

    public void Save()
    {
        lock (_fileLock)
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
    }
}
