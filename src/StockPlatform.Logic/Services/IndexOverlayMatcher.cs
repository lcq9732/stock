using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// "个股 K线叠加对应大盘"用的两件小事（2026-08-13新增，给行情详情窗口的叠加功能用）：
/// ① 一只票该跟哪个大盘指数比（<see cref="PickFor"/>）；② 把指数的收盘价按**交易日对齐**到个股的
/// K线序列上（<see cref="AlignToBars"/>）。
///
/// 为什么需要对齐：图表的横轴是"个股第几根K线"，而指数和个股的K线根数并不相同（个股停牌、上市晚、
/// 指数偶有休市差异），直接按下标一一对应会整体错位、越往前错得越多。所以按日期查表，个股某天缺
/// 指数数据时沿用上一个已知值（不留空洞，否则叠加线会断成好几段）。
/// </summary>
public static class IndexOverlayMatcher
{
    /// <summary>
    /// 按交易所给个股配对应的大盘指数：沪市→上证指数，深市→深证成指（用户 2026-08-13 明确要求
    /// "深圳的股票就与深圳大盘叠加，上海的就与上海的叠"，所以默认按**交易所**而不是细分板块——
    /// 创业板/科创板的票默认仍跟所在交易所的大盘比，想跟创业板指/科创50 比可以在界面上手工换）。
    ///
    /// 北交所返回 null：本地库里没有抓北证50（见 <see cref="MarketIndexCatalog"/> 的清单），没有
    /// 可比的指数，界面上会显示"该股所在市场没有可叠加的大盘指数"而不是硬凑一个别的市场的指数。
    /// </summary>
    public static (string Symbol, string Name)? PickFor(string code) => MarketClassifier.Classify(code) switch
    {
        MarketBoard.ShanghaiMain or MarketBoard.ShanghaiStar or MarketBoard.ShanghaiB => ("sh000001", "上证指数"),
        MarketBoard.ShenzhenMain or MarketBoard.ShenzhenChiNext or MarketBoard.ShenzhenB => ("sz399001", "深证成指"),
        _ => null,   // 北交所 / 无法识别的代码
    };

    /// <summary>
    /// 把指数收盘价按交易日对齐到个股的K线序列：返回的数组长度等于 <paramref name="stockBars"/>，
    /// 第 i 个元素是个股第 i 根K线那天的指数收盘价。个股某天没有对应指数数据时沿用上一个已知值；
    /// 个股开头几天早于指数最早数据（几乎不会发生）则为 NaN，画线时自然断开。
    /// </summary>
    public static double[] AlignToBars(IReadOnlyList<Bar> stockBars, IReadOnlyList<Bar> indexBars)
    {
        var byDate = new Dictionary<DateTime, double>(indexBars.Count);
        foreach (var b in indexBars) byDate[b.PeriodStart.Date] = b.Close;

        var result = new double[stockBars.Count];
        double lastKnown = double.NaN;
        for (int i = 0; i < stockBars.Count; i++)
        {
            if (byDate.TryGetValue(stockBars[i].PeriodStart.Date, out var close)) lastKnown = close;
            result[i] = lastKnown;
        }
        return result;
    }
}
