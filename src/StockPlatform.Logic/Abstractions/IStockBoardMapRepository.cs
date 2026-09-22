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

    /// <summary>
    /// 清空两张表。
    ///
    /// ⚠ **抓取那条路已经不用它了**（2026-09-22）：先清后写的话，抓了一半停下来就只剩
    /// 一部分票的归属，其余票静默退回证监会粗分类、而且没有任何标记说这份快照是半截的。
    /// 现在走 <see cref="ReplaceForStocks"/> 按票整只替换。留着这个方法是给"人工要把这两张表
    /// 倒掉重来"用的。
    /// </summary>
    void ClearAll();

    int UpsertIndustries(IEnumerable<StockIndustryEm> items);
    int UpsertThemes(IEnumerable<StockThemeEm> items);

    /// <summary>
    /// **按票整只替换**这一批股票的行业/题材归属（2026-09-22）——先删这些票的旧行、再插新的，
    /// 一个事务。
    ///
    /// 为什么是"按票"而不是"清全表再逐批写"：这两张表有一条隐含不变式——**同一只股票的
    /// 1/2/3 级三行构成一条父子链**，<see cref="GetBoardParents"/> 就是按股票聚合把整棵板块树
    /// 还原出来的。一只票身上混着两代的行，这条链就断了；而 <see cref="GetFinestIndustryByStock"/>
    /// 取的是 <c>board_level</c> 最大那条，残留一条旧的三级板块会**压掉**本轮的正确值——
    /// 那是静默错值，比"没有数据、退回兜底"更糟。
    ///
    /// 按票替换之后，任何时刻库里每只票都是自洽的一代：抓到的是本轮新值（连已失效的归属
    /// 也一并清掉），没抓到的是上一轮的完整值。代价是"全市场同一时刻"这个性质没了——
    /// 归属变动很慢、季度跑一次，这个代价远小于"整块票凭空消失"。
    ///
    /// <paramref name="industries"/> 和 <paramref name="themes"/> 的代码**取并集**当作要替换的票：
    /// 一只票可能只有题材没有行业（或反过来），那时"本轮它没有行业归属"也是事实，旧行该删。
    /// 所以调用方必须保证**一只票的行不被批边界切开**，否则会把同一轮里另一半删掉。
    /// </summary>
    /// <returns>写入的行数（行业, 题材）。</returns>
    (int Industry, int Theme) ReplaceForStocks(
        IEnumerable<StockIndustryEm> industries, IEnumerable<StockThemeEm> themes);

    /// <summary>
    /// 删掉 <paramref name="cutoff"/> 之前落盘的行，返回删了多少（2026-09-22）。
    ///
    /// 配合 <see cref="ReplaceForStocks"/> 用，只在**整轮抓完且对账通过**之后调：那时全市场
    /// 都已按票替换过一遍，剩下的旧时间戳就是"本轮一行都没出现"的票——被东财摘掉全部归属的
    /// 那种。中途停下来时**不能**调它，那会把"这一轮还没轮到"误当成"已经没有归属"。
    /// </summary>
    int PurgeOlderThan(DateTime cutoff);

    int CountIndustries();
    int CountThemes();
    int CountIndustryStocks();

    /// <summary>每只股票的最细行业（board_level 最大那条）。缺的应退回证监会分类兜底。</summary>
    Dictionary<string, (string BoardCode, string BoardName)> GetFinestIndustryByStock();

    /// <summary>
    /// 全部 (股票, 板块) 对，**不分层级**（2026-09-11，给观察项的 L1 派生用，
    /// 见 doc/watch-item-design.md §5）。
    ///
    /// 为什么不用 <see cref="GetFinestIndustryByStock"/>：那个只给最细的一级，而规则可能挂在
    /// **任何一级**上——"锂电池 BK1303"是三级，"电池 BK1033"是二级，"电力设备 BK1200"是一级。
    /// 只拿最细一级的话，挂在二级上的规则对谁都匹配不上，而且是**静默**匹配不上。
    /// </summary>
    List<(string Code, string BoardCode)> GetAllIndustryLinks();

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
