using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 龙虎榜营业部席位明细（东财 datacenter）。买方榜 131 万行 + 卖方榜 133 万行。
///
/// ════ 2026-09-17：从"按月切片"改成"按交易日" ════
/// 原来一次查一个月（40~60 页）。排序键 <c>TRADE_DATE,SECURITY_CODE</c> **不唯一**
/// （一天一只股有 5~10 行），而东财翻页靠排序定序——键不唯一时同键行在页与页之间的先后
/// 不保证，**既会重复又会丢行**。实测 2021-05 月片收到 6226 行里重复 14 行、
/// 同时丢掉 14 行真数据；全表 178 万行里这样的副本有 3579 行，缺的行一样多。
///
/// 改按日之后 5204 个"交易日 × 买卖侧"里有 4941 个**只有一页**，页边界根本不存在；
/// 同样的 2021-05 逐日抓 18 天，收到 6226 行、重复 0 行，且正好含有月片漏掉的那 14 行。
/// 剩下 263 个多页的日侧靠**排序键加长**兜（见 <see cref="SortColumns"/>）。
///
/// ⚠ 这个坑在 <c>EastMoneyLhbProvider.QueryAsync</c>（龙虎榜主表，2026-09-09 换源时写的）
/// 的注释里早就写明白了，只是 2026-09-03 写的这个 provider 没跟上。
///
/// 落库是**整日替换**（<c>ILhbSeatRepository.ReplaceForDay</c>），所以反复抓多少次结果都一样，
/// <see cref="LhbSeat.Seq"/> 这种位次型的主键末列也不会再堆出副本。
/// </summary>
public class EastMoneyLhbSeatProvider : ILhbSeatDayFetcher
{
    private const string BuyReport = "RPT_BILLBOARD_DAILYDETAILSBUY";
    private const string SellReport = "RPT_BILLBOARD_DAILYDETAILSSELL";

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyLhbSeatProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>
    /// 排序键。<b>一列都不能少</b>——东财翻页靠排序定序，键不唯一时同键行在页与页之间的
    /// 先后不保证，会既重复又丢行（见类注释里 2021-05 的实测）。
    ///
    /// 同一天同一只股可以有多张榜（<c>EXPLANATION</c>），同一张榜里可以有多个**匿名**机构席位
    /// （<c>OPERATEDEPT_CODE</c> 一律 "0"、名称一律"机构专用"，实测同榜同代码多行的有 5.4 万组），
    /// 所以定到唯一必须一路带到 <c>NET</c>。剩下的并列只有"席位和金额完全相同"的行，
    /// 那种行彼此可互换，跨页换位也不改变结果。
    ///
    /// 2024-02-07（单侧 3085 行 / 7 页，全表最大的一天）实测：两侧各 7 页收齐、内部重复 0。
    /// </summary>
    private const string SortColumns = "TRADE_DATE,SECURITY_CODE,EXPLANATION,NET,OPERATEDEPT_CODE";

    /// <summary>
    /// 抓某一个交易日的买卖两侧席位明细。
    ///
    /// ⚠ 两侧**都**要跟接口自报的 count 对上才算这一天完整（<see cref="LhbSeatDay.IsComplete"/>）——
    /// 落库是整日替换，只收到买方就落库等于把卖方永久删掉。
    /// 判断和处置交给调用方（<c>LhbSeatTask</c> 记残缺日、<c>LhbSeatDayWriter</c> 直接抛）。
    ///
    /// <see cref="LhbSeat.Seq"/> 在**整天两侧都收齐之后**统一自赋（见 <see cref="AssignSeq"/>）。
    /// </summary>
    public async Task<LhbSeatDay> FetchLhbSeatsOfDayAsync(DateTime day, CancellationToken ct = default)
    {
        var filter = EastMoneyQuerySlicer.DateFilter("TRADE_DATE", day.Date, day.Date);
        var rows = new List<LhbSeat>();
        int reportedBuy = -1, reportedSell = -1;

        foreach (var (report, isBuy) in new[] { (BuyReport, true), (SellReport, false) })
        {
            ct.ThrowIfCancellationRequested();
            int reported = -1;
            await foreach (var el in _dc.QueryAsync(
                report, filter, SortColumns, onTotalCount: n => reported = n, ct: ct))
            {
                var row = Parse(el, isBuy);
                if (row != null) rows.Add(row);
            }
            // 那天那一侧一行都没有时接口连 result 都不给（走 yield break），拿不到 count——
            // 记 0，跟"抓到 0 行"一致，调用方才能认成"数据源就是没有"而不是"抓漏了"。
            if (reported < 0) reported = 0;
            if (isBuy) reportedBuy = reported; else reportedSell = reported;
        }

        AssignSeq(rows);
        return new LhbSeatDay(day.Date, rows, reportedBuy, reportedSell);
    }

