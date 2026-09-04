namespace StockPlatform.Logic.Models;

/// <summary>
/// 分档资金流（东财 <c>push2his</c> 的个股历史资金流）。
///
/// 跟现有的 <c>NetInflow</c> 表是**同一件事的不同精度**，不是新东西：那张表 1077 万行，
/// 但每行只存了一个 <c>main_net_inflow</c>（主力净额合计）。东财这个接口给的是五档拆分——
/// 超大单/大单/中单/小单各自的净额和净占比，共 10 个维度。
///
/// 为什么值得单独存一张：判断资金性质要看结构而不是合计。同样是"主力净流入 1 亿"，
/// 超大单流入、小单流出，跟大单流入、超大单流出，含义完全不同——前者是机构在建仓，
/// 后者更像游资接力。合计数把这个信息抹平了。
///
/// ⚠ <b>接口只给最近约 120 个交易日</b>（<c>lmt=0</c> 也突破不了），所以拿不到长历史，
/// 只能靠定期抓取滚动累积。短期内做不了长周期回测，但"当下这波是谁在买"够用。
/// </summary>
public class NetInflowDetail
{
    public string Code { get; set; } = "";
    public DateTime TradeDate { get; set; }

    /// <summary>主力净额（元）——等于超大单+大单，跟现有 NetInflow 表那一列同口径。</summary>
    public double? MainNet { get; set; }
    public double? SuperNet { get; set; }   // 超大单
    public double? BigNet { get; set; }     // 大单
    public double? MidNet { get; set; }     // 中单
    public double? SmallNet { get; set; }   // 小单

    /// <summary>各档净额占当日成交额的比例（%）。</summary>
    public double? MainRatio { get; set; }
    public double? SuperRatio { get; set; }
    public double? BigRatio { get; set; }
    public double? MidRatio { get; set; }
    public double? SmallRatio { get; set; }

    /// <summary>接口顺带给的当日收盘价与涨跌幅（%）。</summary>
    public double? ClosePrice { get; set; }
    public double? ChangeRate { get; set; }

    public DateTime FetchedAt { get; set; }
}
