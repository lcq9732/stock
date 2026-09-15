using System.IO;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.Watchlist;

/// <summary>
/// 费率设置的持久化 + 全局共享实例。跟 <see cref="JsonWatchlistStore"/> 一样是个小 JSON 文件
/// （<c>data\trade-fees.json</c>）：全程序就一份设置，各页共用同一个 <see cref="Current"/> 对象，
/// 用户改完立刻落盘，下次开程序还是这个费率。文件不存在（第一次用）就用默认值，不写文件。
/// </summary>
public class TradeFeeStore
{
    private readonly string _filePath;
    private readonly object _fileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>当前生效的费率——各页直接读它（不要各自拷一份，否则改完这页那页还是老费率）。</summary>
    public TradeFeeSettings Current { get; private set; } = new();

    public TradeFeeStore(string filePath)
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
                Current = JsonSerializer.Deserialize<TradeFeeSettings>(json) ?? new TradeFeeSettings();
            }
            catch (Exception)
            {
                // 设置文件坏了不该让程序打不开——退回默认费率，用户在界面上重填一次就好。
                Current = new TradeFeeSettings();
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
