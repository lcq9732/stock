namespace StockPlatform.Logic.Models;

/// <summary>
/// 个股的东财**行业**归属（三级分类）。补的是证监会那套分类的粒度不足。
///
/// 实测对比：证监会 33 门类 + 84 大类，但 <b>1867 只（32.5%）大类为空、只能退回门类</b>，
/// 而"制造业"一个门类就装了 3596 只（占全市场 62%）——拿它做行业中性化等于没中性化。
/// 东财三级（一级31/二级128/三级337），最大的三级行业也才 627 只。
///
/// 一只股票有多行（三级各一行），<see cref="BoardLevel"/> 区分。要最细行业就取
/// <c>MAX(board_level)</c> 那一行。
/// </summary>
public class StockIndustryEm
{
    public string Code { get; set; } = "";
    /// <summary>东财板块码，形如 BK1325。</summary>
    public string BoardCode { get; set; } = "";
    public string BoardName { get; set; } = "";
    /// <summary>1/2/3，越大越细。</summary>
    public int? BoardLevel { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// 个股的东财**题材/概念**归属，带<b>入选理由</b>。
///
/// 跟 <c>BoardMember</c> 是两回事：那张是"板块→成分股"的官方名单（判断板块景气度用，
/// 必须完整所以走 push2）；这张是"个股→题材"并且带 <see cref="Reason"/>——公司为什么
/// 被归到这个题材，原文取自互动易回复、公告等。
///
/// <see cref="Reason"/> + <see cref="IsPrecise"/> 合起来能分辨**实质业务 vs 蹭概念**：
/// 一个板块里如果一半成分股的入选理由是"公司互动易回复称关注该领域"，那这个板块的成分
/// 质量就说明了问题。判断真假风口时，这是唯一能自动化的"含金量"判据。
/// </summary>
public class StockThemeEm
{
    public string Code { get; set; } = "";
    public string BoardCode { get; set; } = "";
    public string BoardName { get; set; } = "";
    /// <summary>是否精确匹配（false = 边缘关联）。</summary>
    public bool IsPrecise { get; set; }
    /// <summary>该股在此题材中的排位。</summary>
    public int? BoardRank { get; set; }
    /// <summary>入选理由原文，可空。</summary>
    public string Reason { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}
