using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Logic.Services;

/// <summary>
/// 东财分档资金流（<c>fflow/daykline</c>）响应的解析——**两条通道共用这一份**（2026-09-14）。
///
/// 为什么要抽出来：走 HttpClient 的 <c>EastMoneyMoneyFlowProvider</c> 和走真浏览器的
/// <c>ChromeCdpMoneyFlowFetcher</c> 打的是**同一个 URL、同一组 fields**，拿回来的 JSON
/// 一个字节都不差——差的只是"谁发的这个请求"。解析各写一份的话，哪天字段序变了就会出现
/// 一条通道对、另一条悄悄错的局面，而这两条通道写的是同一张表，对不上账最难查。
/// 等价性有测试盯着（MoneyFlowKlineParserTests）。
/// </summary>
public static class MoneyFlowKlineParser
{
    /// <summary>接口要哪些字段。两条通道必须用同一份，否则 klines 的分段含义就对不上了。</summary>
    public const string Fields1 = "f1,f2,f3,f7";

    public const string Fields2 = "f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61,f62,f63,f64,f65";

    /// <summary>一只票的完整请求 URL（不含 JSONP 的 <c>cb</c> 参数）。</summary>
    public static string BuildUrl(string host, string code) =>
        $"https://{host}/api/qt/stock/fflow/daykline/get?lmt=0&klt=101" +
        $"&secid={MarketClassifier.EastMoneySecIdPrefix(code)}{code}" +
        $"&fields1={Fields1}&fields2={Fields2}";

    /// <summary>
    /// 把响应体解析成行。<paramref name="json"/> 是接口原样的 JSON 文本
    /// （JSONP 那条通道由页面把回调参数 <c>JSON.stringify</c> 回来，形状完全一样）。
    ///
    /// 停牌/退市/没数据时接口回 <c>data:null</c>，这里返回空列表——**不是失败**，
    /// 调用方要把它跟"抓失败"分开计数（那 342 只北交所票的 secid 拼错就是靠这个数看出来的）。
    /// </summary>
    public static List<NetInflowDetail> Parse(string code, string json, DateTime fetchedAt)
    {
        var result = new List<NetInflowDetail>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object) return result;
        if (!data.TryGetProperty("klines", out var klines) ||
            klines.ValueKind != JsonValueKind.Array) return result;

        foreach (var line in klines.EnumerateArray())
        {
            var parts = (line.GetString() ?? "").Split(',');
            // f51..f63 共 13 个是有意义的（f64/f65 未用），少于 13 段的行直接跳过
            if (parts.Length < 13) continue;
            if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var day)) continue;

            result.Add(new NetInflowDetail
            {
                Code = code,
                TradeDate = day,
                MainNet = D(parts[1]),
                SmallNet = D(parts[2]),
                MidNet = D(parts[3]),
                BigNet = D(parts[4]),
                SuperNet = D(parts[5]),
                MainRatio = D(parts[6]),
                SmallRatio = D(parts[7]),
                MidRatio = D(parts[8]),
                BigRatio = D(parts[9]),
                SuperRatio = D(parts[10]),
                ClosePrice = D(parts[11]),
                ChangeRate = D(parts[12]),
                FetchedAt = fetchedAt,
            });
        }
        return result;
    }

    private static double? D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
