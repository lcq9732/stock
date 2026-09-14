namespace StockPlatform.Logic.Models;

/// <summary>Common metric_key values. New keys can be added without a schema change.</summary>
public static class MetricKeys
{
    public const string Revenue = "revenue";
    public const string NetProfit = "net_profit";
    public const string Roe = "roe";
    public const string Eps = "eps";
    public const string Bvps = "bvps";
    public const string Pe = "pe";
    public const string Pb = "pb";

    /// <summary>流通市值（不含限售股），单位固定是"元"（不是"万元"/"亿元"）——跟 Bar 里价格字段
    /// 一样用最小单位存，换算成"亿"只在展示/比较阈值时做（见
    /// MidCapPullbackAnalysisEngine：80亿=8_000_000_000）。
    ///
    /// **这个 key 已经有真实数据了**（早期注释说"从来是空的、是预留写入点"，那是 2026-07-09 之前
    /// 的状态，已不成立）。写入方是 FetchOrchestrator.FetchMarketCapAsync，"拉取全部"和"拉取当天"
    /// 都会跑一遍，用 Upsert（主键 code+metric_key+as_of_date）。2026-08-04 对
    /// publish/data/local/current.sqlite 实测：80,515 行 / 5,539 只股票 / 15 个交易日，覆盖
    /// 2026-07-09 ~ 2026-08-03。单位"元"已交叉验证：600036 的 value ÷ 同日日线收盘 = 恒定的
    /// 20,628,944,429（招商银行A股流通股本），量级和单位都对得上。
    ///
    /// 两个读这张表时容易踩的坑：
    /// 1. **source="EastMoney" 的行名不副实**——2026-08-04 之前写入处硬编码的就是这个字符串，但实际
    ///    产出这些行的是 SinaListMarketCapFetcher（新浪股票列表顺带的 nmc 字段，万元×10000→元）；
    ///    EastMoney/Tencent 那两个 IMarketCapFetcher 实现从来没有被任何 composition root 构造过。
    ///    现在改成写实际 fetcher 的类名，所以 source="EastMoney" 一律是旧行、来源其实是新浪。
    ///    这一列只写不读（没有任何代码依赖它），纯记录用。
    /// 2. **as_of_date 记的是"这个值属于哪个交易日"，不是"哪天跑的抓取"**——但这是 2026-08-04 才改对的
    ///    （见 FetchOrchestrator.ResolveMarketCapAsOfDateAsync）。之前一律写 DateTime.Today，于是
    ///    盘前/周末/节假日抓到的值（那时接口给的是**上一个交易日收盘**算出来的市值）会被记到抓取当天
    ///    名下。**改动之前写进库的那批行仍是旧语义**：2026-07-09 ~ 2026-08-03 之间已核对出 3 处错位
    ///    （2026-07-11、2026-08-01 两个周六的行其实是 07-10、07-31 的收盘值；2026-07-23 08:02 那行
    ///    是 07-22 的收盘值），已按交易日归位/去重。再往前没有数据。
    ///    另外注意值不一定是**收盘**价算的：盘中跑到的就是那一刻的实时快照，日期对但值偏；同一交易日
    ///    收盘后再跑一次会覆盖成收盘值（Upsert 主键含 as_of_date）。</summary>
    public const string CirculatingMarketCap = "circulating_market_cap";

    /// <summary>
    /// 总股本，单位**股**（2026-09-14 新增）。写入方是 TotalSharesTask，日更，
    /// 来源是东财条件选股接口的 <c>TOTAL_SHARES</c>。
    ///
    /// ⚠ **不要用财报的 <c>share_capital</c> 代替它**——那个是「实收资本(或股本)」，
    /// 单位是**元**，等于 总股本 × 每股面值。A 股绝大多数票面值 1.00 元，两个数恰好相等，
    /// 于是这个巧合被当成了定义，面值不是 1 元的票就全错。2026-09-14 逐只比对，
    /// 5561 只里 373 只对不上，两个方向都有：
    ///   · 报表偏小（面值 &lt; 1 元）269 只——紫金矿业 26.59 亿 vs 265.91 亿股（面值 0.1）、
    ///     中芯国际差 35 倍、诺诚健华（美元面值）差 7.5 万倍；
    ///   · 报表偏大（H 股会计口径，股本科目含溢价）104 只——中国移动 4703.59 亿元
    ///     vs 216.91 亿股。
    /// 换成这个 key 之后重算，3923 只里 277 只（7.1%）的 PE 变了：中国移动 348.8 → 16.1、
    /// 分众传媒 0.5 → 20.1、诺诚健华 0.0 → 50.7。
    ///
    /// ⚠ **拿不到就是没有这一行**，不写 0、不写占位——消费方查不到时回退 <c>share_capital</c>
    /// 并在界面上标识（见 FinancialAnalyzer）。写 0 会算出「市值 0、PE 0」。
    ///
    /// 口径：总股本 = A 股流通 + A 股限售 + H 股/B 股，跟 <c>share_capital</c> 涵盖范围一致
    /// （工行 3564 亿股 = A 股 2696 亿 + H 股 868 亿）。
    ///
    /// as_of_date 跟 <see cref="CirculatingMarketCap"/> 同样是「值所属交易日」，
    /// 落库前用本地交易日历校过（接口自报的日期不直接信）。
    /// </summary>
    public const string TotalShares = "total_shares";
}

/// <summary>One row of the key-value FundamentalMetric table (see doc/data-platform-design.md section 4).</summary>
public class FundamentalMetric
{
    public string Code { get; set; } = "";
    public string MetricKey { get; set; } = "";
    public DateTime AsOfDate { get; set; }
    public double Value { get; set; }
    public string Source { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}
