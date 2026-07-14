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
    /// <summary>要抓取的大盘指数清单。加新指数只需在这里加一行（用腾讯的带前缀符号）。</summary>
    public static readonly IReadOnlyList<(string Symbol, string Name)> All = new[]
    {
        ("sh000001", "上证指数"),
        ("sz399001", "深证成指"),
        ("sz399006", "创业板指"),
        ("sh000300", "沪深300"),
        ("sh000905", "中证500"),
        ("sh000688", "科创50"),
    };

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
