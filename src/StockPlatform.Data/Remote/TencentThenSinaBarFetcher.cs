using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// K线来源回退链：先试腾讯，某只股票腾讯拿不到时才回退到新浪重试这一只（不是两边都发请求）。
/// 2026-07-08 加入——用户反馈实际使用中新浪的抓取稳定性不如腾讯，所以腾讯仍是主力，新浪只是
/// "腾讯拿不到时的备胎"，不是两个平级的选项。这个类只替换Fetcher数据源下拉框里"Tencent"这一项
/// 背后的实现，独立的"Sina"（纯新浪，无回退）和"EastMoney"选项不受影响，仍然是单一数据源。
/// </summary>
public class TencentThenSinaBarFetcher : IBarDataFetcher
{
    private readonly TencentBarFetcher _primary;
    private readonly SinaBarFetcher _fallback;

    public event Action<string>? OnStatus;

    public TencentThenSinaBarFetcher(TencentBarFetcher primary, SinaBarFetcher fallback)
    {
        _primary = primary;
        _fallback = fallback;
        _primary.OnStatus += msg => OnStatus?.Invoke(msg);
        _fallback.OnStatus += msg => OnStatus?.Invoke(msg);
    }

    public async Task<(string Name, List<Bar> Bars)> FetchAsync(string code, string granularity, DateTime? start, DateTime? end, CancellationToken ct = default)
    {
        try
        {
            return await _primary.FetchAsync(code, granularity, start, end, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 用户点了"停止"，不是腾讯本身失败，不应该触发新浪回退
        }
        catch (Exception ex)
        {
            OnStatus?.Invoke($"腾讯获取 {code} 失败（{ex.Message}），改用新浪重试");
            return await _fallback.FetchAsync(code, granularity, start, end, ct);
        }
    }
}
