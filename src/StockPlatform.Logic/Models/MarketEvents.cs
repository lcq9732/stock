namespace StockPlatform.Logic.Models;

/// <summary>
/// 大宗交易（东财 <c>RPT_DATA_BLOCKTRADE</c>）。本地此前没有。
///
/// 值钱的是 <see cref="BuyerName"/>/<see cref="SellerName"/>（买卖双方营业部）和
/// <see cref="PremiumRatio"/>（折溢价率）：大幅折价出货通常是股东在减持套现，
/// 溢价接盘则可能是产业资本或有备而来的机构。这跟龙虎榜席位是互补的两块筹码信息。
/// </summary>
public class BlockTrade
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime TradeDate { get; set; }
    /// <summary>当日第几笔——同一股同一天可能成交多笔，进主键。</summary>
    public int DailyRank { get; set; }

    public double? DealPrice { get; set; }
    public double? DealVolume { get; set; }
    public double? DealAmount { get; set; }
    /// <summary>折溢价率（%），负数=折价。</summary>
    public double? PremiumRatio { get; set; }
    public double? ClosePrice { get; set; }
    public double? ChangeRate { get; set; }
    public double? TurnoverRate { get; set; }

    public string BuyerName { get; set; } = "";
    public string SellerName { get; set; } = "";

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 机构调研（东财 <c>RPT_ORG_SURVEYNEW</c>）。本地此前没有。
///
/// 用途是看资金关注度的迁移：一家公司突然被几十家机构集中调研，通常早于股价异动；
/// <see cref="Investigators"/> 里是参与调研的机构名单，能看出是谁在看。
/// 属于软信号——它证明"有人在关注"，不证明"基本面变好"。
/// </summary>
public class OrgSurvey
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>公告日 —— 增量按它切片。</summary>
    public DateTime NoticeDate { get; set; }
    /// <summary>接待起始日（同一次调研可能跨天）。</summary>
    public DateTime? ReceiveStartDate { get; set; }
    public DateTime? ReceiveEndDate { get; set; }

    /// <summary>
    /// 参与调研的机构名（一条记录一家），取自接口的 <c>RECEIVE_OBJECT</c>。
    ///
    /// ⚠ 别用接口里的 <c>ORG_NAME</c>——那是**被调研的上市公司全名**，同一次调研的每一行都一样。
    /// 早先误取了它做主键，实测一批 10280 行有 95.1% 主键重复、只剩 506 行（靠仓储的批内
    /// 去重自检当场发现的，否则这份数据会静默丢掉 95%）。
    /// </summary>
    public string OrgName { get; set; } = "";
    /// <summary>参与机构的代码（可能为空，所以主键用名称不用它）。</summary>
    public string ObjectCode { get; set; } = "";
    /// <summary>机构类型：基金公司/证券公司/保险公司…</summary>
    public string OrgType { get; set; } = "";
    /// <summary>接待方式：特定对象调研/电话会议/业绩说明会…</summary>
    public string ReceiveWay { get; set; } = "";
    public string ReceivePlace { get; set; } = "";
    /// <summary>参与人员名单（机构侧）。</summary>
    public string Investigators { get; set; } = "";
    /// <summary>公司接待人员。</summary>
    public string Receptionist { get; set; } = "";
    /// <summary>
    /// 接口的 <c>NUM</c>——同一机构同一天参与同一公司的**多次**调研时靠它区分，所以**进主键**。
    /// 实测中信证券 2026-09-02 对 600177 就有 3 条记录（NUM=1/7/22）；不带它的话这类行会互相
    /// 覆盖（整批约 0.6%）。语义上更像"该次调研活动的记录序号"，不是机构家数。
    /// </summary>
    public int SurveyNo { get; set; }
    /// <summary>本次调研的机构总家数（接口的 <c>SUM</c>）。</summary>
    public int? OrgTotal { get; set; }

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 限售解禁（东财 <c>RPT_LIFT_STAGE</c>）。本地此前没有。
///
/// ⚠ 这张表**含未来的解禁计划**（实测样例里有 2035 年的），所以它不是"历史事件表"而是
/// "日程表"——不能按"抓到今天为止"做增量，每次都要全量重取。好在只有 3 万行、63 页。
///
/// 用途：解禁是次新股最明确的时间节点，尤其是首发原股东限售股解禁前后。
/// </summary>
public class ShareLift
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>解禁日（可能在未来）。</summary>
    public DateTime FreeDate { get; set; }
    /// <summary>限售股types：首发原股东限售股份/定向增发机构配售股份…同一天可能有多类，进主键。</summary>
    public string ShareType { get; set; } = "";

    /// <summary>本次解禁股数（万股）。</summary>
    public double? LiftShares { get; set; }
    /// <summary>解禁市值（万元）。</summary>
    public double? LiftMarketCap { get; set; }
    /// <summary>占流通股比例 / 占总股本比例（%）。</summary>
    public double? FreeRatio { get; set; }
    public double? TotalRatio { get; set; }
    /// <summary>涉及股东户数。</summary>
    public int? HolderCount { get; set; }

    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 股东增减持（东财 <c>RPT_SHARE_HOLDER_INCREASE</c>）。本地此前没有。
///
/// 跟 <c>TopShareholder</c>（十大股东名单，季度快照）互补：那张表告诉你"季末谁持有多少"，
/// 这张告诉你"期间谁在买卖、买卖了多少、什么价位"。重要股东的增减持是明确的内部人信号。
/// </summary>
public class HolderChange
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>公告日 —— 增量按它切片。</summary>
    public DateTime NoticeDate { get; set; }
    public string HolderName { get; set; } = "";

    /// <summary>变动方向："增持"/"减持"。</summary>
    public string Direction { get; set; } = "";
    /// <summary>变动股数（万股），带符号。</summary>
    public double? ChangeShares { get; set; }
    /// <summary>变动比例（%）。</summary>
    public double? ChangeRatio { get; set; }
    /// <summary>变动后持股数（万股）与持股比例（%）。</summary>
    public double? AfterShares { get; set; }
    public double? AfterRatio { get; set; }
    /// <summary>变动区间起止。</summary>
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    /// <summary>交易均价（元）。</summary>
    public double? AveragePrice { get; set; }

    public DateTime FetchedAt { get; set; }
}
