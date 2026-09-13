using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Orchestration;

public class JsonManifestStore : IManifestStore
{
    private readonly string _filePath;

    public JsonManifestStore(string filePath)
    {
        _filePath = filePath;
    }

    public Manifest Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new Manifest();
            var json = File.ReadAllText(_filePath);
            var manifest = JsonSerializer.Deserialize<Manifest>(json) ?? new Manifest();
            // 老格式（九个各自为政的名单）就地并进 Todos，**读完立刻转**——这样内存里的 manifest
            // 永远是新格式，全程序不用再有第二种读法，也不会有"这个字段到底还准不准"的疑问。
            // 落盘时那九个字段就是空数组，下一次读进来什么都不用做（幂等）。
            manifest.MigrateLegacyTodos();
            return manifest;
        }
        catch
        {
            return new Manifest();
        }
    }

    public void Save(Manifest manifest)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
