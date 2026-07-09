using System.Text.RegularExpressions;

namespace StockPlatform.Logic.Services;

/// <summary>
/// Best-effort extraction of a headline contract/order-win amount from an announcement's plain
/// text body (see doc: 中标/订单公告 抓取方案). Announcement bodies are unstructured prose/tables,
/// not JSON, so this only handles the common pattern of a "合计"(total) line carrying a
/// 亿元/万元/元-denominated figure — e.g. "项目金额合计 190.9"（亿元）— and deliberately does NOT
/// sum individual line items itself, since a missed or double-matched line would silently corrupt
/// the total. When no total line is found, callers should treat the amount as unknown, not zero.
/// </summary>
public static class OrderWinExtractor
{
    // Captures a number immediately followed by a unit, optionally with the unit appearing later
    // on the same line (as in EastMoney's "项目金额（亿元） ... 190.9" table layout) — so the
    // pattern is applied per matching line with the unit resolved separately, not embedded here.
    private static readonly Regex AmountRegex = new(@"(\d+(?:\.\d+)?)\s*(亿元|万元)", RegexOptions.Compiled);
    private static readonly Regex TotalLineRegex = new(@"合\s*计", RegexOptions.Compiled);

    /// <summary>Returns the amount (in yuan) from the first line containing "合计" that also
    /// carries a 亿元/万元 figure, or null if no such line exists.</summary>
    public static double? ExtractTotalAmountYuan(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        foreach (var line in content.Split('\n'))
        {
            if (!TotalLineRegex.IsMatch(line)) continue;
            var match = AmountRegex.Match(line);
            if (!match.Success) continue;

            var value = double.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value;
            return unit == "亿元" ? value * 1e8 : value * 1e4;
        }
        return null;
    }
}
