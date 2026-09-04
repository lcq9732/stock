using System.Globalization;
using System.Text.Json;
using StockPlatform.Logic.Models;

namespace StockPlatform.Data.Remote;

/// <summary>
/// 四类"市场事件"数据（东财 datacenter）：大宗交易、机构调研、限售解禁、股东增减持。
/// 本地此前全都没有，也都没有回退源。
///
/// 放一个 provider 里是因为它们同构——都是 datacenter 报表，差别只在 reportName、
/// 日期字段和字段映射。分成四个类只会把同一套分片/翻页/解析逻辑抄四遍。
///
/// 切片粒度按数据量给：大宗交易 68 万行按月切，其余几万到几十万行按年切即可
/// （切太细会产生大量小请求，反而更容易撞限流）。
/// </summary>
public class EastMoneyMarketEventProvider
{
    private readonly EastMoneyDataCenterClient _dc;

    public event Action<string>? OnStatus;

    public EastMoneyMarketEventProvider(EastMoneyDataCenterClient dc)
    {
        _dc = dc;
        _dc.OnStatus += s => OnStatus?.Invoke(s);
    }

    /// <summary>大宗交易。约 68 万行，按月切片。</summary>
    public async Task<int> FetchBlockTradesAsync(
        DateTime start, DateTime end, Func<List<BlockTrade>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await RunSlicedAsync(
            "RPT_DATA_BLOCKTRADE", "TRADE_DATE", EastMoneyQuerySlicer.ByMonth(start, end),
            "大宗交易", el =>
            {
                var code = Str(el, "SECURITY_CODE");
                var d = Date(el, "TRADE_DATE");
                if (code.Length == 0 || d == null) return null;
                return new BlockTrade
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    TradeDate = d.Value,
                    DailyRank = (int)(Num(el, "DAILY_RANK") ?? 0),
                    DealPrice = Num(el, "DEAL_PRICE"),
                    DealVolume = Num(el, "DEAL_VOLUME"),
                    DealAmount = Num(el, "DEAL_AMT"),
                    PremiumRatio = Num(el, "PREMIUM_RATIO"),
                    ClosePrice = Num(el, "CLOSE_PRICE"),
                    ChangeRate = Num(el, "CHANGE_RATE"),
                    TurnoverRate = Num(el, "TURNOVER_RATE"),
                    BuyerName = Str(el, "BUYER_NAME"),
                    SellerName = Str(el, "SELLER_NAME"),
                    FetchedAt = DateTime.Now,
                };
            }, onBatch, progress, ct);

    /// <summary>机构调研。约 28 万行，按年切片。</summary>
    public async Task<int> FetchOrgSurveysAsync(
        DateTime start, DateTime end, Func<List<OrgSurvey>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await RunSlicedAsync(
            "RPT_ORG_SURVEYNEW", "NOTICE_DATE", EastMoneyQuerySlicer.ByYear(start, end),
            "机构调研", el =>
            {
                var code = Str(el, "SECURITY_CODE");
                var d = Date(el, "NOTICE_DATE");
                if (code.Length == 0 || d == null) return null;
                return new OrgSurvey
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    NoticeDate = d.Value,
                    ReceiveStartDate = Date(el, "RECEIVE_START_DATE"),
                    ReceiveEndDate = Date(el, "RECEIVE_END_DATE"),
                    // ⚠ 是 RECEIVE_OBJECT 不是 ORG_NAME——后者是**被调研的上市公司全名**，
                    // 同一次调研的所有行都一样，拿它做主键会 95% 撞车（实测 10280 行只剩 506 行）。
                    // RECEIVE_OBJECT 才是参与调研的机构（"同犇投资""华能贵诚"…）。
                    OrgName = Str(el, "RECEIVE_OBJECT"),
                    ObjectCode = Str(el, "OBJECT_CODE"),
                    OrgType = Str(el, "ORG_TYPE"),
                    ReceiveWay = Str(el, "RECEIVE_WAY_EXPLAIN"),
                    ReceivePlace = Str(el, "RECEIVE_PLACE"),
                    Investigators = Str(el, "INVESTIGATORS"),
                    Receptionist = Str(el, "RECEPTIONIST"),
                    SurveyNo = (int)(Num(el, "NUM") ?? 0),
                    OrgTotal = (int?)Num(el, "SUM"),
                    FetchedAt = DateTime.Now,
                };
            }, onBatch, progress, ct);

