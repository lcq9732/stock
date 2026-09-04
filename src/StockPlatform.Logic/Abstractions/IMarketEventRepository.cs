using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 大宗交易 / 机构调研 / 限售解禁 / 股东增减持的本地存取（2026-09-03 新增，本地此前全都没有）。
///
/// 四张表同构：增量累积、INSERT OR REPLACE 幂等、按日期水位线做增量
/// （限售解禁除外——它含未来的解禁计划，是"日程表"不是"历史事件表"，只能全量重取）。
/// </summary>
public interface IMarketEventRepository
{
    void EnsureSchema();

    /// <summary>
    /// 批内主键重复告警。<b>务必订阅</b>——主键少一列会让重复行静默互相覆盖，
    /// 龙虎榜席位表就这么丢过 7.5% 的数据且完全不报错。
    /// </summary>
    event Action<string>? OnWarning;

    int UpsertBlockTrades(IEnumerable<BlockTrade> items);
    int UpsertOrgSurveys(IEnumerable<OrgSurvey> items);
    int UpsertShareLifts(IEnumerable<ShareLift> items);
    int UpsertHolderChanges(IEnumerable<HolderChange> items);

    /// <summary>某张表某个日期列的最大值——增量水位线。</summary>
    DateTime? GetLatestDate(string table, string column);

    int Count(string table);
}
