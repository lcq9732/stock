using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Reads/writes the local manifest.json that tracks the current master/daily database files.</summary>
public interface IManifestStore
{
    Manifest Load();
    void Save(Manifest manifest);
}
