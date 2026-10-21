using StockPlatform.Data.Orchestration;
using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 财务报表的**离线模拟**源（2026-10-21）——一个请求都不发，凭空造出近八个报告期的几项科目。
/// 跟 <see cref="MockShareholderProvider"/> 同一个用途和同一道闸：只在 DEBUG 构建里、且
/// <c>OfflineMock</c> 开关打开时才会被装上。
///
/// ════ 为什么要它 ════
/// 【拉取财务报表】2026-10-21 挪进日更、去掉每轮上限、改了报错口径。单测覆盖得了判据和落库，
/// 覆盖不了"计划排到它 → 开工那行 → 逐只落库 → 收工汇总 → 状态列"这整条链；真跑一轮又是几千个东财请求。
///
/// ════ 造出来的数据长什么样 ════
/// 最新一期取这只票在本地预约日表里**已实际披露**的那一期（跟判据同口径，所以"抓完就该判成最新"测得出来）；
/// 没有披露记录的取 <see cref="FinancialFetchPlanner.LatestExpectedReportPeriod"/>。再往前推七期，共八期。
///
/// ════ 两个标记代码 ════
///   · 代码含 <see cref="FailMarker"/> ⇒ 抛异常，验"失败只报只数、不记问过了、下一轮再取"；
///   · 代码含 <see cref="LagMarker"/> ⇒ 最新一期故意少给一期，验"已披露、数据源还没更新"。
/// </summary>
public sealed class MockFinancialProvider(string dbPath) : IFinancialProvider
{
    /// <summary>含这个片段的代码一律失败。</summary>
    public const string FailMarker = "999";

    /// <summary>含这个片段的代码少给最新一期（模拟数据源还没更新）。</summary>
    public const string LagMarker = "777";

    private const int Periods = 8;

    private Dictionary<string, (DateTime Period, DateTime ActualDate)>? _disclosed;

    public event Action<string>? OnStatus;

    public Task<List<FinancialValue>> GetAllAsync(string code, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (code.Contains(FailMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"[离线模拟] {code} 按 FailMarker 规则失败");

        _disclosed ??= new SqliteEarningsScheduleRepository(dbPath).GetLatestDisclosureByCode(DateTime.Today);
        var latest = _disclosed.TryGetValue(code, out var d)
            ? d.Period
            : FinancialFetchPlanner.LatestExpectedReportPeriod(DateTime.Today);
        if (code.Contains(LagMarker, StringComparison.Ordinal))
        {
            latest = QuarterEnd(latest.AddMonths(-3));
            OnStatus?.Invoke($"[离线模拟] {code} 按 LagMarker 规则少给最新一期");
        }

        var rows = new List<FinancialValue>();
        for (int k = 0; k < Periods; k++)
        {
            var period = QuarterEnd(latest.AddMonths(-3 * k));
            rows.Add(new FinancialValue { Code = code, ReportDate = period, Key = FinancialKeys.Revenue, Value = 1e9 - k * 1e7 });
            rows.Add(new FinancialValue { Code = code, ReportDate = period, Key = FinancialKeys.OperCost, Value = 7e8 - k * 7e6 });
            rows.Add(new FinancialValue { Code = code, ReportDate = period, Key = FinancialKeys.NetProfitParent, Value = 1e8 - k * 1e6 });
            rows.Add(new FinancialValue { Code = code, ReportDate = period, Key = FinancialKeys.TotalAssets, Value = 5e9 });
        }
        return Task.FromResult(rows);
    }

    /// <summary>落到季度末（6-30 往前推三个月是 3-30，要校成 3-31）。</summary>
    private static DateTime QuarterEnd(DateTime d)
    {
        int m = (d.Month + 2) / 3 * 3;
        return new DateTime(d.Year, m, DateTime.DaysInMonth(d.Year, m));
    }
}
