using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 个股行业/题材归属的本地存取（2026-09-03 新增）。
///
/// 补的是证监会分类的粒度不足：实测 1867 只（32.5%）大类为空只能退回门类，
/// 而"制造业"一个门类装了 3596 只——拿它做行业中性化等于没中性化。
/// 老的 <c>StockIndustry</c> 表**保留不删**，给东财覆盖不到的那 100 多只兜底。
/// </summary>
public interface IStockBoardMapRepository
{
    void EnsureSchema();

    /// <summary>清空两张表。⚠ 只该在"新数据确认拿得到"之后调用，见实现里的注释。</summary>
    void ClearAll();

    int UpsertIndustries(IEnumerable<StockIndustryEm> items);
    int UpsertThemes(IEnumerable<StockThemeEm> items);

    int CountIndustries();
    int CountThemes();
    int CountIndustryStocks();

    /// <summary>每只股票的最细行业（board_level 最大那条）。缺的应退回证监会分类兜底。</summary>
    Dictionary<string, (string BoardCode, string BoardName)> GetFinestIndustryByStock();

    /// <summary>
    /// 板块代码 → (父板块, 自己的层级)。一级板块的父是 null。
    ///
    /// 这张表本身没有"板块的父是谁"这一列，但**同一只股票的 1/2/3 级三行就是一条父子链**，
    /// 按股票聚合就能把整棵树还原出来（实测 465 个子板块、0 个多父冲突）。
    ///
    /// 用途是**交叉校验**东财终端本地文件解出来的层级树（2026-09-07）：那个格式是逆向出来的、
    /// 没有文档，东财哪天改了结构，解析结果会是一堆看着像模像样的错关系。
    ///
    /// ⚠ 必须对**父子归属**，不能只对层级数字 —— 这是拿真数据换来的教训：解析器的第一版
    ///   层级数字 932 处全对、却有 51 条边的父是错的（"银行Ⅱ 挂在石油石化下"）。错位之后
    ///   每个子板块照样挂在一个层级正确的父上，只对层级的校验会一路放行。
    /// </summary>
    Dictionary<string, (string? Parent, int Level)> GetBoardParents();
}
