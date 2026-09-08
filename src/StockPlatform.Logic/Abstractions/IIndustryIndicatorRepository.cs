using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 行业景气指标的本地存取（2026-09-07）。
///
/// 这里有**两种截然不同的写入语义**，分清楚了才不会出事：
///
/// · <b>目录</b>（指标字典 + 股票映射）是<b>快照</b>——东财一次给全 116 个指标、1190 条映射。
///   半截名单进库＝凭空少掉一批股票的指标关联，所以必须"三页都拿到才提交"。
///
/// · <b>序列</b>是<b>累积</b>——每个指标独立增量，抓一个存一个。某个指标这轮没抓到，
///   下轮它的水位线还是旧的，自然会重抓，已抓到的 115 个不受影响。
///
/// 两者共同的铁律跟别的表一样：<b>空集合是空操作</b>，抓取失败时保留库里上一次的。
/// </summary>
public interface IIndustryIndicatorRepository
{
    void EnsureSchema();

    /// <summary>指标字典写入（upsert）。空集合是空操作。</summary>
    int UpsertIndicators(IEnumerable<IndustryIndicator> items);

    /// <summary>
    /// 股票↔指标映射，<b>快照替换</b>：清掉不在本批里的旧关联再写入，一个事务。
    /// ⚠ 只在目录完整拿到之后调——空集合是空操作，绝不清空。
    /// </summary>
    int ReplaceLinks(IEnumerable<StockIndicatorLink> links);

    /// <summary>指标序列写入（upsert，按 indicator_id + trade_date）。累积语义，不删旧的。</summary>
    int UpsertPoints(IEnumerable<IndicatorPoint> points);

    /// <summary>全部指标定义——抓序列时要按 ChartType 决定走哪个接口。</summary>
    List<IndustryIndicator> GetIndicators();

    /// <summary>
    /// 每个指标已有的最新日期，**增量的水位线**。没抓过的不在字典里（＝该全量拉）。
    /// 一次全查出来而不是逐个查：116 次往返换 1 次。
    /// </summary>
    Dictionary<string, DateTime> GetLatestDates();

    /// <summary>
    /// 每个指标挑一只**代表股**（接口查序列必须带 SECUCODE，否则返回股票×日期的笛卡尔积、
    /// 撞分页上限被静默截断）。取哪只都行——值完全一样——但必须**稳定**，
    /// 所以按 code 排序取第一只：换来换去的话日志对不上、出了问题没法复现。
    /// </summary>
    Dictionary<string, string> GetRepresentativeStocks();

    /// <summary>(指标数, 序列行数, 映射数)，给界面和体检看。</summary>
    (int Indicators, int Points, int Links) GetCounts();
}
