using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**数据源（2026-09-13）——一个请求都不发，按交易日历凭空造出请求区间里的K线。
///
/// ════ 为什么要它 ════
/// 【重新拉取失败】这条链（分派 → 各任务补自己的待办 → 复查 → 落账 → 收敛进白名单）
/// 真跑一轮是几千个网络请求、几个小时，而且会跟正在抓数据的正式实例抢数据源配额。
/// 单元测试又只能覆盖到纯函数，覆盖不了"界面点下去之后整条链对不对"
/// （2026-09-08 的教训：单测测不出调用环路和 UI 链）。
/// 选这个源，就能在 Debug 实例里把整条链真跑一遍，只是网络那层换成了本地构造。
///
/// ════ 安全边界 ════
/// ⚠ 它写进库的是**假数据**。两道闸：
///   ① 只在 DEBUG 构建里注册进数据源列表（见 Fetcher 的 App.xaml.cs），Release 的下拉里没有它；
///   ② Debug 实例的数据目录在 bin\Debug\...\data 下，跟正式实例的 publish\data 天然隔离。
/// 两道都别拆。
///
/// ════ 造出来的数据长什么样 ════
/// 请求区间里**每个工作日**一根，OHLC 全是 <see cref="Price"/>、量额换手给非零常数
/// （零值会被值体检的"NULL/零值"判据报出来，那就不是在测重取链了）。
/// 抓取时刻记成当天 20:00——盘后，免得被"盘中固化"判据盯上（见 project_intraday_bar_confirmation）。
/// 周末不返回：交易日历是按上证指数的真实日线算的，造出周末的行反而会让复查对不上。
/// </summary>
public sealed class MockBarFetcher : IBarDataFetcher
{
    /// <summary>造出来的价格。取个一眼就知道是假数据的整数。</summary>
    public const double Price = 11.11;

    /// <summary>模拟失败：代码里含这个片段的一律抛异常，用来验证失败名单那条路。</summary>
    public const string FailMarker = "999";

    public bool SupportsHfq => true;   // 三个口径的待办都要能走到

    public event Action<string>? OnStatus;

    public Task<(string Name, List<Bar> Bars)> FetchAsync(
        string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (code.Contains(FailMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"[模拟源] {code} 按约定抓取失败（代码里含 {FailMarker}）");

        var from = (start ?? DateTime.Today.AddDays(-30)).Date;
        var to = (end ?? DateTime.Today).Date;
        var bars = new List<Bar>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            bars.Add(new Bar
            {
                Code = code,
                Granularity = granularity,
                PeriodStart = d,
                Open = Price, Close = Price, High = Price, Low = Price,
                Volume = 10000, Amount = 10000 * Price, Turnover = 1.5,
                FetchedAt = d.AddHours(20),
            });
        }

        OnStatus?.Invoke($"[模拟源] {code}/{granularity} 造了 {bars.Count} 根（{from:MM-dd}~{to:MM-dd}），未发任何请求");
        return Task.FromResult(("Mock", bars));
    }
}

/// <summary>
/// 跟 <see cref="MockBarFetcher"/> 配套的股票名册：直接读**本地库里已有的**名册，不联网。
/// 造一份假名册会把假代码写进 StockMeta，之后每一轮抓取都会跟着跑它们。
/// </summary>
public sealed class MockStockListProvider : IStockListProvider
{
    private readonly Func<IReadOnlyList<StockListEntry>> _local;

    public MockStockListProvider(Func<IReadOnlyList<StockListEntry>> local) => _local = local;

    public Task<List<StockListEntry>> GetAllStocksAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        var list = _local().ToList();
        progress?.Report($"[模拟源] 名册直接取本地已有的 {list.Count} 只，未发任何请求");
        return Task.FromResult(list);
    }
}
