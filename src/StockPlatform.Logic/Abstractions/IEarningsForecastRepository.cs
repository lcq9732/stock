using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 业绩预告 / 业绩快报的本地存取（2026-09-03 新增）。
///
/// 跟板块那种"快照整体替换"不同，这两张表是<b>增量累积</b>的：预告一经发布就是历史事实，
/// 公司后续修正是新增一行（主键含公告日），不覆盖旧的——修正方向本身就是有用的信息。
/// 所以接口只有 Upsert，没有 ReplaceAll。
/// </summary>
public interface IEarningsForecastRepository
{
    void EnsureSchema();

    int UpsertForecasts(IEnumerable<EarningsForecast> items);
    int UpsertExpress(IEnumerable<EarningsExpress> items);

    /// <summary>本地已有的最新预告公告日，增量抓取的水位线（没有数据时为 null）。</summary>
    DateTime? GetLatestForecastNoticeDate();

    /// <summary>本地已有的最新快报更新日。</summary>
    DateTime? GetLatestExpressUpdateDate();

    int CountForecasts();
    int CountExpress();

    /// <summary>取某个报告期的预告（同一股同一指标只取最后一次修正）。</summary>
    List<EarningsForecast> QueryByReportDate(DateTime reportDate);
}
