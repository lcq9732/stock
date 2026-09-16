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

    /// <summary>
    /// 记下某年的完成度。
    /// </summary>
    /// <param name="reported">接口自报的总行数，<b>含非 A 股主体</b>。</param>
    /// <param name="saved">实际落库行数。</param>
    /// <param name="skipped">
    /// 主动丢掉的行数（非 A 股代码等）。<b>必须记</b>——只比 saved 和 reported 的话，
    /// 主动过滤会被误判成"没抓齐"，那几年每轮重抓且永远抓不齐。
    /// </param>
    void SaveYearState(int year, int reported, int saved, int skipped);

    /// <summary>每年的 (接口自报, 实际落库, 主动丢弃)。没抓过的年份不在字典里。</summary>
    Dictionary<int, (int Reported, int Saved, int Skipped)> GetYearStates();

    // ── 实体消歧（2026-09-08）──────────────────────────────────────────

    /// <summary>
    /// 需要匹配的对手名（去重）。只返回**还没匹配过**或要求重算的，避免每轮重扫 76 万行。
    /// </summary>
    List<string> GetPartnerNamesToMatch(bool all = false);

    /// <summary>
    /// 回填匹配结果：把 partner_name 等于 key 的行统一写上代码和档次。
    /// <b>空字典是空操作</b>——档案库没拉到时保留上次的匹配结果，绝不抹成 NULL。
    /// </summary>
    /// <param name="evaluated">
    /// 本轮**评估过的全部名字**（含没命中的）。给了就把"评估过但不在 <paramref name="matches"/>
    /// 里"的那些清成 NULL，也就是允许改判和撤销。
    ///
    /// ⚠ **判据改版时缺了它，修复就落不了地**（2026-09-15 的坑）：原来这个方法只遍历命中的名字，
    ///   于是「中国铝业集团有限公司」在新规则下不再命中、压根不在字典里，它那条旧的错值
    ///   <c>601600</c> 就原封不动留着。加了 MatcherVersion 触发全量重匹也一样清不掉。
    ///
    /// 传 null＝老行为（只写不清），增量模式用这个：增量本来就只评估 partner_code 为 NULL 的，
    /// 没有可清的东西，多跑一遍 UPDATE 纯属浪费。
    /// </param>
    int ApplyMatches(IReadOnlyDictionary<string, (string Code, string MatchType)> matches,
                     IReadOnlyCollection<string>? evaluated = null);

    /// <summary>
    /// 最该补子公司名单的公司：按**被别人写进前五大客户/供应商的次数**降序取前 N 个。
    ///
    /// 只数 exact/short/qualified 三个可信档——normalized 和 parent_group 都可能指错主体，
    /// 拿它们排序会让下载清单跑偏。
    /// </summary>
    List<string> GetMostReferencedPartners(int top);

    /// <summary>
    /// 上一次匹配用的规则版本。<b>0 = 从没记过</b>（老库，或从没跑过消歧）。
    /// 跟 <see cref="StockPlatform.Logic.Services.PartnerNameMatcher.MatcherVersion"/> 比对，
    /// 不相等就说明判据改过，要全量重匹一次。
    /// </summary>
    int GetMatcherVersion();

    /// <summary>记下本轮用的规则版本。**必须在 ApplyMatches 成功之后才写**——
    /// 先写版本再匹配的话，中途失败就会留下"版本已是新的、数据还是旧的"这种修不回来的状态。</summary>
    void SetMatcherVersion(int version);

    /// <summary>(已匹配行数, 有名字但没匹配上的行数, 匿名行数)，给收尾报告用。</summary>
    (int Matched, int Unmatched, int Anonymous) GetMatchStats();

    /// <summary>(总行数, 覆盖的股票数, 最早报告期, 最晚报告期)，给界面和体检看。</summary>
    (int Rows, int Stocks, DateTime? First, DateTime? Last) GetStats();
}
