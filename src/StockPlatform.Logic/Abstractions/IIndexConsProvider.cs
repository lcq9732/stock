namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取某个指数的成分股名单（新浪指数成分接口，较稳）——返回 6 位成分股代码（已去前缀）。
/// 逐指数调用，供"拉取指数成分/权重"遍历全部指数。</summary>
public interface IIndexConsProvider
{
    event Action<string>? OnStatus;

    /// <summary>抓某个指数（6 位代码）的成分股——返回每只成分股的 6 位代码 + 纳入日期（可空）；该指数在
    /// 新浪无数据时返回空列表。</summary>
    Task<List<(string Code, DateTime? InDate)>> GetConsAsync(string indexCode, CancellationToken ct = default);
}
