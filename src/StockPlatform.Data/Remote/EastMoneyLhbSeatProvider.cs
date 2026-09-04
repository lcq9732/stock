using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 龙虎榜营业部席位明细（东财 datacenter）。买方榜 131 万行 + 卖方榜 133 万行，是本次接入的
/// 数据里最大的一块，所以是唯一走**流式回调**而不是"返回一个大 List"的 provider：
/// 264 万行全装进内存再写库，光对象开销就是几百 MB，而且中途失败前面全白抓。
///
/// 改成按片回调之后：每抓完一个月就交给调用方落库，中断时已经落库的部分是有效的，
/// 重跑从本地水位线接着走即可。
/// </summary>
public class EastMoneyLhbSeatProvider
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
    /// 抓买卖双方席位明细，<b>每抓完一个月就回调一次</b>由调用方落库。
    ///
    /// 回调粒度是"一片(一个月)"而不是"每 N 行"，因为 <see cref="LhbSeat.Seq"/> 要在**整片就位后**
    /// 才能算：同一张榜里的多个匿名机构席位要按净额降序编位次，行没收齐就编不出稳定的号。
    /// 一个月约 1.5 万行，内存毫无压力；而全量 264 万行一次性装内存才是真问题——这也正是
    /// 不把整段数据攒完再返回的原因。断点粒度也因此天然是"月"。
    /// </summary>
    /// <param name="onBatch">一片数据就绪时的回调，返回值是实际写入行数。</param>
    /// <returns>总共抓到并回调出去的行数。</returns>
    public async Task<int> FetchAsync(
        DateTime start, DateTime end, Func<List<LhbSeat>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int total = 0;
        // 按月切片：全量 2620 页，东财翻到上千页会拒绝；月片下每片 40 页左右，翻页永远是浅的
        var slices = EastMoneyQuerySlicer.ByMonth(start, end);

        // ⚠ 循环嵌套顺序是**外层月份、内层买卖**，不能反过来。
        //
        // 反过来（先抓完所有月份的买方、再抓卖方）会在中断时丢数据：调用方的水位线是
        // MAX(trade_date)、不区分买卖，买方一路抓到最新月时水位线就已经推到今天了；
        // 此时中断，下一轮从今天开始抓，**卖方的历史就永远补不回来**。
        // 首轮要跑几个小时，中断是常态，这个顺序错了迟早会踩上。
        //
        // 现在这样：某个月的买卖两边都落库了，水位线才会推过这个月。
        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            int inSlice = 0;

            foreach (var (report, isBuy, label) in new[]
                     {
                         (BuyReport, true, "买方"),
                         (SellReport, false, "卖方"),
                     })
            {
                ct.ThrowIfCancellationRequested();

                var filter = EastMoneyQuerySlicer.DateFilter("TRADE_DATE", s.Start, s.End);
                var rows = new List<LhbSeat>();

                await foreach (var el in _dc.QueryAsync(report, filter, "TRADE_DATE,SECURITY_CODE", ct: ct))
                {
                    var row = Parse(el, isBuy);
                    if (row != null) rows.Add(row);
                }

                AssignSeq(rows);
                int written = rows.Count > 0 ? onBatch(rows) : 0;
                inSlice += written;
                total += written;
            }

            if (inSlice > 0 || i % 12 == 0)
                progress?.Report($"龙虎榜席位 {s.Name}（{i + 1}/{slices.Count}）：" +
                                 $"本月买卖共 {inSlice} 行，累计 {total} 行");
        }
        return total;
    }

    /// <summary>
    /// 给同一张榜里的行编位次。分组键是"哪天+哪只股+买还是卖+哪张榜"，组内按净额降序。
    ///
    /// 按净额而不是按接口返回顺序，是为了幂等——接口返回顺序不保证稳定，跟着它编号的话
    /// 重抓一次就会产生一批 seq 不同的新行，同一天的数据在库里翻倍。
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
