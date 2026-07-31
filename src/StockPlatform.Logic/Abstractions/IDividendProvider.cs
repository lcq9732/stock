using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取一只股票的分红送配历史（新浪 vISSUE_ShareBonus 分红派息页，较稳、非东财）——一次返回
/// 该股历年全部分红方案。逐股调用（全市场 5000+ 只，较慢）。</summary>
public interface IDividendProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某只股票（6位代码）的全部分红方案；无分红返回空列表。</summary>
    Task<List<DividendRow>> GetAllAsync(string code, CancellationToken ct = default);
}
