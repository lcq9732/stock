using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Reads/writes manifest.json wherever the shared data set currently lives (local staging folder for now).</summary>
public interface IManifestStore
{
    Manifest Load();
    void Save(Manifest manifest);
}
