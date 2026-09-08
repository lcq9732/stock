using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 前五大客户/供应商的本地存取（2026-09-07）。见 <see cref="CustomerSupplier"/>。
///
/// 写入是**累积**语义（按 code + 报告期 + 客户/供应商 + 名次 upsert），不是快照：
/// 数据按报告期一期一期出，老报告期的行永远有效，没有"这轮没出现＝已下架"这回事。
/// 跟板块那种快照表正好相反，所以这里**没有任何 DELETE**。
/// </summary>
public interface ICustomerSupplierRepository
{
    void EnsureSchema();

    /// <summary>写入一批。空集合是空操作。返回写入行数。</summary>
    int Upsert(IEnumerable<CustomerSupplier> items);

    /// <summary>某年库里有多少行。</summary>
    int CountByYear(int year);

    // ── 按年的完成度（2026-09-08）────────────────────────────────────────
    //
    // 为什么要单独记而不是"看这一年有没有数据"：新式任务骨架会在 Deadline / MaxItems 到点时
    // **从批中间截断**，而且那算**正常完成**（走 OnCompletedAsync、返回 Completed、界面打勾）。
    // 只看"有没有数据"的话，2019 年抓了 6000/66000 行也算抓过了，下轮直接跳过——
    // 剩下 6 万行永远不来，**毫无征兆**。今年去年靠"每轮都重抓"能自愈，2002-2024 不能。
    //
    // 一般原则：任务的水位线粒度必须**细于**骨架的截断粒度。截断粒度是批（2000 行），
    // 所以水位线不能是"年（有/无）"，得是"这一年落了多少行 / 该有多少行"。

    /// <summary>记下某年的完成度。<paramref name="reported"/> 是接口自报的总行数。</summary>
    void SaveYearState(int year, int reported, int saved);

    /// <summary>每年的 (接口自报, 实际落库)。没抓过的年份不在字典里。</summary>
    Dictionary<int, (int Reported, int Saved)> GetYearStates();

    // ── 实体消歧（2026-09-08）──────────────────────────────────────────

    /// <summary>
    /// 需要匹配的对手名（去重）。只返回**还没匹配过**或要求重算的，避免每轮重扫 76 万行。
    /// </summary>
    List<string> GetPartnerNamesToMatch(bool all = false);

    /// <summary>
    /// 回填匹配结果：把 partner_name 等于 key 的行统一写上代码和档次。
    /// <b>空字典是空操作</b>——档案库没拉到时保留上次的匹配结果，绝不抹成 NULL。
    /// </summary>
    int ApplyMatches(IReadOnlyDictionary<string, (string Code, string MatchType)> matches);

    /// <summary>(已匹配行数, 有名字但没匹配上的行数, 匿名行数)，给收尾报告用。</summary>
    (int Matched, int Unmatched, int Anonymous) GetMatchStats();

    /// <summary>(总行数, 覆盖的股票数, 最早报告期, 最晚报告期)，给界面和体检看。</summary>
    (int Rows, int Stocks, DateTime? First, DateTime? Last) GetStats();
}
