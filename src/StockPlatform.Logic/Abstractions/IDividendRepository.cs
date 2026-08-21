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
}
