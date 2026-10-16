using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财新股发行表 <c>RPTA_APP_IPOAPPLY</c>（2026-10-16，见 doc/ipo-issue-design.md）——
/// 发行价、上市日、申购日、中签率、发行市盈率、募资额、保荐机构、上市首日表现……共 151 列。
///
/// ════ 接口行为（2026-10-16 沙箱实测）════
/// · 全量 5,640 行、一个代码一行（沪深主板/创业板/科创板 + 北交所 358 只），1990 年上市的老股也在；
///   **不含退市股**（名册里 338 只退市股只查到 2 只）、不含吸收合并/换股上市的（600023、000333 等 24 只没有首发）。
/// · 还没上市的新股也在（LISTING_DATE 为空，发行价已有），上市后才补上上市日和首日数据——
///   所以日常增量按申购日回看一年，覆盖"申购→上市"这一段。
/// · 按 SECURITY_CODE 排序能唯一定序（一个代码一行），500 一页，全量 12 页。
/// · 上市日跟本地第一根日K逐只比：5,520 一致、33 不一致——不一致的是**本地缺了上市头几周的K线**
///   （301371 敷尔佳等 2023-08 上市的创业板股），不是接口错。
/// </summary>
public sealed class EastMoneyIpoIssueProvider(EastMoneyDataCenterClient dc) : IIpoIssueProvider
{
    public const string ReportName = "RPTA_APP_IPOAPPLY";

    private static readonly Dictionary<string, IpoColumn> Catalog =
        IpoIssueColumns.All.ToDictionary(c => c.Api, StringComparer.Ordinal);

    private static readonly Regex MidnightStamp = new(@"^(\d{4}-\d{2}-\d{2}) 00:00:00$", RegexOptions.Compiled);

    public event Action<string>? OnStatus
    {
        add => dc.OnStatus += value;
        remove => dc.OnStatus -= value;
    }

    public async IAsyncEnumerable<IReadOnlyList<IpoIssueRow>> FetchAsync(
        DateOnly? applySince, IpoFetchStats stats, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = applySince is { } d ? $"(APPLY_DATE>='{d:yyyy-MM-dd}')" : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<IpoIssueRow>(EastMoneyDataCenterClient.PageSize);

        await foreach (var item in dc.QueryAsync(ReportName, filter, sortColumns: "SECURITY_CODE", descending: false,
                           onTotalCount: n => stats.ReportedTotal = n, ct: ct))
        {
            if (Parse(item, stats, seen) is not { } row) continue;
            stats.Fetched++;
            batch.Add(row);
            if (batch.Count >= EastMoneyDataCenterClient.PageSize)
            {
                yield return batch;
                batch = new List<IpoIssueRow>(EastMoneyDataCenterClient.PageSize);
            }
        }
        if (batch.Count > 0) yield return batch;

        foreach (var c in Catalog.Keys.Where(k => !seen.Contains(k))) stats.MissingColumns.Add(c);
    }

    /// <summary>
    /// 一行 → <see cref="IpoIssueRow"/>（公开给单测拿真实响应验）。没有代码的行返回 null。
    /// 数值列收到文本先试着按数解析，解析不了存空、记进 <see cref="IpoFetchStats.TypeMismatches"/>；
    /// 日期列 "yyyy-MM-dd 00:00:00" 存成 yyyy-MM-dd，带时刻的原样。
    /// </summary>
    public static IpoIssueRow? Parse(JsonElement item, IpoFetchStats? stats = null, HashSet<string>? seen = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in item.EnumerateObject())
        {
            if (!Catalog.TryGetValue(p.Name, out var col))
            {
                stats?.UnknownColumns.Add(p.Name);
                continue;
            }
            seen?.Add(p.Name);
            values[p.Name] = Value(p.Value, col, stats);
        }
        if (values.GetValueOrDefault(IpoIssueColumns.CodeColumn) is not string code || code.Length == 0) return null;
        return new IpoIssueRow { Code = code, Values = values };
    }

    private static object? Value(JsonElement v, IpoColumn col, IpoFetchStats? stats)
    {
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        switch (col.Kind)
        {
            case IpoColumnKind.Number:
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.String
                    && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                stats?.TypeMismatches.Add(col.Api);
                return null;
            case IpoColumnKind.Date:
                var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                var m = MidnightStamp.Match(s);
                return m.Success ? m.Groups[1].Value : s;
            default:
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        }
    }
}
