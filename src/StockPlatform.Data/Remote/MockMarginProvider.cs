using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// **离线模拟**的融资余额源（2026-09-18）——一个请求都不发，按请求的交易日凭空造行。
/// 照 <see cref="MockBarFetcher"/> 抽的，理由也一样：
///
/// 【融资余额】迁到新任务框架之后，要验的是"界面点下去之后整条链对不对"——四道闸、
/// 最近 5 个交易日无条件重抓、空日名单的定案与撤销、残缺日待办的转交与复查。
/// 单测覆盖不了调用环路和 UI 链（2026-09-08 的教训），而真跑一轮要几十个请求、
/// 还会跟正在抓数据的正式实例抢交易所那边的配额。选这个源就能把整条链真跑一遍，
/// 只是网络那层换成了本地构造。
///
/// ════ 安全边界（跟 MockBarFetcher 同款，两道闸都别拆）════
/// ① 只在 DEBUG 构建里注册（见 Fetcher 的 App.xaml.cs），Release 里根本不存在这个选项；
/// ② Debug 实例的数据目录在 bin\Debug\...\data 下，跟正式实例的 publish\data 天然隔离。
///
/// ════ 造出来的数据长什么样 ════
/// 每个工作日 <see cref="RowsPerDay"/> 行，代码用 6000xx / 0000xx 各一半——两市都有，
/// 免得被日频体检的"某个交易所整天没有"判据报成残缺日（那就不是在测这条链了）。
/// 融资余额给一眼就知道是假数据的整数。
///
/// <see cref="EmptyFrom"/> 之后的日子返回 0 行，用来验"空日名单"那条：
/// 够旧的空才定案、太新的不定案（两所 T+1）。默认不设，即所有工作日都有数据。
/// </summary>
public sealed class MockMarginProvider : IMarginProvider
{
    /// <summary>每天造几行。够两市各一半即可，不用真的 4000 多只。</summary>
    public const int RowsPerDay = 10;

    /// <summary>一眼就知道是假数据的融资余额。</summary>
    public const double Balance = 111_111;

    public event Action<string>? OnStatus;

    /// <summary>这个源"最早有数据"的那天。默认跟真源一样是两融开市首日。</summary>
    public DateOnly EarliestAvailable { get; init; } = new(2010, 3, 31);

    /// <summary>
    /// 这天（含）起返回 0 行。**默认是今天**——交易所是 T+1，当天的数据本来就还没发布，
    /// 模拟源照着来，"今天的 0 行不该定案"那条判据才验得到（见 <c>DailyNoDataGate</c>）。
    /// </summary>
    public DateOnly? EmptyFrom { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>这些天抛异常——用来验"单天失败不拖垮整轮"和失败名单。</summary>
    public HashSet<DateOnly> Throws { get; init; } = [];

    public Task<List<MarginDetailRow>> GetDetailAsync(DateOnly date, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (Throws.Contains(date))
            throw new InvalidOperationException($"[模拟源] {date:yyyy-MM-dd} 按约定抓取失败");

        // 周末不返回：交易所本来就没有，造出来反而会让体检和复查对不上。
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || date < EarliestAvailable
            || (EmptyFrom is { } e && date >= e))
        {
            OnStatus?.Invoke($"[模拟源] 融资余额 {date:yyyy-MM-dd}：0 行，未发任何请求");
            return Task.FromResult(new List<MarginDetailRow>());
        }

        var rows = new List<MarginDetailRow>(RowsPerDay);
        for (int i = 0; i < RowsPerDay; i++)
        {
            // 前一半沪市、后一半深市——体检的"某市整天没有"判据要两市都见得到。
            string code = i < RowsPerDay / 2 ? $"6000{i:D2}" : $"0000{i:D2}";
            rows.Add(new MarginDetailRow
            {
                TradeDate = date.ToDateTime(TimeOnly.MinValue),
                Code = code,
                MarginBalance = Balance + i,
            });
        }
        OnStatus?.Invoke($"[模拟源] 融资余额 {date:yyyy-MM-dd}：造了 {rows.Count} 行，未发任何请求");
        return Task.FromResult(rows);
    }
}