    /// <summary>
    /// 给同一张榜里的行编位次。分组键是"哪天+哪只股+买还是卖+哪张榜"，组内按净额降序。
    ///
    /// 按净额而不是按接口返回顺序，是为了让同一天两次抓出来的编号一致，看日志时对得上。
    ///
    /// ⚠ 但**幂等不是靠它保证的**，是靠整日替换（<c>ILhbSeatRepository.ReplaceForDay</c>）。
    /// seq 是位次，取决于"这一组里有几行"：只要某次抓取多收了一行（跨页重复），整组编号就多
    /// 一位，上一次落库的高位 seq 行没人覆盖得掉，永久留在库里——2026-09-17 之前正是这么
    /// 堆出 3579 行副本的。改整日替换之后这条路彻底断了。
    /// </summary>
    public static void AssignSeq(List<LhbSeat> rows)
    {
        foreach (var g in rows.GroupBy(r => (r.TradeDate, r.Code, r.IsBuy, r.Explanation)))
        {
            int seq = 0;
            // ThenBy(Buy) 让净额相同时也有确定顺序；OrderByDescending 对 null 的处理是排在最后
            foreach (var r in g.OrderByDescending(r => r.Net ?? double.MinValue)
                                .ThenByDescending(r => r.Buy ?? 0)
                                .ThenBy(r => r.SeatCode, StringComparer.Ordinal))
                r.Seq = seq++;
        }
    }

    private static LhbSeat? Parse(JsonElement el, bool isBuy)
    {
        var code = Str(el, "SECURITY_CODE");
        var date = Date(el, "TRADE_DATE");
        var seat = Str(el, "OPERATEDEPT_CODE");
        if (code.Length == 0 || date == null) return null;

        return new LhbSeat
        {
            Code = code,
            Name = Str(el, "SECURITY_NAME_ABBR"),
            TradeDate = date.Value,
            IsBuy = isBuy,
            SeatCode = seat,
            SeatName = Str(el, "OPERATEDEPT_NAME"),
            Buy = Num(el, "BUY"),
            Sell = Num(el, "SELL"),
            Net = Num(el, "NET"),
            Explanation = Str(el, "EXPLANATION"),
            RiseProbability3Day = Num(el, "RISE_PROBABILITY_3DAY"),
            Times3Day = (int?)Num(el, "TOTAL_BUYER_SALESTIMES_3DAY"),
            TradeId = Str(el, "TRADE_ID"),
            ClosePrice = Num(el, "CLOSE_PRICE"),
            ChangeRate = Num(el, "CHANGE_RATE"),
            BuyRatio = Num(el, "TOTAL_BUYRIO"),
            SellRatio = Num(el, "TOTAL_SELLRIO"),
            ChangeType = Str(el, "CHANGE_TYPE"),
            FetchedAt = DateTime.Now,
        };
    }

    private static string Str(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    private static double? Num(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float,
                                                    CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null,
        };
    }

    private static DateTime? Date(JsonElement el, string prop)
    {
        var s = Str(el, prop);
        if (s.Length < 10) return null;
        return DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}
