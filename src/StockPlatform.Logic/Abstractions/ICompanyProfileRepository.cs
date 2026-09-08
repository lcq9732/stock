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
    /// (代码, 全称) 全量，给实体消歧建索引用。
    /// 只返回这两列——这张表会被匹配步骤全表读，别把长文本捎上。
    /// </summary>
    List<(string Code, string FullName)> GetAllNames();

    /// <summary>(档案条数, 长文本条数)。两者应该相等，不等就是有半拉记录。</summary>
    (int Profiles, int Narratives) GetCounts();
}
