using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取某个指数的成分股权重（中证指数官网 closeweight.xls）——只有中证系指数有；非中证系
/// 指数会抓不到（抛异常，由调用方计入失败名单、可手工重试）。源偏不稳，失败率较高。</summary>
public interface IIndexWeightProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某个指数（6 位代码）的成分股权重；非中证系或当前不可达时抛异常。</summary>
    Task<List<IndexWeightRow>> GetWeightsAsync(string indexCode, CancellationToken ct = default);
}
