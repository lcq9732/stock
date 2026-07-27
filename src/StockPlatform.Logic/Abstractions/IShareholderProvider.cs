using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取一只股票的股东数据（新浪股本股东页，较稳、非东财）——一次返回该股历年各报告期的股东
/// 户数 + 十大股东 + 十大流通股东。逐股调用（全市场 5000+ 只，较慢）。</summary>
public interface IShareholderProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某只股票（6 位代码）的全部股东数据；无数据返回空聚合。</summary>
    Task<ShareholderData> GetAsync(string code, CancellationToken ct = default);
}
