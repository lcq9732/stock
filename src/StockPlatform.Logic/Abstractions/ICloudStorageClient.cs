using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// Abstraction over "wherever the shared master/daily files and manifest live" (see
/// doc/data-platform-design.md section 6.5). The current implementation is a manual
/// staging-folder + user prompt (no cloud API credentials yet); a future implementation
/// can call the real Baidu Netdisk (or other) API without changing any calling code.
/// </summary>
public interface ICloudStorageClient
{
    Task UploadAsync(string localFilePath, string remoteFolder, CancellationToken ct = default);
    Task DownloadAsync(string remoteFileName, string remoteFolder, string localFilePath, CancellationToken ct = default);
    Task<List<CloudFileInfo>> ListFilesAsync(string remoteFolder, CancellationToken ct = default);
    Task DeleteAsync(string remoteFileName, string remoteFolder, CancellationToken ct = default);
}
