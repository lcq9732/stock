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
}
