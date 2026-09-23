using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 交易所官方公布的每日 ETF 份额（2026-09-23）——【ETF换手率校正】的裁判来源。
/// 一个市场一个实现：上交所一天一个请求，深交所一个月一个请求（见 <see cref="Batch"/>）。
/// </summary>
public interface IEtfShareProvider
{
    /// <summary>"sh" / "sz"。</summary>
    string Market { get; }

    /// <summary>这个源最早有数据的交易日。更早的一个请求都不发。</summary>
    DateOnly FirstDay { get; }

    /// <summary>一次请求覆盖多长。</summary>
    EtfShareBatch Batch { get; }

    /// <summary>限流退避、重试之类的状态播报。</summary>
    event Action<string>? OnStatus;

    /// <summary>
    /// 取 [<paramref name="from"/>, <paramref name="to"/>] 这段的份额（<see cref="EtfShareBatch.Day"/>
    /// 的源只接受 from == to）。**返回空列表＝交易所那段确实没有数据**（开始有数据以前、或份额还没发布），
    /// 不是失败；请求失败、响应骨架不对都抛异常。
    /// </summary>
    Task<List<EtfShareRow>> GetAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}
