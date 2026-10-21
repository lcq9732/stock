using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 分红送配的**离线模拟**源（2026-10-21）——一个请求都不发，每只票造三年的现金分红。
/// 跟 <see cref="MockShareholderProvider"/> 同一个用途和同一道闸：只在 DEBUG 构建里、且
/// <c>OfflineMock</c> 开关打开时才会被装上。
///
/// ════ 为什么要它 ════
/// 【拉取分红送配】2026-10-21 挪进日更。分红的值取新浪、公告索引取东财，两条都是真网络；
/// 没有它，零联网的 Debug 实机验证跑到这一项就会发真请求。
///
/// ════ 标记代码 ════
///   · 代码含 <see cref="FailMarker"/> ⇒ 抛 <see cref="RateLimitedException"/>，验失败只报只数、下轮再取。
/// </summary>
public sealed class MockDividendProvider : IDividendProvider
{
    /// <summary>含这个片段的代码一律按限流失败。</summary>
    public const string FailMarker = "999";

    public event Action<string>? OnStatus;

    public async Task<List<DividendRow>> GetAllAsync(string code, CancellationToken ct = default)
        => (await GetAllWithRightsAsync(code, ct)).Dividends;

    public Task<DividendAndRights> GetAllWithRightsAsync(string code, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (code.Contains(FailMarker, StringComparison.Ordinal))
            throw new RateLimitedException($"[离线模拟] {code} 按 FailMarker 规则失败");

        var now = DateTime.Now;
        var rows = new List<DividendRow>();
        for (int k = 1; k <= 3; k++)
        {
            int year = DateTime.Today.Year - k;
            rows.Add(new DividendRow
            {
                Code = code,
                AnnounceDate = new DateTime(year + 1, 4, 20),
                DividendYuan = 2 + k,
                Progress = "实施",
                RecordDate = new DateTime(year + 1, 6, 10),
                ExDate = new DateTime(year + 1, 6, 11),
                FetchedAt = now,
            });
        }
        OnStatus?.Invoke($"[离线模拟] {code} 造了 {rows.Count} 条分红");
        return Task.FromResult(new DividendAndRights(rows, []));
    }
}

/// <summary>
/// 分红公告索引的**离线模拟**（2026-10-21）：代码尾号是 1 的票算"今天出了分红公告"，
/// 其余不在索引里——够验"索引命中排最前、其余按 90 天兜底"这条链，一个请求都不发。
/// </summary>
public sealed class MockDividendNoticeIndex : IDividendNoticeIndex
{
    public string SourceName => "离线模拟";

    public event Action<string>? OnStatus;

    public Task<IReadOnlyDictionary<string, DateTime>> GetRecentAsync(int lookbackDays, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        OnStatus?.Invoke($"[离线模拟] 分红公告索引：尾号 1 的票算今天有公告（回看 {lookbackDays} 天）");
        // 索引只给代码，名单由任务自己的全集决定；这里给一段常见代码区间里尾号为 1 的
        var hits = Enumerable.Range(600000, 1000).Select(i => i.ToString("000000"))
            .Concat(Enumerable.Range(1, 999).Select(i => i.ToString("000000")))
            .Where(c => c.EndsWith('1'))
            .ToDictionary(c => c, _ => DateTime.Today, StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, DateTime>>(hits);
    }
}
