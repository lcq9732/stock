namespace StockPlatform.Logic.Services;

/// <summary>
/// 把各数据源给的成交量**归一化成「手」**（2026-09-10）。
///
/// ════ 为什么需要它：口径是按"数据源 × 板块"分岔的 ════
/// 2026-09-09/10 实测（拿 <c>amount / (volume × close)</c> 判：≈100 是手、≈1 是股）：
///
/// <code>
///                  主板/创业板/北交所      科创板 688·689
///   腾讯              手                    **股**
///   新浪            **股**                  **股**
///   东财              手                      手
/// </code>
///
/// 而两个 fetcher 原来都是把源给的数字**原样存库**，于是 <c>Bar.volume</c> 这一列里
/// 并存着两种单位：2026-09-08 那天全市场日线，主板 3203 只、创业板 1404 只、北交所 342 只是手，
/// 科创板 613 只是股。跨板块比较成交量时科创板差 100 倍。
///
/// ════ 这个错很难被发现 ════
/// 单只票内部的分析（放量比、量能排序、K线图）用的是**比值**，单位约掉了，怎么看都正常；
/// 只有**跨股票累加**才露馅——`BoardIndexSynthesizer` 把板块成分股的成交量加总，
/// 含科创板的板块因此虚高。这类错不会报警，只会让某几个板块的量能常年偏高而没人知道为什么。
///
/// ════ 为什么统一到「手」而不是「股」 ════
/// 现状 95% 的数据（4949 只）是手，改动量小 100 倍；东财那条路本来就是手；
/// 东财终端本地文件导入的也是手。代价：科创板可以买 1 股，除以 100 后会出现
/// <c>85,403.3 手</c> 这种非整数——列是 REAL 存得下，量能比较无影响。
///
/// ⚠ **判据走 <see cref="MarketClassifier"/>，不许在这里自己写代码前缀规则**——
/// 920 那次就是 provider 自写前缀，害得 342 只北交所票静默抓不到。
/// </summary>
public static class BarVolumeUnit
{
    /// <summary>数据源给成交量时用的口径，决定要不要换算。</summary>
    public enum Source
    {
        /// <summary>腾讯 newfqkline：主板给手，**科创板给股**。</summary>
        Tencent,

        /// <summary>新浪 getKLineData：**一律给股**（实测 600000 给 50,532,458 而当天真实是 505,325 手）。</summary>
        Sina,

        /// <summary>东财 push2his：所有板块都给手，不用换算。</summary>
        EastMoney,
    }

    /// <summary>一手多少股。A 股统一 100，科创板虽然可以按 1 股申报，但"手"的定义没变。</summary>
    public const double SharesPerLot = 100;

    /// <summary>
    /// 把源给的成交量换算成「手」。已经是手的原样返回。
    ///
    /// <paramref name="code"/> 传带前缀的指数符号（<c>sh000001</c>）时，
    /// <see cref="MarketClassifier"/> 判不出板块、返回 <c>Unknown</c>，于是不做换算——
    /// 这正是想要的：指数的"成交量"是成分股汇总，本来就不适用手/股这套判据。
    /// </summary>
    public static double ToLots(string code, double raw, Source source) => source switch
    {
        // 新浪一律是股
        Source.Sina => raw / SharesPerLot,

        // 腾讯只有科创板（含 689 那几只 CDR）是股
        Source.Tencent when MarketClassifier.Classify(code) == MarketBoard.ShanghaiStar => raw / SharesPerLot,

        _ => raw,
    };

    /// <summary>
    /// 一行历史数据的成交量看起来是不是「股」——给一次性回填用的判据。
    ///
    /// 用 <c>amount / (volume × close)</c> 而不是"按代码前缀一刀切"，是因为要修的不止科创板：
    /// 新浪回退（<c>TencentThenSinaBarFetcher</c>，2026-09-10 已拆除）每触发一次，就往那只票的
    /// 历史里掺一段股口径的行，哪只票哪一段全凭当时的网络抖动。实测掺进来的极少
    /// （600 开头 520 万行里只有 1 行），但按前缀改就漏掉它们了。
    ///
    /// 区间取 0.8~1.25 而不是精确 1：成交额和收盘价都有舍入，尤其腾讯的 amount 截断到百位。
    /// 跟"手"的 80~125 区间之间留着 1.25~80 这么宽的空隙，判错的可能性极低。
    /// </summary>
    public static bool LooksLikeShares(double volume, double amount, double close)
    {
        if (volume <= 0 || close <= 0 || amount <= 0) return false;
        var ratio = amount / (volume * close);
        return ratio is >= 0.8 and <= 1.25;
    }
}
