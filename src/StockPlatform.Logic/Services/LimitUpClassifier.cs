namespace StockPlatform.Logic.Services;

/// <summary>
/// A股涨跌停幅度判定——按板块+ST状态区分。跟 MarketClassifier 分开是因为这个判定还需要股票名称
/// （ST状态不是代码能看出来的），而 MarketClassifier 特意保持"只看代码"的纯函数契约。
/// </summary>
public static class LimitUpClassifier
{
    // 实际涨跌停价会按最小价格变动单位(0.01元)四舍五入，导致真实涨幅比理论百分比有零点几个百分点
    // 的出入——留这个容差，避免"就差一点点"的涨停被漏判。
    private const double TolerancePercent = 0.3;

    public static double LimitUpPercent(string code, string name)
    {
        if (name.Contains("ST")) return 5.0; // covers both "ST" and "*ST"
        return MarketClassifier.Classify(code) switch
        {
            MarketBoard.Beijing => 30.0,
            MarketBoard.ShanghaiStar => 20.0,
            MarketBoard.ShenzhenChiNext => 20.0,
            _ => 10.0,
        };
    }

    /// <param name="pctChange">(今日收盘-昨日收盘)/昨日收盘*100</param>
    public static bool IsLimitUp(string code, string name, double pctChange) =>
        pctChange >= LimitUpPercent(code, name) - TolerancePercent;
}
