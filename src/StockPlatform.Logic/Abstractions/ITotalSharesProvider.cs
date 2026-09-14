namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 全市场总股本（2026-09-14 新增）。
///
/// ════ 为什么需要单独一个源 ════
/// 财报里的 <c>share_capital</c>（实收资本或股本）是**金额、单位元**，不是股数：
/// <code>share_capital(元) = 总股本(股) × 每股面值(元/股)</code>
/// A 股绝大多数票面值 1.00 元，两个数**恰好相等**——PE/PB 一直把这个巧合当定义用，
/// 于是面值不是 1 元的票全算错。2026-09-14 拿本接口跟库里逐只比对，5561 只里 **373 只对不上**：
///   · 269 只报表股本偏小（面值 &lt; 1 元）：紫金矿业 10 倍、中芯国际 35 倍、
///     诺诚健华 7.5 万倍（美元面值），PE 被**低估**成 1.3 / 3.9 / 0.0；
///   · 104 只报表股本偏大（H 股会计口径，股本科目含溢价）：中国移动报表 4703.59 亿元
///     vs 实际 216.91 亿股，PE 被**高估** 21 倍（348.8 而不是 16.1）。
/// 重算后 3923 只里 277 只（7.1%）的 PE 会变。
///
/// ⚠ 偏大那一类**没有本地判据能发现**：用「流通市值 ÷ 收盘价推出流通股数 &gt; 报表股本」
/// 只能抓出偏小的那些（流通股不可能多于总股本），对偏大的完全失明。所以这个数只能取，
/// 不能靠本地校验兜住。
///
/// ════ 口径 ════
/// 总股本 = A 股流通 + A 股限售 + H 股/B 股，跟报表 <c>share_capital</c> 的涵盖范围一致
/// （工商银行 3564 亿股 = A 股 2696 亿 + H 股 868 亿），所以换这个字段不改变 A+H 公司的行为：
/// PE 依然是「A 股价 × 总股本」，对 A+H 相当于拿 A 股价给 H 股也定价（市场通行口径）。
/// </summary>
public interface ITotalSharesProvider
{
    /// <summary>数据源名字，写进 FundamentalMetric.source。</summary>
    string SourceName { get; }

    event Action<string>? OnStatus;

    /// <summary>全市场一次取回。翻页/重试在实现里，调用方拿到的就是完整快照。</summary>
    Task<TotalSharesSnapshot> GetAllAsync(CancellationToken ct = default);
}

/// <summary>一次全市场总股本快照。</summary>
/// <param name="Rows">每只票一行。</param>
/// <param name="TradeDate">
/// 数据源自报的「值所属交易日」。⚠ **不要直接当 as_of_date 用**——它是数据源说的，
/// 落库前要拿本地交易日历校一遍（见 TotalSharesTask）。
/// </param>
public record TotalSharesSnapshot(IReadOnlyList<TotalSharesEntry> Rows, DateTime? TradeDate);

/// <param name="Code">6 位代码。</param>
/// <param name="TotalShares">总股本，单位**股**。</param>
public record TotalSharesEntry(string Code, double TotalShares);
