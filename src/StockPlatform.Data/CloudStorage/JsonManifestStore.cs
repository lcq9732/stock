using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.CloudStorage;

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
            return JsonSerializer.Deserialize<Manifest>(json) ?? new Manifest();
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
