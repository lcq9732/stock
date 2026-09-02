using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取一只股票的分红送配历史（新浪 vISSUE_ShareBonus 分红派息页，较稳、非东财）——一次返回
/// 该股历年全部分红方案。逐股调用（全市场 5000+ 只，较慢）。</summary>
public interface IDividendProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某只股票（6位代码）的全部分红方案；无分红返回空列表。</summary>
    Task<List<DividendRow>> GetAllAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// 分红 + 配股一起抓（2026-09-01 新增）。两者在源页面上是同一页的两张表，
    /// 分开调等于把请求数翻倍——全市场 5500 只、限流 3 并发/1 秒，多一倍就是多半小时。
    /// </summary>
    Task<DividendAndRights> GetAllWithRightsAsync(string code, CancellationToken ct = default);
}

/// <summary>一只股票的分红方案 + 配股方案，见 <see cref="IDividendProvider.GetAllWithRightsAsync"/>。</summary>
public readonly record struct DividendAndRights(List<DividendRow> Dividends, List<RightsIssueRow> Rights);
