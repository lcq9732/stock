using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// <c>StockWatchIndicator</c>（我们自己认定的"这票该看这指标"）的本地存取。
/// 见 doc/watch-item-design.md §4.1、§6。
///
/// ════ 跟 <see cref="IIndustryIndicatorRepository.ReplaceLinks"/> 的关键差别 ════
/// 那边是**全表快照替换**（<c>DELETE FROM StockIndustryIndicator</c>），因为那张表只有一个
/// 主人——东财目录。这张表有**两个**主人：规则（<c>origin=rule</c>）和人（<c>origin=manual</c>），
/// 所以这里的替换是 <b>"只删自己那部分"</b>，见 <see cref="ReplaceRuleLinks"/>。
///
/// 写成全表替换的后果是静默的：人手挂的映射在下一轮规则跑完就没了，界面上只表现为
/// "这只票恰好没有指标"。这跟往东财那张表里补映射会踩的是同一个坑，只是主人换了一对。
/// </summary>
public interface IWatchIndicatorRepository
{
    void EnsureSchema();

    /// <summary>
    /// 规则派生行的替换：**只删 <c>origin='rule'</c> 的旧行**再写入新的，一个事务。
    /// <c>origin='manual'</c> 的一行都不碰。
    ///
    /// ⚠ 空集合是空操作（跟库里其他仓储同一条铁律）——规则配置读失败或全部校验不过时
    /// 传进来的就是空集合，那时候应该**保留上一轮的结果**，而不是把映射清空。
    /// </summary>
    /// <returns>写入的行数。</returns>
    int ReplaceRuleLinks(IEnumerable<WatchIndicatorLink> links);

    /// <summary>一只票挂了哪些指标（含规则的和手挂的），按 <c>weight</c> 升序。</summary>
    List<WatchIndicatorLink> GetLinks(string code);

    /// <summary>(规则行数, 手挂行数, 覆盖股票数)，给日志和体检看。</summary>
    (int RuleLinks, int ManualLinks, int Stocks) GetCounts();
}
