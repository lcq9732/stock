using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Abstractions;

/// <summary>股东数据本地存取（ShareholderCount + TopShareholder 两张表）——每次抓取返回该股全部历史，
/// 所以按 code "删旧写新"整体覆盖（而非按报告期累积）。</summary>
public interface IShareholderRepository
{
    void EnsureSchema();

    /// <summary>用一次抓取结果替换某只股票的全部股东数据（先删该 code 两表旧行，再写入）。</summary>
    void ReplaceByCode(string code, ShareholderData data);

    /// <summary>反查：某只股票的股东户数时间序列（按报告期升序），供后续分析"筹码集中/股东户数变化"。</summary>
    List<ShareholderCountRow> GetCountSeries(string code);

    /// <summary>已存有股东户数的股票只数（供界面显示"数据状态"）。</summary>
    int GetCodeCount();
}
