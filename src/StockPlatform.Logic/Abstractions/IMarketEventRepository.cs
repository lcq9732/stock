using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 大宗交易 / 机构调研 / 限售解禁 / 股东增减持的本地存取（2026-09-03 新增，本地此前全都没有）。
///
/// ⚠ 四张表**不再同构**：大宗交易 2026-09-17 改成按交易日整日替换（见下），另外三张
/// 仍是增量累积、INSERT OR REPLACE 幂等、按日期水位线做增量
/// （限售解禁再除外——它含未来的解禁计划，是"日程表"不是"历史事件表"，只能全量重取）。
/// </summary>
public interface IMarketEventRepository
{
    void EnsureSchema();

    /// <summary>
    /// 批内主键重复告警。<b>务必订阅</b>——主键少一列会让重复行静默互相覆盖，
    /// 龙虎榜席位表就这么丢过 7.5% 的数据且完全不报错。
    /// </summary>
    event Action<string>? OnWarning;

    /// <summary>
    /// **整日替换**某个交易日的大宗交易——不是 UPSERT。
    ///
    /// 这张表的主键第三列 <c>daily_rank</c> 原来存东财的 <c>DAILY_RANK</c>，而那个值
    /// **跨抓取不稳定**，UPSERT 认不出"同一笔"、每次重抓都多一份副本（全表曾多出 3087 行）。
    /// 现在序号由实现按接口返回顺序自赋，整天删了重写，反复跑多少次结果都一样。
    /// 详见 doc/block-trade-task-design.md。
    ///
    /// ⚠ 调用方必须先确认这一天**抓全了**（<c>BlockTradeDay.IsComplete</c>）；
    /// <paramref name="rows"/> 为空时实现不删，免得接口抽风返回空就抹掉一整天。
    /// </summary>
    int ReplaceBlockTradesForDay(DateTime day, IReadOnlyList<BlockTrade> rows);
    int UpsertOrgSurveys(IEnumerable<OrgSurvey> items);
    int UpsertShareLifts(IEnumerable<ShareLift> items);
    int UpsertHolderChanges(IEnumerable<HolderChange> items);

    /// <summary>某张表某个日期列的最大值——增量水位线。</summary>
    DateTime? GetLatestDate(string table, string column);

    int Count(string table);
}
