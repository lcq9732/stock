using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 公司档案的本地存取（2026-09-08）。快照语义（接口无时间维度，每股一行），upsert 覆盖。
/// <b>空集合是空操作</b>——抓不到时保留库里上一次的，跟别的快照表同一条铁律。
/// </summary>
public interface ICompanyProfileRepository
{
    void EnsureSchema();

    /// <summary>
    /// 写一批。档案和长文本是**同一份响应拆两张表**，必须在一个事务里写完——
    /// 一张写了另一张漏，就会出现"有档案没简介"或反过来的半拉记录。
    /// </summary>
    int Upsert(IEnumerable<(CompanyProfile Profile, CompanyNarrative Narrative)> items);

    /// <summary>
    /// (代码, 全称, 简称) 全量，给实体消歧建索引用。
    /// 只返回这三列——这张表会被匹配步骤全表读，别把长文本捎上。
    ///
    /// 简称（<c>abbr</c>）是 2026-09-15 加的：5561 个 A 股简称实测**零重名**，
    /// 所以"对手名 == 简称"跟全称精确一样没有歧义空间，实测多认出 651 个名字。
    /// 取不到就是 null，<see cref="StockPlatform.Logic.Services.PartnerNameMatcher"/> 会跳过。
    /// </summary>
    List<(string Code, string FullName, string? Abbr)> GetAllNames();

    /// <summary>
    /// 档案里**已终止上市**（<c>listing_state='2'</c>）的票，(代码, 简称)。
    ///
    /// 东财 <c>RPT_HSF9_BASIC_ORGINFO</c> 的 <c>LISTING_STATE</c>：<c>0</c>=在市、<c>2</c>=已退市、
    /// <c>9</c>=待上市/暂缓上市（蚂蚁集团那批，有几只还在正常交易）、<c>10</c>=换代码吸收合并
    /// （深赤湾A→招商港口）。**只有 2 算退市**，9 和 10 各有各的语义，混进来会把在交易的票
    /// 踢出日常轮询。
    ///
    /// 给【补全退市名单】当第二个候选来源用：它原来的候选集要减去在市名册，于是**还挂在在市
    /// 名册里的已退市票永远不是候选**——920305 云创退就是这么漏了半年的（名册说在市、
    /// 档案说退市，谁也纠正不了谁）。这一份不受名册限制。
    /// </summary>
    List<(string Code, string Name)> GetDelistedCodes();

    /// <summary>(档案条数, 长文本条数)。两者应该相等，不等就是有半拉记录。</summary>
    (int Profiles, int Narratives) GetCounts();
}
