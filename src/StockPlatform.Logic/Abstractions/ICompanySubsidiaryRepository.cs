using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>
/// 年报子公司名单的存取（2026-09-11）。
///
/// **累积语义，没有 DELETE**：名单按报告期一期一期出，老报告期的行永远有效
/// （跟 StockCustomerSupplier 同样的道理）。重解析同一份 PDF 时靠主键覆盖。
/// </summary>
public interface ICompanySubsidiaryRepository
{
    void EnsureSchema();

    /// <summary>
    /// 写一份 PDF 解析出来的名单，**连同水位线一起写**。
    ///
    /// ⚠ 两者必须在同一个事务里：只写名单不写水位线，下轮会重解析；只写水位线不写名单，
    ///   那份 PDF 的结果就永远丢了而且没人知道。<paramref name="items"/> 为空也要写水位线
    ///   （found_count = 0 说明版式认不出来，是要记下来的事实，不是"没处理过"）。
    /// </summary>
    void Save(string code, DateTime reportDate, int parserVersion,
              IReadOnlyList<CompanySubsidiary> items);

    /// <summary>
    /// 已经用**当前规则版本**解析过的 (代码, 报告期)。规则版本变了的不算，要重解析。
    /// </summary>
    HashSet<(string Code, DateTime ReportDate)> GetParsed(int parserVersion);

    /// <summary>
    /// 全部「子公司名 → 母公司代码」。给实体消歧建索引用。
    ///
    /// ⚠ 同一个名字可能落到多个母公司（集团内交叉持股、或者两家公司的子公司重名）。
    ///   这种**一律丢掉**——宁可少连一条边，也不能连错。返回值里已经去掉了。
    /// </summary>
    Dictionary<string, string> GetNameToParent();

    (int Rows, int Parents, int Parsed, int Empty) GetStats();
}
