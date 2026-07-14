using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 通过 GitHub Releases 分发数据库(见 doc/data-platform-design.md 2026-07-14 变更记录)。仓库是公开的，
/// 所以**下载 release 资产匿名即可、不需要 token**(用户端 Analyzer 用)；**上传/建 release/删资产需要
/// PAT token**(发布端 Fetcher 用)。资产命名约定：全量 `baseline-YYYYMMDD.zip`、每日增量
/// `daily-YYYYMMDD.zip`，全部挂在同一个滚动 release(tag=<see cref="Tag"/>)下。为什么用 Releases 而不是
/// 提交进 git：单文件 856MB 远超 git 的 100MB 限制，而 release 资产单个可达 2GB、下载不计流量。
/// </summary>
public class GitHubReleaseClient
{
    public const string Owner = "lcq9732";
    public const string Repo = "stock";
    public const string Tag = "data"; // 承载所有数据资产的滚动 release

    private readonly HttpClient _http;
    private readonly string? _token;

    public GitHubReleaseClient(string? token = null, HttpClient? httpClient = null)
    {
        _token = token;
        _http = httpClient ?? new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromMinutes(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("stock-analyzer");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static HttpClientHandler CreateHandler()
    {
        var proxy = WebRequest.GetSystemWebProxy();
        proxy.Credentials = CredentialCache.DefaultCredentials;
        return new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
    }

    public record Asset(long Id, string Name, string DownloadUrl, long Size);

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        if (!string.IsNullOrWhiteSpace(_token)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return r;
    }

    /// <summary>列出数据 release 上的全部资产；release 还不存在(404)时返回空列表。</summary>
    public async Task<IReadOnlyList<Asset>> ListAssetsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{Tag}"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<Asset>();
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var list = new List<Asset>();
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                list.Add(new Asset(
                    a.GetProperty("id").GetInt64(),
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
        return list;
    }

    /// <summary>把一个资产流式下载到本地文件，progress 报告 0~1 的完成比例。</summary>
    public async Task DownloadAsync(Asset asset, string destPath, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, asset.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var output = File.Create(destPath);
        var buffer = new byte[1 << 20];
        long read = 0; int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
    }

    // ───── 以下为上传侧，需要 token(Fetcher 用) ─────

    /// <summary>拿到数据 release 的 id；不存在就创建。需要 token。</summary>
    public async Task<long> EnsureReleaseIdAsync(CancellationToken ct = default)
    {
        RequireToken();
        using (var get = await _http.SendAsync(Req(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{Tag}"), ct))
        {
            if (get.StatusCode == HttpStatusCode.OK)
            {
                using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct));
                return doc.RootElement.GetProperty("id").GetInt64();
            }
            if (get.StatusCode != HttpStatusCode.NotFound) get.EnsureSuccessStatusCode();
        }

        var createReq = Req(HttpMethod.Post, $"https://api.github.com/repos/{Owner}/{Repo}/releases");
        createReq.Content = new StringContent(
            JsonSerializer.Serialize(new { tag_name = Tag, name = "数据库", body = "股票数据库分发（全量 baseline-* + 每日增量 daily-*）" }),
            Encoding.UTF8, "application/json");
        using var create = await _http.SendAsync(createReq, ct);
        create.EnsureSuccessStatusCode();
        using var cdoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync(ct));
        return cdoc.RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>上传一个文件作为 release 资产；同名资产已存在会先删掉再传(用于替换 baseline)。需要 token。</summary>
    public async Task UploadAsync(long releaseId, string filePath, string assetName, IProgress<double>? progress, CancellationToken ct = default)
    {
        RequireToken();
        // 同名先删（GitHub 不允许重名资产）
        foreach (var a in await ListAssetsAsync(ct))
            if (a.Name == assetName)
            {
                using var del = await _http.SendAsync(Req(HttpMethod.Delete, $"https://api.github.com/repos/{Owner}/{Repo}/releases/assets/{a.Id}"), ct);
                del.EnsureSuccessStatusCode();
            }

        var url = $"https://uploads.github.com/repos/{Owner}/{Repo}/releases/{releaseId}/assets?name={Uri.EscapeDataString(assetName)}";
        var req = Req(HttpMethod.Post, url);
        await using var fs = File.OpenRead(filePath);
        var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentLength = fs.Length;
        req.Content = content;
        progress?.Report(0);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        progress?.Report(1);
    }

    private void RequireToken()
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new InvalidOperationException("上传到 GitHub 需要 PAT token，但没有配置（见 data/local/github_token.txt）");
    }
}
