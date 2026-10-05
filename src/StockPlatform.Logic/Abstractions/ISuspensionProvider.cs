using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 交易所官网的停复牌记录（2026-09-30）。一路接口一个实现（上交所股票、上交所基金、深交所），
/// 都按**自然月**取：一个月一个请求（上交所一页给 1000 条、超了自己翻页；深交所走 xlsx 导出不分页）。
/// </summary>
public interface ISuspensionProvider
{
    /// <summary><see cref="SuspensionSource"/> 的取值。</summary>
    string Source { get; }

    /// <summary>日志里怎么叫它（"上交所·股票"）。</summary>
    string Label { get; }

    /// <summary>从哪个月开始问（每月 1 日）。更早的一个请求都不发。</summary>
    DateOnly FirstMonth { get; }

    /// <summary>限流退避之类的状态播报。</summary>
    event Action<string>? OnStatus;

    /// <summary>
    /// 取 <paramref name="month"/> 所在自然月的记录。**返回空列表＝那个月官网确实一条都没有**；
    /// 请求失败、响应骨架不对、结果被截断都抛异常（当成空会让那个月永远被当成"问过了、没有"）。
    /// </summary>
    Task<List<SuspensionRow>> GetMonthAsync(DateOnly month, CancellationToken ct = default);
}
