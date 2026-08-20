namespace StockPlatform.Data.Orchestration;

/// <summary>
/// "还有多少东西等着重试"的分类汇总（2026-08-19新增，取代原来那个把各类失败数直接相加的
/// <c>GetFailedCodeCount</c>）。
///
/// 拆开的原因是**各类失败的粒度根本不一样**：K线/资金净流入/股东/分红是逐只抓的，失败几只就是
/// 几只；而流通市值是一次请求拿回全市场的整轮扫描，接口挂掉时会保守地把整批代码都记进失败名单
/// （见 FetchOrchestrator.FetchMarketCapAsync）。两者相加得到的"5547 支失败"极具误导性——它读
/// 起来像 5547 只股票的数据丢了，实际是"1 次市值快照没取到 + 3 只资金流失败"。
///
/// 所以这里保留各类的原始数字，由 <see cref="Describe"/> 用各自合适的量词表达。
/// </summary>
public class FailedRetrySummary
{
    /// <summary>K线失败的股票数（逐只抓，数字就是股票只数）。</summary>
    public int BarCodes { get; init; }

    /// <summary>流通市值失败名单里的代码数——**注意这不是"多少只股票的市值丢了"**：市值是整轮
    /// 扫描，失败时整批代码都会进名单，所以它只表示"有一轮市值待重试"（见类注释）。</summary>
    public int MarketCapCodes { get; init; }

    public int NetInflowCodes { get; init; }
    public int IndexConsCodes { get; init; }
    public int IndexWeightCodes { get; init; }
    public int ShareholderCodes { get; init; }
    public int DividendCodes { get; init; }

    /// <summary>市值有没有待重试的整轮（名单非空即为真）。</summary>
    public bool MarketCapPending => MarketCapCodes > 0;

    /// <summary>逐只重试的那几类加起来的股票数——这个数才是"多少只股票的数据确实缺着"。</summary>
    public int PerStockTotal =>
        BarCodes + NetInflowCodes + IndexConsCodes + IndexWeightCodes + ShareholderCodes + DividendCodes;

    /// <summary>有没有任何东西需要重试（决定"重新拉取失败股票"按钮能不能点）。</summary>
    public bool Any => PerStockTotal > 0 || MarketCapPending;

    /// <summary>按钮上那行字：没有就"无失败"，有就逐类列出，市值用"轮"。
    /// 例：<c>K线 12 只 · 市值 1 轮 · 净流入 3 只</c></summary>
    public string Describe()
    {
        if (!Any) return "无失败";

        var parts = new List<string>();
        if (BarCodes > 0) parts.Add($"K线 {BarCodes} 只");
        // 市值：整轮扫描，说"1轮"而不是把整批代码数报出来（那个数字没有"多少只票缺数据"的含义）
        if (MarketCapPending) parts.Add("市值 1 轮");
        if (NetInflowCodes > 0) parts.Add($"净流入 {NetInflowCodes} 只");
        if (IndexConsCodes > 0) parts.Add($"指数成分 {IndexConsCodes} 个");
        if (IndexWeightCodes > 0) parts.Add($"指数权重 {IndexWeightCodes} 个");
        if (ShareholderCodes > 0) parts.Add($"股东 {ShareholderCodes} 只");
        if (DividendCodes > 0) parts.Add($"分红 {DividendCodes} 只");
        return string.Join(" · ", parts);
    }
}
