namespace StockPlatform.Logic.Models;

/// <summary>
/// 龙虎榜的一条记录——同一只股票同一天可能因多个"上榜指标"(<see cref="Reason"/>)分别上榜，
/// 所以主键是 (trade_date, stock_code, reason)。
///
/// ════ 两个数据源，字段不是同一套（2026-09-09）════
/// <b>东财</b>（<c>EastMoneyLhbProvider</c>，现在的主源）给的是交易所原文的上榜原因，跟
/// <see cref="LhbSeat.Explanation"/> **同源**——两张表因此能按 (日期,代码,原因) 直接 join，
/// 这是从新浪切过来的主要理由。它额外给了一整组新浪没有的字段：龙虎榜买入/卖出/净额、
/// 占总成交比、换手率、流通市值、解读、以及上榜后 N 日涨跌幅。
///
/// <b>新浪</b>（<c>SinaLhbProvider</c>，留作后备）把上榜原因**归并成了粗类**，而
/// <see cref="Deviation"/> 仍跟着各自的原规则走，于是同一个 reason 下混着语义不同的值：
/// 2026-08-04 创业板那批 reason 全是"涨幅偏离值达7%的证券"，dev 却分别是当日涨跌幅(20.0)、
/// 两日累计(39.8)、多日累计(31.54)。**这份历史数据的 reason 与 deviation 是错配的**，
/// 别拿它当真值校验任何东西。
///
/// ════ 东财缺的两列靠本地派生 ════
/// 东财这张表没有"对应值"和"成交量"：<see cref="Deviation"/> 按上榜原因分类由本地日K+对应
/// 指数算出（见 <c>LhbDeviationDeriver</c>），<see cref="Volume"/> 直接取本地日K的成交量。
/// 算出来的值一律在 <see cref="DeviationSource"/> 里标明来路——派生值和源给的值必须分得清。
/// </summary>
public class LhbRow
{
    public DateTime TradeDate { get; set; }        // 交易日
    public string StockCode { get; set; } = "";    // 6 位股票代码
    public string StockName { get; set; } = "";
    public double ClosePrice { get; set; }         // 收盘价

    /// <summary>对应值（随上榜指标而定：换手率/偏离值/振幅…）。东财源不给，由本地派生；
    /// 派生不出来的（退市整理、融资买入占比这类本来就没有对应值的原因）为 null。</summary>
    public double? Deviation { get; set; }

    /// <summary>
    /// 成交量。**这一列已作废，全表为 NULL**（2026-09-10 起）。
    ///
    /// 经过：东财接口不给成交量。本想从本地日K补（÷100 换成万股），2026-09-08 的 59 只只对上
    /// 39 只——<c>Bar.volume</c> 的单位在科创板是"股"、其余板块是"手"，还有些行的量额本身就不全，
    /// 详见 <c>LhbDeviationDeriver.WhyVolumeIsNotDerived</c>。09-10 换源整段重抓时按天替换，
    /// 新浪时代那 259,147 行历史成交量也跟着没了。
    ///
    /// **为什么不从备份补回来**：东财源以后永远不写这一列，补完的局面是"2026-09-10 之前有、
    /// 之后每天都没有"。这种断层比整列空更危险——按成交量筛龙虎榜的分析在历史回测里跑得好好的，
    /// 上线后静静地筛不出任何东西。全表 NULL 是自洽的，谁都不会误用。
    /// 换源前的备份在 data/local/backup/Lhb-20260910-091818.sqlite，真要查旧值 ATTACH 即可。
    ///
    /// **要成交量就 join <c>Bar</c> 表**（等它的单位问题修好）。这一列留着不删，是因为删列要重建
    /// 26 万行的表，而留一个恒 NULL 的列成本为零、还让这段历史有个挂注释的地方。
    /// 量能信息也可以用 <see cref="Amount"/>（成交额）、<see cref="TurnoverRate"/>（换手率）
    /// 或 <see cref="BillboardBuyAmt"/> 那几列。
    /// </summary>
    public double? Volume { get; set; }

    /// <summary>成交额（万元）。东财的 ACCUM_AMOUNT 是**元**，写库前除以 1e4 对齐新浪历史口径
    /// ——2026-09-09 逐条比过，两源换算后比值精确为 1.000000。</summary>
    public double Amount { get; set; }

    public string Reason { get; set; } = "";       // 上榜指标；东财源下是交易所原文
    public DateTime FetchedAt { get; set; }

    // ════ 以下为东财源独有（新浪源下全为 null/空）════

    public double? ChangeRate { get; set; }        // 当日涨跌幅 %
    public double? TurnoverRate { get; set; }      // 当日换手率 %
    public double? FreeMarketCap { get; set; }     // 流通市值（元）
    public double? BillboardBuyAmt { get; set; }   // 龙虎榜买入额（元）
    public double? BillboardSellAmt { get; set; }  // 龙虎榜卖出额（元）
    public double? BillboardNetAmt { get; set; }   // 龙虎榜净买额（元）
    public double? BillboardDealAmt { get; set; }  // 龙虎榜成交额（元）
    public double? DealAmountRatio { get; set; }   // 龙虎榜成交额占总成交比 %
    public double? DealNetRatio { get; set; }      // 龙虎榜净买额占总成交比 %

    /// <summary>东财的"解读"，如"普通席位买入，成功率36.00%"。</summary>
    public string Explain { get; set; } = "";

    /// <summary>东财的榜单流水号，跟 <see cref="LhbSeat.TradeId"/> 同源。</summary>
    public string TradeId { get; set; } = "";

    public string ChangeType { get; set; } = "";   // 东财的异动类型编码
    public string TradeMarket { get; set; } = "";  // 上市板，如"上交所主板"

    /// <summary>
    /// 上榜后 N 日涨跌幅（%）——**滞后字段**：当天抓到的一律是 null，要等 N 天后东财才填上。
    /// 所以日常增量不能只抓当天，得往前回看一个月用新值覆盖（见
    /// <c>FetchOrchestrator</c> 里龙虎榜的回看窗口），否则这几列永远是空的。
    /// </summary>
    public double? D1Chg { get; set; }
    public double? D2Chg { get; set; }
    public double? D5Chg { get; set; }
    public double? D10Chg { get; set; }
    public double? D20Chg { get; set; }
    public double? D30Chg { get; set; }

    /// <summary>这一行是哪个源抓的：<c>"em"</c>／<c>"sina"</c>。</summary>
    public string Source { get; set; } = "";

    /// <summary><see cref="Deviation"/> 的来路：<c>"源"</c>（源直接给的）／
    /// <c>"派生"</c>（本地算的，规则已用主板数据验证过）／
    /// <c>"派生-未核验"</c>（创业板/科创板/北交所：规则照交易所写，但没有可信真值能验）／
    /// 空（算不出来）。</summary>
    public string DeviationSource { get; set; } = "";
}
