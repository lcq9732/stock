using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 方案类公告（回购/定增/重组）进展的本地存取，见 doc/watch-item-design.md §4.1。
///
/// ⚠ **只增不删**。这是事实表——"历史上发过哪些公告"。方案结束了要摘掉的是观察项
/// （<c>WatchItem</c>），不是这里的行。删了就没法回溯"当初方案说的上限是 573"、
/// 也没法对账"进展公告拖了几个月"。「事实只增、待办才有增删」见设计文档 §2.1。
///
/// 失败语义＝**累积**（不是快照）：一只票抓失败不影响其余，没有"整项放弃"的必要。
/// 跟 <see cref="IIndustryIndicatorRepository.UpsertPoints"/> 那一步同类，
/// 跟 <see cref="IIndustryIndicatorRepository.ReplaceLinks"/> 正相反。
/// </summary>
public interface IPlanAnnouncementRepository
{
    void EnsureSchema();

    /// <summary>按 (code, kind, announce_date, stage) upsert。空集合是空操作。</summary>
    int Upsert(IEnumerable<PlanAnnouncement> items);

    /// <summary>一只票某类方案的全部记录，按公告日倒序。</summary>
    List<PlanAnnouncement> GetByCode(string code, string kind);

    /// <summary>
    /// 每只票**当前是否有未结束的方案**——L1 派生据此决定挂不挂观察项。
    /// "未结束"＝有 <c>方案</c> 阶段的记录，且之后没有 <c>完毕</c>／<c>终止</c>。
    /// </summary>
    Dictionary<string, PlanAnnouncement> GetOpenPlans(string kind);

    /// <summary>已入库的最新公告日，做增量回看的水位线。没有就返回 null。</summary>
    DateTime? GetLatestAnnounceDate(string kind);

    /// <summary>(记录数, 覆盖股票数, 未结束方案数)，给日志和体检看。</summary>
    (int Rows, int Stocks, int OpenPlans) GetCounts(string kind);
}