    /// <summary>
    /// 限售解禁。⚠ <b>含未来的解禁计划</b>（样例里有 2035 年的），所以不做日期切片、
    /// 每次全量重取——按"抓到今天为止"做增量会永远漏掉未来那部分，而未来正是它的价值所在。
    /// 好在只有 3 万行、63 页。
    /// </summary>
    /// <param name="fromYear">起始年份（含）。默认 2010。</param>
    /// <param name="toYear">
    /// 截止年份（含）。默认"今年+15"——**必须给未来留足余量**，这张表的价值恰恰在未来那部分
    /// （实测样例里有 2035 年的解禁计划）。测试可以传小范围避免跑满 30 多片。
    /// </param>
    public async Task<int> FetchShareLiftsAsync(
        Func<List<ShareLift>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default,
        int? fromYear = null, int? toYear = null)
    {
        // 按年切片，范围覆盖"历史 + 未来的解禁计划"。
        //
        // 为什么切片：这张表原本是唯一不切片的（全量 3 万行、63 页一路翻到底），结果撞上了
        // 东财的深分页问题——实测有 0.1~0.2% 的跨页重复（同一条记录在两页里各出现一次）。
        // 前两页查不出来，重复都发生在几十页之后。按年切片后每片只有两三页，永远是浅分页。
        //
        // 上界取"今年+15年"而不是今天：这张表的价值恰恰在未来那部分（实测样例里有 2035 年的
        // 解禁计划），按"抓到今天为止"会把它全漏掉。
        var start = new DateTime(fromYear ?? 2010, 1, 1);
        var end = new DateTime(toYear ?? (DateTime.Today.Year + 15), 12, 31);
        var slices = EastMoneyQuerySlicer.ByYear(start, end);

        progress?.Report($"限售解禁：全量重取 {start:yyyy}~{end:yyyy}（含未来解禁计划，" +
                         $"不能按日期做增量），{slices.Count} 片...");

        int total = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            ct.ThrowIfCancellationRequested();
            var filter = EastMoneyQuerySlicer.DateFilter("FREE_DATE", s.Start, s.End);
            var rows = new List<ShareLift>();
            await foreach (var el in _dc.QueryAsync("RPT_LIFT_STAGE", filter, "FREE_DATE,SECURITY_CODE", ct: ct))
            {
                var code = Str(el, "SECURITY_CODE");
                var d = Date(el, "FREE_DATE");
                if (code.Length == 0 || d == null) continue;
                rows.Add(new ShareLift
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    FreeDate = d.Value,
                    ShareType = Str(el, "FREE_SHARES_TYPE"),
                    LiftShares = Num(el, "CURRENT_FREE_SHARES"),
                    LiftMarketCap = Num(el, "LIFT_MARKET_CAP"),
                    FreeRatio = Num(el, "FREE_RATIO"),
                    TotalRatio = Num(el, "TOTALSHARES_RATIO") ?? Num(el, "TOTAL_RATIO"),
                    HolderCount = (int?)Num(el, "BATCH_HOLDER_NUM"),
                    FetchedAt = DateTime.Now,
                });
            }
            int written = rows.Count > 0 ? onBatch(rows) : 0;
            total += written;
            if (written > 0 || i % 10 == 0)
                progress?.Report($"限售解禁 {s.Name}（{i + 1}/{slices.Count}）：{written} 行，累计 {total}");
        }
        progress?.Report($"限售解禁完成：{total} 行");
        return total;
    }

    /// <summary>股东增减持。约 15 万行，按年切片。</summary>
    public async Task<int> FetchHolderChangesAsync(
        DateTime start, DateTime end, Func<List<HolderChange>, int> onBatch,
        IProgress<string>? progress = null, CancellationToken ct = default)
        => await RunSlicedAsync(
            "RPT_SHARE_HOLDER_INCREASE", "NOTICE_DATE", EastMoneyQuerySlicer.ByYear(start, end),
            "股东增减持", el =>
            {
                var code = Str(el, "SECURITY_CODE");
                var d = Date(el, "NOTICE_DATE");
                if (code.Length == 0 || d == null) return null;
                return new HolderChange
                {
                    Code = code,
                    Name = Str(el, "SECURITY_NAME_ABBR"),
                    NoticeDate = d.Value,
                    HolderName = Str(el, "HOLDER_NAME"),
                    Direction = Str(el, "DIRECTION"),
                    ChangeShares = Num(el, "CHANGE_NUM_SYMBOL") ?? Num(el, "CHANGE_NUM"),
                    ChangeRatio = Num(el, "CHANGE_RATE"),
                    AfterShares = Num(el, "AFTER_HOLDER_NUM"),
                    AfterRatio = Num(el, "HOLD_RATIO"),
                    StartDate = Date(el, "START_DATE"),
                    EndDate = Date(el, "END_DATE"),
                    AveragePrice = Num(el, "TRADE_AVERAGE_PRICE"),
                    FetchedAt = DateTime.Now,
                };
            }, onBatch, progress, ct);

    /// <summary>分片抓取的公共骨架：切片 → 翻页 → 解析 → 整片回调落库。</summary>
    private async Task<int> RunSlicedAsync<T>(
        string report, string dateField, List<EastMoneyQuerySlicer.Slice> slices, string label,
        Func<JsonElement, T?> parse, Func<List<T>, int> onBatch,
        IProgress<string>? progress, CancellationToken ct) where T : class
    {
        int total = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            ct.ThrowIfCancellationRequested();
            var filter = EastMoneyQuerySlicer.DateFilter(dateField, s.Start, s.End);
            var rows = new List<T>();
            await foreach (var el in _dc.QueryAsync(report, filter, dateField + ",SECURITY_CODE", ct: ct))
            {
                var row = parse(el);
                if (row != null) rows.Add(row);
            }
            int written = rows.Count > 0 ? onBatch(rows) : 0;
            total += written;
            if (written > 0 || i % 12 == 0)
                progress?.Report($"{label} {s.Name}（{i + 1}/{slices.Count}）：本片 {written} 行，累计 {total}");
        }
        return total;
    }

    // ── JSON 取值 ──────────────────────────────────────────────────
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
