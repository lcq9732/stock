namespace StockPlatform.Logic.Services;

/// <summary>
/// 大盘指数目录（2026-07-13新增）——供 FetchOrchestrator 在"拉取全部"/"拉取当天"时顺带抓取
/// 指数日K，给分析/回测做大盘环境过滤用（指数MA20、两市成交额热度等）。
///
/// 指数在 Bar 表里的 code 用带厂商前缀的8位符号（"sh000001"），不是6位数字——上证指数的
/// 000001 和平安银行(sz000001)的 000001 是两个不同标的，6位裸代码会在 Bar 表主键上撞车；
/// 带前缀存储天然区分，且各选股Tab的扫描全集（SqliteBarRepository.GetAllCodes，只认6位纯
/// 数字代码）不会把指数当成个股捞进去。腾讯/新浪的K线接口本来就直接接受这种带前缀的符号
/// （newfqkline 对指数返回 "day" 节点而不是 "qfqday"，行结构与个股一致，[7]换手率/[8]成交额
/// 照常有值——2026-07-13 对 sh000001/sz399001 实测确认），东方财富按 sh→"1." / sz→"0." 映射
/// 成 secid 即可。指数没有除权除息，qfq参数对它是无害的空操作。
///
/// 故意不写进 StockMeta 表——那张表是"全市场个股列表"，各分析Tab用它补股票名称、QueryTab用
/// 它当查询全集，混入指数会让指数出现在选股结果里。指数的名称就靠这里的常量目录。
/// </summary>
public static class MarketIndexCatalog
{
    /// <summary>上证指数。除了作为要抓的指数之一，还被用作**"最新交易日"的锚**——指数不会停牌、
    /// 不会退市，它最新一根日线的日期就是"最近一个已收盘的交易日"，这样判断交易日就不需要在本地
    /// 维护一份A股节假日日历（见 FetchOrchestrator.ResolveMarketCapAsOfDateAsync）。</summary>
    public const string ShanghaiCompositeSymbol = "sh000001";

    /// <summary>要抓取的大盘指数清单。加新指数只需在这里加一行（用腾讯的带前缀符号）。</summary>
    public static readonly IReadOnlyList<(string Symbol, string Name)> All = new[]
    {
        (ShanghaiCompositeSymbol, "上证指数"),
        ("sz399001", "深证成指"),
        ("sz399006", "创业板指"),
        ("sh000300", "沪深300"),
        ("sh000905", "中证500"),
        ("sh000688", "科创50"),
        // ↓ 2026-09-09 追加，专为龙虎榜"涨跌幅偏离值"的派生（见 DeviationBenchmarkFor）。
        // 它们不是给人看的大盘指数，是**算偏离值的分母**——交易所算偏离值用的就是这几条，
        // 少抓一条，对应板块的偏离值就永远算不出来。
        ("sz399106", "深证综指"),
        ("sz399102", "创业板综"),
        ("bj899050", "北证50"),
    };

    /// <summary>
    /// 算"涨跌幅偏离值"时，一只票该拿哪条指数当基准——偏离值 ＝ 个股涨跌幅 − 该指数涨跌幅。
    ///
    /// ⚠ 跟 <see cref="IndexOverlayMatcher.PickFor"/> **不是一回事，别合并**：那个是界面上
    /// "个股K线叠加大盘"用的，用户明确要求按**交易所**配（深市的票一律叠深证成指）；这里是
    /// 交易所计算规则里写死的基准，深市主板用的是**深证综指 399106**，不是深证成指。
    /// 两者拿同一批数据实测过：深主板配 399106 命中率 70.6%，配 399001 只有 2.3%。
    ///
    /// 验证情况（2026-09-09，用 2026-06 以来的主板历史反推，|派生−真值| &lt; 0.02 算命中）：
    ///   · 沪主板 × 上证综指   n=670  中位 0.0032  命中 88.2%  ✅
    ///   · 深主板 × 深证综指   n=653  中位 0.0082  命中 70.6%  ✅
    ///   · 创业板 / 科创板 / 北交所——**没验过**，照交易所规则配的。不是算不出来，是没有可信
    ///     真值：唯一的历史对照是新浪那份，而它对这三个板块的对应值本身就是错配的。
    ///     所以这三类算出来的值一律标 <c>派生-未核验</c>。
    ///
    /// 返回 null＝识别不出板块，偏离值就留空，不硬凑一条别的指数。
    /// </summary>
    public static string? DeviationBenchmarkFor(string code) => MarketClassifier.Classify(code) switch
    {
        MarketBoard.ShanghaiMain or MarketBoard.ShanghaiB => ShanghaiCompositeSymbol,
        MarketBoard.ShanghaiStar => "sh000688",
        MarketBoard.ShenzhenMain or MarketBoard.ShenzhenB => "sz399106",
        MarketBoard.ShenzhenChiNext => "sz399102",
        MarketBoard.Beijing => "bj899050",
        _ => null,
    };

    /// <summary>这只票的偏离值派生规则**有没有拿真值验证过**——只有沪深主板验过，
    /// 见 <see cref="DeviationBenchmarkFor"/> 里的实测数据。决定写库时标"派生"还是"派生-未核验"。</summary>
    public static bool IsDeviationRuleVerified(string code) => MarketClassifier.Classify(code)
        is MarketBoard.ShanghaiMain or MarketBoard.ShanghaiB
        or MarketBoard.ShenzhenMain or MarketBoard.ShenzhenB;

    /// <summary>是否是带厂商前缀的完整符号（"sh000001"这种）——各K线抓取器用它区分
    /// "已经是指数符号，原样使用"和"6位个股代码，需要按板块推前缀"两种输入。</summary>
    public static bool IsPrefixedSymbol(string code)
    {
        code = code.Trim();
        return code.Length == 8
            && (code.StartsWith("sh") || code.StartsWith("sz") || code.StartsWith("bj"))
            && code[2..].All(char.IsDigit);
    }
}
