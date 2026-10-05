using Microsoft.Data.Sqlite;
using StockPlatform.Logic.Models;
using StockPlatform.Logic.Services;

namespace StockPlatform.Data.Sqlite;

/// <summary>
/// 给一批 Bar 代码，查它们各自哪些交易日**全天停牌**（2026-09-30，见 doc/suspension-design.md）。
/// 读 Suspension 表 + 交易日历 + 这几只的日K日期，判据交给 <see cref="SuspensionRule"/>。
///
/// 两个调用方：【当日完整性体检】（当天缺日线的是不是停牌）、【全库数据体检】（历史空洞是不是停牌）。
/// 同一判据只此一份——这个项目在"两处各写一份"上栽过（见 <see cref="SqliteAdjSeriesAuditor"/> 的由来）。
///
/// ⚠ 表还没建、一条记录都没有、查询出错，一律当"不知道有没有停牌"＝不排除任何日子——
///   那正是没有这个功能之前的行为，不会比以前更糟。
/// </summary>
public sealed class SqliteSuspensionLookup(string dbPath)
{
    private List<DateOnly>? _calendar;

    /// <param name="barCodes">Bar 表里的代码（个股裸码、ETF 带前缀）。</param>
    /// <param name="granularity">拿哪条日K当"复牌了没有"的依据（停牌后第一根日K＝已复牌）。</param>
    /// <param name="asOf">还在停牌的算到这天（含）。</param>
    /// <returns>只含**有**停牌日的代码。</returns>
    public Dictionary<string, HashSet<DateOnly>> FullDaysOf(
        IReadOnlyCollection<string> barCodes, string granularity, DateOnly asOf)
    {
        var result = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
        if (barCodes.Count == 0) return result;
        try
        {
            var rows = new SqliteSuspensionRepository(dbPath).GetByBarCodes(barCodes);
            if (rows.Count == 0) return result;

            _calendar ??= new SqliteTradingDayRepository(dbPath).GetAll()
                .Select(DateOnly.FromDateTime).Distinct().OrderBy(d => d).ToList();

            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            foreach (var (code, list) in rows)
            {
                var days = SuspensionRule.FullDays(list, _calendar, asOf, BarDays(conn, code, granularity));
                if (days.Count > 0) result[code] = days;
            }
        }
        catch (SqliteException) { return new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal); }
        return result;
    }

    /// <summary>一只票某个口径有日K的日子，升序（走主键前缀，很快）。</summary>
    private static List<DateOnly> BarDays(SqliteConnection conn, string code, string granularity)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT period_start FROM Bar WHERE code = $c AND granularity = $g ORDER BY period_start;";
        cmd.Parameters.AddWithValue("$c", code);
        cmd.Parameters.AddWithValue("$g", granularity);
        var list = new List<DateOnly>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (DateTime.TryParse(r.GetString(0), out var d)) list.Add(DateOnly.FromDateTime(d));
        return list;
    }
}
