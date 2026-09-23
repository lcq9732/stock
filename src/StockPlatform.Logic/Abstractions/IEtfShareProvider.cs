using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 上交所官方公布的每日 ETF 份额（2026-09-23）——【ETF换手率校正】的裁判来源。
/// 一天一个请求，拿到当天全部沪市 ETF。
/// </summary>
public interface IEtfShareProvider
{
    /// <summary>限流退避、重试之类的状态播报。</summary>
    event Action<string>? OnStatus;

    /// <summary>
    /// 取一天。**返回空列表＝上交所那天确实没有数据**（2012-01-04 以前、或当天份额还没发布），
    /// 不是失败；请求失败、响应骨架不对都抛异常。
    /// </summary>
    Task<List<EtfShareRow>> GetDayAsync(DateOnly day, CancellationToken ct = default);
}
