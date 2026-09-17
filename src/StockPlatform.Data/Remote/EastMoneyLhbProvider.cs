using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Abstractions;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 龙虎榜每日概要（东财 datacenter，报表 <c>RPT_DAILYBILLBOARD_DETAILSNEW</c>）——2026-09-09
/// 从 <see cref="SinaLhbProvider"/> 切过来，两者并存、由 <c>fetcher-settings.json</c> 的
/// <c>LhbSource</c> 决定用哪个。
///
/// ════ 为什么换（换之前逐条比对过）════
/// 2026-09-08 单日：两源票集 55 vs 55 **双向零差异**，收盘价/简称 55 只全对上，成交额换算比值
/// 精确 1.000000（新浪万元 × 1e4 ＝ 东财 ACCUM_AMOUNT 元）。数据起点也同为 2004-06-25。
/// 换的理由不是"数据更多"，而是**口径**：东财给的上榜原因是交易所原文，跟
/// <c>LhbSeat.explanation</c> 同源，两张表因此能按 (日期,代码,原因) join——
/// "这张榜为什么上"和"这张榜是谁在买"以前根本对不起来。
///
/// 顺带修掉一个真错误：新浪把上榜原因**归并成粗类**，而"对应值"仍跟着各自的原规则走，
/// 于是同一个 reason 下混着语义不同的值（2026-08-04 创业板那批 reason 全是"涨幅偏离值达7%的
/// 证券"，对应值却分别是当日涨跌幅 20.0、两日累计 39.8、多日累计 31.54）。
///
/// ════ 两个入口，别用错 ════
///   · <see cref="GetDailyAsync"/>：日常增量，一天一个请求，实现 <see cref="ILhbProvider"/>。
///   · <see cref="FetchSlicesAsync"/>：整段回补，按月切片流式产出。
/// 全量 2004-06-25 至今是 26.8 万行：走逐日入口要 5300 个请求约 1.8 小时，走月片只要
/// 约 580 个、十几分钟。所以回补**必须**走后者——这也是这个 provider 比接口多长出一个方法的原因。
///
/// ════ 东财这张表没有的两列 ════
/// "对应值"(deviation) 和"成交量"(volume) 接口里根本不存在——它只给 CHANGE_RATE 和
/// TURNOVERRATE。这两列由 <c>LhbDeviationDeriver</c> 用本地日K补，本 provider 一律留 null，
/// 不在这里瞎填。
/// </summary>
public class EastMoneyLhbProvider : ILhbProvider, ILhbRangeProvider
{
    /// <summary>东财龙虎榜每日详情。名字末尾的 NEW 是东财自己的（老表 RPT_DAILYBILLBOARD_DETAILS
    /// 已经不返回近年数据）。</summary>
    private const string Report = "RPT_DAILYBILLBOARD_DETAILSNEW";

    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    /// <summary>2004-06-25——东财这张表自报的第一天（按 TRADE_DATE 升序取首行实测）。
    /// 跟本地 Lhb 表最早的一天完全相同，所以切源不会丢历史。
    /// ⚠ 比 <see cref="SinaLhbProvider.EarliestAvailable"/> 声称的 2002-01-01 晚两年半，
    /// 但那个是"制度起点"的保守猜测，实际新浪也一行都没给出过 2004-06-25 之前的数据。</summary>
    public DateOnly EarliestAvailable => new(2004, 6, 25);

    public EastMoneyLhbProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>抓某一个交易日。非交易日/当天还没发布都返回空列表——跟新浪那版的语义一致，
    /// 调用方据此把这天记进 DailyFetchNoData。</summary>
    public async Task<List<LhbRow>> GetDailyAsync(DateOnly date, CancellationToken ct = default)
    {
        var d = date.ToDateTime(TimeOnly.MinValue);
        var rows = new List<LhbRow>();
        await foreach (var el in QueryAsync(d, d, ct)) rows.Add(el);
        return rows;
    }

    /// <summary>
    /// 整段回补：按月切片，**每抓完一个月 yield 一次**由调用方落库。
    ///
    /// 切月的理由跟 <see cref="EastMoneyLhbSeatProvider"/> 原来一样——全量 26.8 万行按 500/页是
    /// 536 页，东财翻到几百页就开始拒绝或极慢；切成月片后每片 1~3 页，翻页永远是浅的。
    ///
    /// ⚠ 这张表**不必**跟着席位表改按日：它的排序键
    /// <c>TRADE_DATE,SECURITY_CODE,EXPLANATION</c> 本来就唯一（见 <see cref="QueryAsync"/> 的注释），
    /// 深分页的跨页错位对它不成立；改按日反而让请求数从 580 涨到 5300。
    ///
    /// 中断时已落库的片有效，重跑从水位线接着走。
    /// </summary>
    public async IAsyncEnumerable<LhbSlice> FetchSlicesAsync(
        DateTime start, DateTime end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var slices = EastMoneyQuerySlicer.ByMonth(start, end);
        for (int i = 0; i < slices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = slices[i];

            var rows = new List<LhbRow>();
            await foreach (var row in QueryAsync(s.Start, s.End, ct)) rows.Add(row);

            yield return new LhbSlice(s.Start, s.End, s.Name, i + 1, slices.Count, rows);
        }
    }

