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

    /// <summary>
    /// 成分股「抓过多久算还新鲜」——这么久之内抓过的板块，本轮跳过。
    ///
    /// 为什么挂在**通道**上而不是做成一个全局常量（2026-09-06 改）：这个值的本质是
    /// "重抓一遍要付多大代价"，而各通道的代价差着几个数量级——走 push2 的那几条跑一轮
    /// 三小时起、还随时被限流打断，所以宁可让数据陈一周也不重抓；而读本地文件的通道
    /// 一次拿全量、耗时以毫秒计、一个请求都不发，没有任何节流的理由。
    /// 写成一个常量的话，这两种情况只能共用一个折中值，对谁都不合适。
    ///
    /// **小于等于 0 表示完全不节流、每轮全量覆盖**：调用方会把"新鲜"的时间线推到
    /// <see cref="DateTime.MaxValue"/>，于是没有任何记录算得上新鲜。读本地文件的通道就该这样——
    /// 重来一遍几乎不要钱，而留着旧值反而会把**别的通道留下的半截数据**一直保下去。
    /// </summary>
    TimeSpan MemberFreshFor { get; }
}
