using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

// 指数那两条线的离线模拟源（成分 / 权重）。流通市值那个在 MockMarketCapFetcher.cs。
// 说明书见 doc/offline-mock-design.md。

/// <summary>
/// **离线模拟**的指数成分源（2026-09-18）。说明书见 doc/offline-mock-design.md；
/// 安全边界跟 <see cref="MockBarFetcher"/> 一样（只在 DEBUG 构建里挂上去 + Debug 数据目录隔离）。
///
/// <see cref="EmptyFor"/> 里的指数返回空列表——**空不算失败**，那是真源的行为
/// （新浪对某些老指数本来就没有成分），任务侧靠它验"空结果不该进失败名单"。
/// </summary>
public sealed class MockIndexConsProvider : IIndexConsProvider
{
    /// <summary>每个指数造几只成分股。</summary>
    public const int MembersPerIndex = 5;

    public event Action<string>? OnStatus;

    /// <summary>这些指数返回空成分（不是失败）。</summary>
    public HashSet<string> EmptyFor { get; init; } = [];

    /// <summary>这些指数抛异常。</summary>
    public HashSet<string> Throws { get; init; } = [];

    public Task<List<(string Code, DateTime? InDate)>> GetConsAsync(
        string indexCode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Throws.Contains(indexCode))
            throw new InvalidOperationException($"[模拟源] 指数 {indexCode} 成分按约定抓取失败");

        if (EmptyFor.Contains(indexCode))
        {
            OnStatus?.Invoke($"[模拟源] 指数 {indexCode}：无成分，未发任何请求");
            return Task.FromResult(new List<(string, DateTime?)>());
        }

        var list = new List<(string, DateTime?)>();
        for (int i = 0; i < MembersPerIndex; i++)
            list.Add((i < MembersPerIndex / 2 ? $"6000{i:D2}" : $"0000{i:D2}",
                      DateTime.Today.AddYears(-1)));
        OnStatus?.Invoke($"[模拟源] 指数 {indexCode}：造了 {list.Count} 只成分，未发任何请求");
        return Task.FromResult(list);
    }
}

/// <summary>
/// **离线模拟**的指数权重源（2026-09-18）。
///
/// <see cref="NoFileFor"/> 里的指数返回空——那模拟的是**404（没有权重文件）**，
/// 真源上非中证系一律如此。任务侧靠它验"404 记进 IndexWeightMissing、不算失败"。
/// </summary>
public sealed class MockIndexWeightProvider : IIndexWeightProvider
{
    /// <summary>每个指数造几行权重。</summary>
    public const int RowsPerIndex = 5;

    public event Action<string>? OnStatus;

    /// <summary>这些指数返回空（＝404，没有权重文件）。默认：**代码不以 0 开头的都没有**——
    /// 粗略模拟"只有中证系才有文件"。</summary>
    public Func<string, bool>? NoFileFor { get; init; }

    /// <summary>这些指数抛异常。</summary>
    public HashSet<string> Throws { get; init; } = [];

    public Task<List<IndexWeightRow>> GetWeightsAsync(string indexCode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Throws.Contains(indexCode))
            throw new InvalidOperationException($"[模拟源] 指数 {indexCode} 权重按约定抓取失败");

        bool noFile = NoFileFor?.Invoke(indexCode) ?? !indexCode.StartsWith('0');
        if (noFile)
        {
            OnStatus?.Invoke($"[模拟源] 指数 {indexCode}：没有权重文件（模拟 404），未发任何请求");
            return Task.FromResult(new List<IndexWeightRow>());
        }

        // 基准日取上个月末——真源是月度更新的，这样"本地这一期还新鲜"那道筛子才验得到。
        var asOf = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-1);
        var rows = new List<IndexWeightRow>();
        for (int i = 0; i < RowsPerIndex; i++)
            rows.Add(new IndexWeightRow
            {
                IndexCode = indexCode,
                StockCode = i < RowsPerIndex / 2 ? $"6000{i:D2}" : $"0000{i:D2}",
                Weight = 100.0 / RowsPerIndex,
                AsOfDate = asOf,
                FetchedAt = DateTime.Now,
            });
        OnStatus?.Invoke($"[模拟源] 指数 {indexCode}：造了 {rows.Count} 行权重，未发任何请求");
        return Task.FromResult(rows);
    }
}
