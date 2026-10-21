namespace StockPlatform.Logic.Services;

/// <summary>
/// 把各数据源给的成交量**归一化成「股」**（ETF 是「份」）。2026-09-10 建，那时统一成「手」；
/// 2026-10-20 随入库单位统一改成股（见 doc/unit-normalization-inventory.md）。
///
/// ════ 为什么需要它：口径是按"数据源 × 板块"分岔的 ════
/// 2026-09-09/10 实测（拿 <c>amount / (volume × close)</c> 判：≈100 是手、≈1 是股）：
///
/// <code>
///                  主板/创业板/北交所/ETF/指数     科创板 688·689
///   腾讯              手                          **股**
///   新浪            **股**                        **股**
///   东财              手                            手
/// </code>
///
/// 两个 fetcher 原来都是把源给的数字**原样存库**，于是 <c>Bar.volume</c> 这一列里曾并存两种单位，
/// 跨板块比较成交量时科创板差 100 倍（单只票内部的分析用比值、单位约掉，所以很难被发现——
/// 只有 <c>BoardIndexSynthesizer</c> 这类**跨股票累加**才露馅）。
///
/// ════ 为什么 2026-10-20 改成「股」 ════
/// 用户定入库一律存基本单位（股、元、份），比例存百分数。"手"是交易单位不是计量单位：
/// 科创板可以买 1 股，存手会出现 317113.34 手这种小数；换算到元（成交额÷成交量＝均价）
/// 还要记得再除 100。存量已由【成交量改存股】一次性 ×100（见 <c>SqliteBarVolumeMigration</c>）。
///
/// ⚠ **判据走 <see cref="MarketClassifier"/>，不许在这里自己写代码前缀规则**——
/// 920 那次就是 provider 自写前缀，害得 342 只北交所票静默抓不到。
/// </summary>
public static class BarVolumeUnit
{
    /// <summary>数据源给成交量时用的口径，决定要不要换算。</summary>
    public enum Source
    {
        /// <summary>腾讯 newfqkline：主板/创业板/北交所/ETF/指数给**手**，科创板给股。</summary>
        Tencent,

        /// <summary>新浪 getKLineData：**一律给股**（实测 600000 给 50,532,458，当天真实是 505,325 手）。</summary>
        Sina,

        /// <summary>东财 push2his：所有板块都给手。</summary>
        EastMoney,
    }

    /// <summary>一手多少股（ETF 是份）。A 股统一 100，科创板虽然可以按 1 股申报，但"手"的定义没变。</summary>
    public const decimal SharesPerLot = 100m;

    /// <summary>
    /// 把源给的成交量换算成「股」（ETF「份」）。已经是股的原样返回。
    ///
    /// <paramref name="code"/> 传带前缀的符号（指数 <c>sh000001</c>、ETF <c>sh510300</c>）时
    /// <see cref="MarketClassifier"/> 判成 <c>Unknown</c>，按"不是科创板"处理：腾讯给它们的也是手
    /// （2026-10-20 核对：510300、159915 量额比 ≈100；上证指数成交额÷成交量≈16 元＝按手算的均价），一样 ×100。
    /// 换算走 <see cref="UnitScale"/>（decimal），不出浮点尾巴。
    /// </summary>
    public static double ToShares(string code, double raw, Source source) => source switch
    {
        // 新浪一律是股
        Source.Sina => raw,

        // 腾讯只有科创板（含 689 那几只 CDR）是股，其余是手
        Source.Tencent when MarketClassifier.Classify(code) == MarketBoard.ShanghaiStar => raw,

        _ => UnitScale.Apply(raw, SharesPerLot),
    };
}
