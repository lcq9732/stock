using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.CloudStorage;

/// <summary>
/// Stand-in <see cref="ICloudStorageClient"/> for use before a real cloud-drive API credential
/// (e.g. Baidu Netdisk AppKey/SecretKey) is available — see doc/data-platform-design.md section 6.5.
///
/// Instead of calling a real API, it treats two local folders as the "cloud":
/// - Outbox: files the producer has prepared and is waiting for the user to manually upload
/// - Inbox: files the user has manually downloaded from the real cloud drive, ready to be consumed
///
/// Swapping this out for a real `BaiduNetdiskClient` later requires no changes to calling code.
/// </summary>
public class ManualUploadPrompter : ICloudStorageClient
{
    public string OutboxFolder { get; }
    public string InboxFolder { get; }

    public ManualUploadPrompter(string outboxFolder, string inboxFolder)
    {
        OutboxFolder = outboxFolder;
        InboxFolder = inboxFolder;
        Directory.CreateDirectory(OutboxFolder);
        Directory.CreateDirectory(InboxFolder);
    }

    public Task UploadAsync(string localFilePath, string remoteFolder, CancellationToken ct = default)
    {
        var destDir = Path.Combine(OutboxFolder, remoteFolder);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(localFilePath));
        File.Copy(localFilePath, dest, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string remoteFileName, string remoteFolder, string localFilePath, CancellationToken ct = default)
    {
        var source = Path.Combine(InboxFolder, remoteFolder, remoteFileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"未在本地暂存目录找到 {remoteFileName}，请先手动从网盘下载该文件到：{Path.Combine(InboxFolder, remoteFolder)}", source);
        }
        var destDir = Path.GetDirectoryName(localFilePath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.Copy(source, localFilePath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<List<CloudFileInfo>> ListFilesAsync(string remoteFolder, CancellationToken ct = default)
    {
        var dir = Path.Combine(InboxFolder, remoteFolder);
        var result = new List<CloudFileInfo>();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var info = new FileInfo(file);
                result.Add(new CloudFileInfo
                {
                    Name = info.Name,
                    RemotePath = Path.Combine(remoteFolder, info.Name),
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTime,
                });
            }
        }
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string remoteFileName, string remoteFolder, CancellationToken ct = default)
    {
        var outboxFile = Path.Combine(OutboxFolder, remoteFolder, remoteFileName);
        if (File.Exists(outboxFile)) File.Delete(outboxFile);
        var inboxFile = Path.Combine(InboxFolder, remoteFolder, remoteFileName);
        if (File.Exists(inboxFile)) File.Delete(inboxFile);
        return Task.CompletedTask;
    }
}
