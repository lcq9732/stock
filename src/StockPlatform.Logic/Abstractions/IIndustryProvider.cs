using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取全市场股票的证监会行业分类（门类来自沪深两所官网、大类来自新浪，合并返回）。
/// 行业变动极少，属于"定期数据"，跟股东/财报一起季度跑一次即可。</summary>
public interface IIndustryProvider
{
    event Action<string>? OnStatus;

    /// <summary>这一份数据的来源标记，写进 <c>StockIndustry.source</c>，取值见 <see cref="IndustrySources"/>。</summary>
    string SourceName { get; }

    Task<List<StockIndustry>> GetAllAsync(CancellationToken ct = default);
}
