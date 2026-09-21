using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>Local SQLite storage for the Bar table (see doc/data-platform-design.md section 4).</summary>
public interface IBarRepository
{
    void EnsureSchema();
    void InsertOrRefreshUnconfirmed(IEnumerable<Bar> bars);
    DateTime? GetLatestPeriodStart(string code, string granularity);
    /// <summary>Latest period_start across ALL codes for a granularity — used by the Analyzer to
    /// show "本地数据最新到 X" without needing a separate sync-state file (see
    /// doc/analysis-app-design.md — the Analyzer reads the local database directly).</summary>
    DateTime? GetOverallLatestPeriodStart(string granularity);
    /// <summary>Earliest period_start across ALL codes for a granularity — paired with
    /// <see cref="GetOverallLatestPeriodStart"/> so the Fetcher UI can show "本地数据覆盖范围：X 至 Y"
    /// (see doc/data-platform-design.md).</summary>
    DateTime? GetOverallEarliestPeriodStart(string granularity);
    /// <summary>某个时点(含)之前的最新 period_start——给"按历史截止日期验证"用（见
    /// CutoffBarRepository）：用户输入的截止日可能是周末/节假日，需要据此定位真正的最后交易日。</summary>
    DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff);
    List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null);
    /// <summary>
    /// 这只票某个粒度下**最后一根**K线，没有就返回 null（2026-09-17）。
    ///
    /// 只要"最新收盘价 + 是哪天"的调用方走这里，别用 <c>Query(code, gran)[^1]</c>：那会把全历史
    /// 读出来（老股 5000+ 行，每行两次 <c>ParseExact</c>）再丢掉，分析详情窗口原先就卡在这上面。
    /// 周/月线不落库、由日线现算，传进来会抛 <see cref="ArgumentException"/>。
    /// </summary>
    Bar? GetLatestBar(string code, string granularity);
    /// <summary>
    /// 给【板块指数合成】的**只追加**那一路用（2026-09-21）：返回
    /// <paramref name="from"/> 那天**之前的最后一根**（作为算涨幅的基准），加上 from 及以后的全部。
    ///
    /// ⚠ 为什么不能"按天数往前切一段"：基准必须是这只票**自己**的上一根，而停牌可以长达几百天
    /// （见 <c>BoardIndexSynthesizer</c> 里那句"用各成分股自己的上一根算涨幅，天然处理停牌缺口"）。
    /// 切固定窗口会在长停牌票上漏掉基准，那一天它就被悄悄排除在均值之外——跟全量算出来的不一样。
    /// 没有任何早于 from 的数据时只返回 from 及以后的，调用方自己会因为"不足两根"跳过它。
    /// </summary>
    List<Bar> QueryForAppend(string code, string granularity, DateTime from);

    /// <summary>Bar表里所有"个股"代码（6位纯数字）——Analyzer各选股Tab的扫描全集。大盘指数
    /// （带前缀的8位符号如"sh000001"，见 MarketIndexCatalog）故意排除在外：指数K线只是给大盘
    /// 环境过滤/回测用的参照数据，不参与选股。</summary>
    List<string> GetAllCodes();
}
