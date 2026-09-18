using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>分红送配本地存取（Dividend 表）——每次抓取返回该股全部分红历史，所以按 code "删旧写新"
/// 整体覆盖（仿 <see cref="IShareholderRepository"/>）。</summary>
public interface IDividendRepository
{
    void EnsureSchema();

    /// <summary>用一次抓取结果替换某只股票的全部分红方案（先删该 code 旧行，再写入）。</summary>
    void ReplaceByCode(string code, List<DividendRow> rows);

    /// <summary>反查：某只股票的全部分红方案（按公告日期升序），供股息率/除权核对。</summary>
    List<DividendRow> GetByCode(string code);

    /// <summary>全市场"最近12个月已实施的现金派息合计"，单位=元/股（表里是每10股口径，这里已除10）。
    /// 只算 progress='实施' 且除权除息日落在窗口内的方案——预案可能变更、不分配的行派息为0。
    /// 批量返回是因为全市场扫描要用，逐只 <see cref="GetByCode"/> 查 5000+ 次太慢。</summary>
    Dictionary<string, double> GetTrailingCashDividendPerShare(DateTime since);

    /// <summary>
    /// 同上，但**只算一只票**（2026-09-17）——口径与批量版完全一致，没有分红返回 0。
    ///
    /// 为什么要单只版：分析详情窗口只看一只票的股息率，原先却调批量版把整张 Dividend 表
    /// <c>GROUP BY</c> 一遍再从字典里取一个 key，白扫全表。批量版留着给全市场扫描用，两者
    /// 的 <c>WHERE</c> 条件必须一字不差，否则同一个数在列表页和详情页会对不上。
    /// </summary>
    double GetTrailingCashDividendPerShare(string code, DateTime since);

    /// <summary>全市场"按年分组的每股现金派息"，单位=元/股（表里是每10股口径，这里已除10）。
    /// 年份取**除权除息日所属年**（钱实际到账那一年），只算 progress='实施' 的方案；同一年有多次
    /// 派息（中期+年度）的已合并成一行。返回值里每只股票的列表按年份升序。
    ///
    /// 为什么要这个而不是复用 <see cref="GetTrailingCashDividendPerShare"/>：底仓法要判"连续分红
    /// 年数"和"派息趋势"，光看近12个月分不出"连分十年的电力股"和"去年头一回分红"，而全市场逐只
    /// 调 <see cref="GetByCode"/> 是 5000+ 次查询。见 DividendMetrics。</summary>
    Dictionary<string, List<(int Year, double PerShare)>> GetAnnualCashDividendPerShare(DateTime since);

    /// <summary>已存有分红方案的股票只数（供界面显示"数据状态"）。</summary>
    int GetCodeCount();

    /// <summary>
    /// 每只票**已实施且有除权日**的除权日集合（2026-09-18，给【分红对账】判重用）。
    ///
    /// ⚠ 判重必须按除权日、**不能按主键** <c>(code, announce_date)</c>：
    /// 新浪那列是页面上的"公告日期"，东财给的是预案公告日，语义不同——
    /// 按主键插会让同一个方案变成两行，库里凭空多出一次除权，
    /// 复权序列直接错，比原来缺一条还糟。
    /// </summary>
    Dictionary<string, HashSet<DateTime>> GetImplementedExDates();

    /// <summary>
    /// 补几行进来（2026-09-18，【分红对账】用）——**只加不改**：主键已存在的原样不动。
    /// 返回真正插进去的行数。
    /// </summary>
    int InsertMissing(IReadOnlyList<DividendRow> rows);

    /// <summary>
    /// 逐只的抓取状态（2026-09-18）——【拉取分红送配】的水位线，见
    /// <see cref="DividendFetchState"/>。key 是 code。表空着就返回空字典（首次全抓）。
    /// </summary>
    Dictionary<string, DividendFetchState> GetFetchStates();

    /// <summary>
    /// 一次性播种水位线（2026-09-18）——**只在 <c>DividendFetchState</c> 整张表是空的时候**
    /// 做一次，用 <c>Dividend</c> 表里每只票的 <c>max(fetched_at)</c> 当作"上次抓成功的时刻"。
    /// 返回播种了多少只；表非空时什么都不做、返回 0。
    ///
    /// 为什么要它：状态表是随这次迁移新建的，空表意味着全市场 5902 只都算"没抓过"，
    /// 于是**新版第一轮仍然会全量重抓一遍**——而库里绝大多数票 11 天前刚抓过
    /// （实测名单里 5825 只有分红行、只有 77 只一行都没有）。播种之后第一轮只剩那几十只。
    ///
    /// ⚠ 这是**近似值**，两处不精确，方向都是"宁可多抓"：
    ///   ① <c>fetched_at</c> 是"最后一次**写进**分红行的时刻"，不是"最后一次抓取的时刻"。
    ///      某只票今天抓了、但返回空（真没分红），老代码不写行，它的时刻就停在上一次——
    ///      播种偏早，下一轮会多抓它一次。
    ///   ② 一行都没有的票没法播种（"无分红"和"没抓过"在 Dividend 表里长得一模一样，
    ///      这正是要单独建状态表的理由），它们照旧去抓。
    /// 反过来绝不会把没抓过的票播成"抓过"——那才是会造成静默漏抓的方向。
    /// </summary>
    int SeedFetchStatesFromDividends();

    /// <summary>
    /// 写回一批抓取状态（整条覆盖，一个事务）。
    ///
    /// ⚠ 传进来的必须是**完整的**状态：失败那只要把原来的 <see cref="DividendFetchState.LastOkAt"/>
    /// 原样带上，否则一次失败就会把"抓过"这个事实抹掉，下一轮它又成了没抓过的。
    /// </summary>
    void SaveFetchStates(IReadOnlyList<DividendFetchState> states);
}