    /// <summary>
    /// 一个日期区间的原始查询。
    ///
    /// ⚠ 排序键是 <c>TRADE_DATE,SECURITY_CODE,EXPLANATION</c> 三列，一列都不能少：东财翻页
    /// 靠排序定序，键不唯一时同键行在页与页之间的先后不保证，**会既重复又丢行**（个股题材表
    /// 上实测前 3 页 1500 行里重复 15 行、同时丢 15 行）。龙虎榜同一天同一只票可以有多个上榜
    /// 原因（2026-09-08 的 920371 就有 3 个），所以必须带上 EXPLANATION 才能定到唯一。
    /// </summary>
    private async IAsyncEnumerable<LhbRow> QueryAsync(
        DateTime start, DateTime end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var filter = EastMoneyQuerySlicer.DateFilter("TRADE_DATE", start, end);
        await foreach (var el in _dc.QueryAsync(Report, filter, "TRADE_DATE,SECURITY_CODE,EXPLANATION", ct: ct))
        {
            var row = Parse(el);
            if (row != null) yield return row;
        }
    }

    /// <summary>
    /// 一行 JSON → <see cref="LhbRow"/>。代码/日期/上榜原因缺任何一个都丢弃——它们是主键，
    /// 缺了写进去就是一条永远对不上、也删不掉的脏行。
    ///
    /// 故意是 public 的：换源的全部风险都在这个函数的口径上（成交额的元/万元、滞后字段的
    /// null、上榜原因取哪个字段），它得能被单元测试直接喂实抓 JSON 打，而不是隔着一层网络。
    /// </summary>
    public static LhbRow? Parse(JsonElement el)
    {
        var code = Str(el, "SECURITY_CODE");
        var date = Date(el, "TRADE_DATE");
        var reason = Str(el, "EXPLANATION");
        if (code.Length == 0 || date == null || reason.Length == 0) return null;

        return new LhbRow
        {
            TradeDate = date.Value,
            StockCode = code,
            StockName = Str(el, "SECURITY_NAME_ABBR"),
            ClosePrice = Num(el, "CLOSE_PRICE") ?? 0,
            Reason = reason,

            // 东财给的是元，历史行（新浪）存的是万元——统一到万元，否则同一列两个量纲。
            Amount = (Num(el, "ACCUM_AMOUNT") ?? 0) / 1e4,

            // 这两列东财没有，留给 LhbDeviationDeriver 用本地日K补
            Deviation = null,
            Volume = null,

            ChangeRate = Num(el, "CHANGE_RATE"),
            TurnoverRate = Num(el, "TURNOVERRATE"),
            FreeMarketCap = Num(el, "FREE_MARKET_CAP"),
            BillboardBuyAmt = Num(el, "BILLBOARD_BUY_AMT"),
            BillboardSellAmt = Num(el, "BILLBOARD_SELL_AMT"),
            BillboardNetAmt = Num(el, "BILLBOARD_NET_AMT"),
            BillboardDealAmt = Num(el, "BILLBOARD_DEAL_AMT"),
            DealAmountRatio = Num(el, "DEAL_AMOUNT_RATIO"),
            DealNetRatio = Num(el, "DEAL_NET_RATIO"),
            Explain = Str(el, "EXPLAIN"),
            TradeId = Str(el, "TRADE_ID"),
            ChangeType = Str(el, "CHANGE_TYPE"),
            TradeMarket = Str(el, "TRADE_MARKET"),

            D1Chg = Num(el, "D1_CLOSE_ADJCHRATE"),
            D2Chg = Num(el, "D2_CLOSE_ADJCHRATE"),
            D5Chg = Num(el, "D5_CLOSE_ADJCHRATE"),
            D10Chg = Num(el, "D10_CLOSE_ADJCHRATE"),
            D20Chg = Num(el, "D20_CLOSE_ADJCHRATE"),
            D30Chg = Num(el, "D30_CLOSE_ADJCHRATE"),

            Source = LhbSources.EastMoney,
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
        return DateTime.TryParseExact(s[..10], "yyyy-MM-dd",
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }
}
