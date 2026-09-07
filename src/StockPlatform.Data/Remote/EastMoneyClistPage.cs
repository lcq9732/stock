using System.Text.Json;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 东财 <c>clist/get</c> 一页响应的解析（2026-09-05 抽出来）。
///
/// 为什么单独放在这一层：它是**操作页面**那条通道（<see cref="EastMoneyBoardPageFetcher"/>，
/// 实现在 Desktop 层的 WebView2 里）截下来的响应的解析入口，而那一层是 WPF、测试项目引不动。
/// 剥壳剥错、字段取错都是**静默丢数据**——不会报错，只会让名单少几只，
/// 所以这段必须待在能被单元测试盯住的地方。
///
/// ════ 为什么要剥壳 ════
/// 页面自己发的是 JSONP（<c>&lt;script src&gt;</c> 带 <c>cb=jQuery371..._178...</c>），
/// 所以响应体长这样：
/// <code>jQuery37103340495740046191_1788618938368({"rc":0,"data":{...}});</code>
/// 而我们自己拼 URL 时不带 <c>cb</c>，拿回来的是裸 JSON。两种都要认。
/// </summary>
public static class EastMoneyClistPage
{
    /// <summary>
    /// 解析一页。返回接口自报的 <c>total</c> 和这一页的股票代码（<c>f12</c>）。
    ///
    /// 解析不了（空串、不是 JSON、被截断）返回 <c>null</c> —— 调用方据此重试或让 total 对账去拦，
    /// **不能当成"这一页是空的"**，那会把半截名单当完整的写进库。
    /// <c>data</c> 为 null 是限流的一种表现，但也可能真是空板块，所以返回 total=0 的空页，
    /// 由上游的对账逻辑决定怎么处理。
    /// </summary>
    public static (int Total, List<string> Codes)? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var json = body.Trim();
        if (!json.StartsWith('{'))
        {
            // JSONP：取第一个 '(' 到**最后一个** ')' 之间。用 LastIndexOf 而不是 IndexOf，
            // 因为 JSON 内容里可能有括号（板块名、股票名都可能带）。
            int open = json.IndexOf('(');
            int close = json.LastIndexOf(')');
            if (open < 0 || close <= open) return null;
            json = json[(open + 1)..close];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return (0, []);

            int total = data.TryGetProperty("total", out var t) && t.TryGetInt32(out var tv) ? tv : 0;

            var codes = new List<string>();
            if (data.TryGetProperty("diff", out var diff) && diff.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in diff.EnumerateArray())
                {
                    // f12 有时是字符串有时是数字（东财老毛病），两种都要认；
                    // 数字形态还会把 000001 这种前导零吃掉，所以补回 6 位。
                    if (!item.TryGetProperty("f12", out var f12)) continue;
                    var c = f12.ValueKind == JsonValueKind.String ? f12.GetString() : f12.ToString();
                    if (string.IsNullOrWhiteSpace(c)) continue;
                    if (f12.ValueKind == JsonValueKind.Number && c!.Length < 6) c = c.PadLeft(6, '0');
                    codes.Add(c!);
                }
            }
            return (total, codes);
        }
        catch (JsonException) { return null; }
    }
}
