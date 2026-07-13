using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 把底层 <see cref="IBarRepository"/> 的数据截断到某个日期(含当天)——"按历史截止日期验证"用：
/// 用户在阶梯低点法 Tab 输入一个日期，整个分析(引擎 + <see cref="MarketEnvironmentCalculator"/>
/// 的宽度/热度 + 条件详情图)都只看那天收盘及之前的数据，等价于回测在那个时间点的截断计算，
/// 用来手工核对回测结果。只包读操作；写操作直接转发（分析程序本来就不写）。
/// </summary>
public class CutoffBarRepository : IBarRepository
{
    private readonly IBarRepository _inner;
    private readonly DateTime _cutoffEnd; // 截止日当天 23:59:59.999…，含当天所有粒度的bar

    public CutoffBarRepository(IBarRepository inner, DateTime cutoffDate)
    {
        _inner = inner;
        _cutoffEnd = cutoffDate.Date.AddDays(1).AddTicks(-1);
    }

    public List<Bar> Query(string code, string granularity, DateTime? start = null, DateTime? end = null)
        => _inner.Query(code, granularity, start, end == null || end > _cutoffEnd ? _cutoffEnd : end);

    public DateTime? GetLatestPeriodStart(string code, string granularity)
    {
        var bars = Query(code, granularity);
        return bars.Count > 0 ? bars[^1].PeriodStart : null;
    }

    public DateTime? GetOverallLatestPeriodStart(string granularity)
        => _inner.GetOverallLatestPeriodStartOnOrBefore(granularity, _cutoffEnd);

    public DateTime? GetOverallLatestPeriodStartOnOrBefore(string granularity, DateTime cutoff)
        => _inner.GetOverallLatestPeriodStartOnOrBefore(granularity, cutoff > _cutoffEnd ? _cutoffEnd : cutoff);

    public DateTime? GetOverallEarliestPeriodStart(string granularity) => _inner.GetOverallEarliestPeriodStart(granularity);
    public List<string> GetAllCodes() => _inner.GetAllCodes();
    public void EnsureSchema() => _inner.EnsureSchema();
    public void InsertOrIgnore(IEnumerable<Bar> bars) => _inner.InsertOrIgnore(bars);
}
