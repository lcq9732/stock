using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;

namespace StockPlatform.Analyzer.ViewModels;

/// <summary>
/// 读【拉取财报预约日】抓来的数据（EarningsSchedule 表，Fetcher 那边负责写，这边只读）。
///
/// 表不存在（老库、或者一次都没抓过）时返回空表——各页会自然退回手填值。
/// 所以这里把异常整个吞掉是有意的：一个还没启用的数据源不该挡住整页加载。
/// </summary>
public static class EarningsLookup
{
    /// <summary>
    /// 每只股票该显示的那条财报日记录，键是 6 位代码。取不到就是空表。
    ///
    /// 优先"还没披露的下一期"，没有就退回"最近一期已披露"——两期都披露完、下期预约表还没发布的
    /// 那段空窗里，后者是唯一能显示的东西（见 GetLatestByCode 的注释）。
    /// 界面据 <see cref="EarningsScheduleRow.Pending"/> 区分这两种，别混为一谈。
    /// </summary>
    public static Dictionary<string, EarningsScheduleRow> LoadUpcoming()
    {
        try
        {
            var paths = new AnalyzerPaths();
            return new SqliteEarningsScheduleRepository(paths.CurrentDb).GetLatestByCode();
        }
        catch
        {
            return new Dictionary<string, EarningsScheduleRow>();
        }
    }
}
