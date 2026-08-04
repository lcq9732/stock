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

    /// <summary>已存有分红方案的股票只数（供界面显示"数据状态"）。</summary>
    int GetCodeCount();
}
