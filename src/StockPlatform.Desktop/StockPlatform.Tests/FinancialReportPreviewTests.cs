using StockPlatform.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;
using Xunit;
using Xunit.Abstractions;

namespace StockPlatform.Tests;

/// <summary>
/// 把**产品代码生成的**完整财务分析打印出来（2026-09-14）。
///
/// 讨论"往这份报告里加一行"之前，得先看清楚它现在长什么样——手工用 python 重写一遍
/// 几百行判定逻辑，看到的是我的复刻品不是真东西，讨论就建立在假的基础上。
/// 这里直接调 FinancialAnalyzer，装配跟 FinancialAnalysisWindow.Open 一模一样。
///
/// ⚠ 只读本地库，设 REPORT_PREVIEW=1 才跑。
/// </summary>
public class FinancialReportPreviewTests
{
    private const string Db = @"C:\Chingli\Git\stock\publish\data\local\current.sqlite";

    private readonly ITestOutputHelper _out;
    public FinancialReportPreviewTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void 打印完整财务分析()
    {
        if (Environment.GetEnvironmentVariable("REPORT_PREVIEW") != "1")
        {
            _out.WriteLine("未设 REPORT_PREVIEW=1，跳过。");
            return;
        }
        if (!File.Exists(Db)) { _out.WriteLine("本机没有库，跳过。"); return; }

        var codes = (Environment.GetEnvironmentVariable("REPORT_CODES") ?? "600426")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries);

        var fin = new SqliteFinancialRepository(Db);
        var bars = new SqliteBarRepository(Db);
        var divs = new SqliteDividendRepository(Db);
        var trailing = divs.GetTrailingCashDividendPerShare(DateTime.Today.AddYears(-1));
        var names = SqliteStockMetaUpsert.GetAll(Db)
                        .ToDictionary(x => x.Code, x => x.Name, StringComparer.Ordinal);

        foreach (var code in codes)
        {
            var history = fin.GetAllByCode(code);
            names.TryGetValue(code, out var name);

            double? price = null;
            var b = bars.Query(code, Granularity.Day);
            if (b.Count > 0) price = b[^1].Close;

            double? dps = trailing.TryGetValue(code, out var d) && d > 0 ? d : null;

            var report = new FinancialAnalyzer().Analyze(code, name ?? code, history, price, dps);

            _out.WriteLine(new string('═', 78));
            _out.WriteLine($"{code} {name}   期别 {report.PeriodName}   收盘 {price:F2}");
            _out.WriteLine(new string('═', 78));
            if (!string.IsNullOrEmpty(report.Error)) { _out.WriteLine("✗ " + report.Error); continue; }
            if (!string.IsNullOrEmpty(report.Headline)) _out.WriteLine(report.Headline + "\n");

            foreach (var sec in report.Sections)
            {
                _out.WriteLine($"【{sec.Title}】");
                foreach (var line in sec.Lines)
                {
                    var v = string.IsNullOrEmpty(line.Change) ? line.Value : $"{line.Value}   ({line.Change})";
                    _out.WriteLine($"    {Mark(line.Verdict)} {line.Label,-22} {v}");
                    if (!string.IsNullOrEmpty(line.Reference))
                        _out.WriteLine($"         参考：{line.Reference}");
                    if (!string.IsNullOrEmpty(line.Note))
                        _out.WriteLine($"         → {line.Note}");
                }
                if (!string.IsNullOrEmpty(sec.Conclusion))
                    _out.WriteLine($"    ⇒ {sec.Conclusion}");
                _out.WriteLine("");
            }
        }
    }

    private static string Mark(Verdict v) => v switch
    {
        Verdict.Good => "✓",
        Verdict.Warn => "!",
        Verdict.Bad => "✗",
        Verdict.Missing => "?",
        _ => " ",
    };
}
