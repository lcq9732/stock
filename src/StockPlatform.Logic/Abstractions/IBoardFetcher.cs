using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>抓取板块行情（概念/行业）及其成分股。分两步是为了让编排层能先拿到板块榜（便宜、一两个
/// 请求）、再逐板块拉成分股（较多请求、可报进度）。</summary>
public interface IBoardFetcher
{
    event Action<string>? OnStatus;

    /// <summary>抓某一类板块的列表（不含成分股），已含涨跌幅/成交额/领涨股等榜单字段。</summary>
    Task<List<Board>> FetchBoardListAsync(BoardType type, CancellationToken ct = default);

    /// <summary>抓某个板块的成分股代码（去掉交易所前缀）。</summary>
    Task<List<string>> FetchMembersAsync(string boardCode, CancellationToken ct = default);
}
